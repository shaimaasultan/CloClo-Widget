using System;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// Lets other programs of the same Windows user ask CloClo's language model
// about a text (Privacy -> "Let other apps ask about a text", off by default).
//
// Protocol: connect to \\.\pipe\CloClo.Ask in message mode, write one UTF-8
// JSON message {"text": "...", "question": "..."} and read one reply:
// {"answer": "..."} or {"error": "..."}. One question per connection.
//
// Guards:
// - Started only by the copy holding the PC-wide single-instance lock, so
//   there is one pipe for the one CloClo.
// - One server instance created with FILE_FLAG_FIRST_PIPE_INSTANCE (.NET sets
//   it for a single-instance pipe): if anything else already owns the name,
//   CloClo doesn't serve at all instead of sharing it with an impostor.
// - PipeOptions.CurrentUserOnly: only this Windows user can connect, and .NET
//   creates every server pipe with PIPE_REJECT_REMOTE_CLIENTS, so nothing
//   connects over the network.
// - Size and time limits on the request and the reply; one question at a
//   time, the next client waits for the pipe. A client that sends too much,
//   too slowly, or doesn't read its reply is disconnected, so none can hold
//   the pipe for long.
// - Only programs you allowed may ask. Each caller is identified by its
//   program file (PipeCaller: path plus signer, or path plus SHA-256 when
//   unsigned); an unknown one is refused unless you allow it in CloClo's
//   dialog, which then remembers it (Privacy -> Apps allowed to ask). A
//   program you turned down isn't asked about again until CloClo restarts.
// - The request is data. Its text and question go only into the model's
//   prompt; the answer goes only back to the caller. Nothing in either is run,
//   opened or typed anywhere, and CloClo says which program asked.
internal sealed class AskPipeServer : IDisposable
{
    public const string PipeName = "CloClo.Ask";
    // 30,000 characters even when every one is JSON-escaped (\u0627: 6 bytes).
    private const int MaxRequestBytes = 256 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromMinutes(3);

    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    // Called on a background thread: the asking program's name, and whether
    // it got an answer.
    private readonly Action<string, bool> _served;
    // Called once if the pipe can't be created (the name is taken).
    private readonly Action<string> _failed;
    // Whether this caller may ask: true only for an allowed program (asking
    // you first for a new one). Called on a background thread.
    private readonly Func<PipeCaller, CancellationToken, Task<bool>> _authorize;

    // The callbacks are set before the loop starts, so none is missed.
    private AskPipeServer(Func<PipeCaller, CancellationToken, Task<bool>> authorize, Action<string, bool> served, Action<string> failed)
    {
        _authorize = authorize;
        _served = served;
        _failed = failed;
        _loop = Task.Run(RunAsync);
    }

    public static AskPipeServer? Start(Func<PipeCaller, CancellationToken, Task<bool>> authorize,
        Action<string, bool> served, Action<string> failed)
    {
        if (Program.Instance?.IsHeld != true) return null;
        return new AskPipeServer(authorize, served, failed);
    }

    private async Task RunAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Message, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _failed("Another program already uses CloClo's pipe name, so other apps can't ask CloClo now.");
                return;
            }

            await using (pipe)
            {
                try
                {
                    await pipe.WaitForConnectionAsync(_stop.Token);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException) { continue; }

                // Identified while it is still connected (see PipeCaller).
                var caller = PipeCaller.Identify(pipe, out var refusal);
                var answered = false;
                try
                {
                    answered = await ServeAsync(pipe, caller, refusal);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client left or CloClo is closing.
                }
                if (!_stop.IsCancellationRequested) _served(caller?.FileName ?? "An unidentified program", answered);
            }
        }
    }

    private async Task<bool> ServeAsync(NamedPipeServerStream pipe, PipeCaller? caller, string? refusal)
    {
        AskRequest? request;
        using (var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token))
        {
            readTimeout.CancelAfter(ReadTimeout);
            var bytes = await ReadMessageAsync(pipe, readTimeout.Token);
            // Too big: the client is still writing and wouldn't read a reply,
            // so it is simply disconnected.
            if (bytes == null) return false;
            try
            {
                request = JsonSerializer.Deserialize(bytes, AskPipeJson.Default.AskRequest);
            }
            catch (JsonException)
            {
                request = null;
            }
        }
        if (request == null)
        {
            await ReplyAsync(pipe, new AskReply(null, "The request must be JSON: {\"text\": \"...\", \"question\": \"...\"}."));
            return false;
        }

        // The request was read first, so the caller isn't left blocked writing
        // while you decide; then nothing is answered unless it is allowed.
        if (caller == null)
        {
            await ReplyAsync(pipe, new AskReply(null, refusal ?? "CloClo couldn't tell which program is asking."));
            return false;
        }
        if (!await _authorize(caller, _stop.Token))
        {
            await ReplyAsync(pipe, new AskReply(null,
                "This program isn't allowed to ask CloClo. Allow it when CloClo asks, or from Privacy -> Apps allowed to ask."));
            return false;
        }

        using var answerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        answerTimeout.CancelAfter(AnswerTimeout);
        try
        {
            var answer = await LlmService.AnswerAsync(request.Text ?? "", request.Question ?? "", answerTimeout.Token);
            await ReplyAsync(pipe, new AskReply(answer, null));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or InvalidDataException or FileNotFoundException)
        {
            await ReplyAsync(pipe, new AskReply(null, ex.Message));
            return false;
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            await ReplyAsync(pipe, new AskReply(null, "The answer took too long."));
            return false;
        }
    }

    // One whole message, or null when it is bigger than MaxRequestBytes.
    private static async Task<byte[]?> ReadMessageAsync(NamedPipeServerStream pipe, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxRequestBytes];
        var count = 0;
        do
        {
            if (count == buffer.Length) return null;
            var read = await pipe.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0) break;
            count += read;
        } while (!pipe.IsMessageComplete);
        return buffer.AsSpan(0, count).ToArray();
    }

    // Within WriteTimeout: a pipe write waits until the client reads, and a
    // client that never does mustn't keep the pipe.
    private async Task ReplyAsync(NamedPipeServerStream pipe, AskReply reply)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(reply, AskPipeJson.Default.AskReply);
        using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        writeTimeout.CancelAfter(WriteTimeout);
        await pipe.WriteAsync(bytes, writeTimeout.Token);
        await pipe.FlushAsync(writeTimeout.Token);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch { /* closing anyway */ }
        _stop.Dispose();
    }
}

internal sealed record AskRequest(string? Text, string? Question);
internal sealed record AskReply(
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Answer,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? Error);

[System.Text.Json.Serialization.JsonSourceGenerationOptions(
    PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true, MaxDepth = 4)]
[System.Text.Json.Serialization.JsonSerializable(typeof(AskRequest))]
[System.Text.Json.Serialization.JsonSerializable(typeof(AskReply))]
internal sealed partial class AskPipeJson : System.Text.Json.Serialization.JsonSerializerContext;
