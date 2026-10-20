using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
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
// - The request is data. Its text and question go only into the model's
//   prompt; the answer goes only back to the caller. Nothing in either is run,
//   opened or typed anywhere, and CloClo says which program asked.
internal sealed class AskPipeServer : IDisposable
{
    public const string PipeName = "CloClo.Ask";
    private const int MaxRequestBytes = 128 * 1024;
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

    // The callbacks are set before the loop starts, so none is missed.
    private AskPipeServer(Action<string, bool> served, Action<string> failed)
    {
        _served = served;
        _failed = failed;
        _loop = Task.Run(RunAsync);
    }

    public static AskPipeServer? Start(Action<string, bool> served, Action<string> failed)
    {
        if (Program.Instance?.IsHeld != true) return null;
        return new AskPipeServer(served, failed);
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

                var caller = ClientProcessName(pipe);
                var answered = false;
                try
                {
                    answered = await ServeAsync(pipe);
                }
                catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
                {
                    // The client left or CloClo is closing.
                }
                if (!_stop.IsCancellationRequested) _served(caller, answered);
            }
        }
    }

    private async Task<bool> ServeAsync(NamedPipeServerStream pipe)
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

    // For the notice only: which program asked. A name, never trusted for
    // anything else.
    private static string ClientProcessName(NamedPipeServerStream pipe)
    {
        try
        {
            if (GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
            {
                using var process = Process.GetProcessById((int)pid);
                return process.ProcessName + ".exe";
            }
        }
        catch
        {
            // The client may already have exited.
        }
        return "another app";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

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
