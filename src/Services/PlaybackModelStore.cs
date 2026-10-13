using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace CloCloWidget;

// The model is inert data shipped with CloClo. It is read through one handle,
// checked against the exact published bytes, then passed to the native runtime
// as that same buffer rather than reopened by pathname.
internal enum SpeechModel
{
    // Multilingual base: CPU dictation, playback, and voice commands.
    Base,
    // large-v3-turbo (q5_0): Arabic dictation on a dedicated GPU.
    LargeV3Turbo,
}

internal static class PlaybackModelStore
{
    private sealed record ModelFile(string FileName, long Size, string Sha256);

    private static ModelFile Pinned(SpeechModel model) => model switch
    {
        SpeechModel.Base => new("ggml-base.bin", 147951465,
            "60ED5BC3DD14EEA856493D334349B405782DDCAF0028D4B5DF4088345FBA2EFE"),
        SpeechModel.LargeV3Turbo => new("ggml-large-v3-turbo-q5_0.bin", 574041195,
            "394221709CD5AD1F40C46E6031CA61BCE88931E6E088C188294C6D5A55FFA7E2"),
        _ => throw new ArgumentOutOfRangeException(nameof(model)),
    };

    private static string PathOf(SpeechModel model) => Path.Combine(AppContext.BaseDirectory, Pinned(model).FileName);

    // Presence and size only; the full hash is checked when the model is read.
    public static bool IsInstalled(SpeechModel model)
    {
        var info = new FileInfo(PathOf(model));
        return info.Exists && info.Length == Pinned(model).Size;
    }

    public static async Task<byte[]> ReadVerifiedBytesAsync(SpeechModel model, CancellationToken cancellationToken)
    {
        var file = Pinned(model);
        var path = PathOf(model);
        if (!File.Exists(path))
            throw new FileNotFoundException("The bundled speech model is missing. Reinstall CloClo.", path);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length != file.Size)
            throw new InvalidDataException("The bundled speech model failed its integrity check. Reinstall CloClo.");

        var bytes = new byte[file.Size];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (!Convert.ToHexString(SHA256.HashData(bytes)).Equals(file.Sha256, StringComparison.Ordinal))
            throw new InvalidDataException("The bundled speech model failed its integrity check. Reinstall CloClo.");
        return bytes;
    }
}
