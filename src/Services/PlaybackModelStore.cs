using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// The model is data, never executable. Download only after the playback toggle
// is clicked, and accept exactly the publisher's pinned model bytes.
internal static class PlaybackModelStore
{
    private const long ModelSize = 147951465;
    private const string ModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";
    private static readonly Uri ModelUri = new(
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/80da2d8bfee42b0e836fc3a9890373e5defc00a6/ggml-base.bin");
    private static readonly string ModelDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloCloWidget", "Models");
    private static readonly string ModelPath = Path.Combine(ModelDirectory, "ggml-base.bin");
    public static async Task<FileStream> OpenVerifiedAsync(CancellationToken cancellationToken)
    {
        var stream = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (stream.Length != ModelSize ||
                !Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))
                    .Equals(ModelSha256, StringComparison.Ordinal))
                throw new InvalidDataException("The local speech model failed its integrity check. Remove it and try again.");
            stream.Position = 0;
            return stream; // caller finishes verification through this same handle
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    // Whisper must consume these exact verified bytes. Passing ModelPath to
    // its native loader would reopen the name after verification and could
    // follow a swapped link to different bytes.
    public static async Task<byte[]> ReadVerifiedBytesAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != ModelSize)
            throw new InvalidDataException("The local speech model failed its integrity check. Remove it and try again.");

        var bytes = new byte[checked((int)ModelSize)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ModelSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The local speech model failed its integrity check. Remove it and try again.");
        return bytes;
    }

    public static async Task EnsureDownloadedAsync(IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(ModelPath))
        {
            await using var verified = await OpenVerifiedAsync(cancellationToken);
            return;
        }

        Directory.CreateDirectory(ModelDirectory);
        if ((File.GetAttributes(ModelDirectory) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The speech model folder is a link. Choose a normal local folder before downloading.");

        // A unique CreateNew file avoids overwriting an existing model or a
        // planted file. The final name is used only after its hash is checked.
        var temporary = Path.Combine(ModelDirectory, $".model-{Guid.NewGuid():N}.download");
        try
        {
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
            {
                Timeout = TimeSpan.FromMinutes(20)
            };
            using var response = await client.GetAsync(ModelUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps ||
                response.Content.Headers.ContentLength is > ModelSize)
                throw new InvalidDataException("The model server returned an unexpected response.");

            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    var buffer = new byte[65536];
                    long length = 0;
                    int read;
                    while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
                    {
                        length += read;
                        if (length > ModelSize) throw new InvalidDataException("The model download exceeds its expected size.");
                        hash.AppendData(buffer, 0, read);
                        await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        progress?.Report(length);
                    }
                    await destination.FlushAsync(cancellationToken);
                    if (length != ModelSize ||
                        !Convert.ToHexString(hash.GetHashAndReset()).Equals(ModelSha256, StringComparison.Ordinal))
                        throw new InvalidDataException("The model download failed its integrity check.");
                }
            }
            File.Move(temporary, ModelPath, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
