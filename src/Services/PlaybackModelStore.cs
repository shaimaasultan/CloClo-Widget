using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// The model is inert data shipped with CloClo. It is read through one handle,
// checked against the exact published bytes, then passed to the native runtime
// as that same buffer rather than reopened by pathname.
internal static class PlaybackModelStore
{
    private const int ModelSize = 147951465;
    private const string ModelSha256 = "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE";
    private static readonly string ModelPath = Path.Combine(AppContext.BaseDirectory, "ggml-base.bin");

    public static async Task<byte[]> ReadVerifiedBytesAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(ModelPath))
            throw new FileNotFoundException("The bundled speech model is missing. Reinstall CloClo.", ModelPath);

        await using var stream = new FileStream(ModelPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != ModelSize)
            throw new InvalidDataException("The bundled speech model failed its integrity check. Reinstall CloClo.");

        var bytes = new byte[ModelSize];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(ModelSha256, StringComparison.Ordinal))
            throw new InvalidDataException("The bundled speech model failed its integrity check. Reinstall CloClo.");
        return bytes;
    }
}
