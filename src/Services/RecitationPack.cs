using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CloCloWidget;

// A Quran recitation pack installed beside CloClo:
// Recitations\CloClo.Recitations.<reciter>.dll, made by
// scripts\pack-recitations.ps1 - a .NET assembly with no code, holding each
// ayah's MP3 as an embedded resource named "SSSAAA.mp3".
//
// It is never loaded as code. The file is opened once, read-only and shared
// for reading only, so nothing can change it while CloClo has it; its
// Authenticode signature is checked on that file (intact, and signed by the
// exe's own certificate - see NativeLibraryCheck.IsSignedLikeExe); then the
// resource table is read straight out of the file and each recitation is
// played from memory. No MP3 is written to disk.
public sealed class RecitationPack : IDisposable
{
    private const long MaxPackBytes = 2L * 1024 * 1024 * 1024;
    private const int MaxResourceBytes = 30 * 1024 * 1024;
    private const string Prefix = "CloClo.Recitations.";

    private readonly FileStream _file;
    private readonly PEReader _pe;
    private readonly Dictionary<string, long> _offsets; // resource name -> file offset of its data

    public string Reciter { get; }
    // Signed with CloClo's certificate (false only for an unsigned build,
    // where an unsigned pack is all that is accepted).
    public bool Signed { get; private init; }

    private RecitationPack(string reciter, FileStream file, PEReader pe, Dictionary<string, long> offsets)
    {
        Reciter = reciter;
        _file = file;
        _pe = pe;
        _offsets = offsets;
    }

    public static string Folder => Path.Combine(AppContext.BaseDirectory, "Recitations");

    // The first installed pack (by name) that passes its checks, or null.
    // Rejected packs are named in rejected, so the window can say so.
    public static RecitationPack? OpenInstalled(out List<string> rejected)
    {
        rejected = new List<string>();
        string[] candidates;
        try
        {
            if (!Directory.Exists(Folder)) return null;
            candidates = Directory.GetFiles(Folder, Prefix + "*.dll")
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        foreach (var path in candidates)
        {
            var pack = TryOpen(path);
            if (pack != null) return pack;
            rejected.Add(Path.GetFileName(path));
        }
        return null;
    }

    public static RecitationPack? TryOpen(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!fileName.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var reciter = fileName[Prefix.Length..];
        FileStream? file = null;
        PEReader? pe = null;
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) return null;
            // Held open, shared for reading only, until CloClo is done with it:
            // the bytes whose signature is checked are the bytes later read.
            file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > MaxPackBytes || !NativeLibraryCheck.IsSignedLikeExe(path)) return Close(file, null);

            pe = new PEReader(file, PEStreamOptions.LeaveOpen);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is not { } cor) return Close(file, pe);
            var metadata = pe.GetMetadataReader();
            // Only data: no types beyond the module's own placeholder, no methods.
            if (metadata.TypeDefinitions.Count > 1 || metadata.MethodDefinitions.Count > 0) return Close(file, pe);
            if (!pe.PEHeaders.TryGetDirectoryOffset(cor.ResourcesDirectory, out var resourcesStart)) return Close(file, pe);

            var offsets = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var handle in metadata.ManifestResources)
            {
                var resource = metadata.GetManifestResource(handle);
                var name = metadata.GetString(resource.Name);
                if (!resource.Implementation.IsNil || name.Length != 10 || !name.EndsWith(".mp3", StringComparison.Ordinal) ||
                    !name[..6].All(char.IsAsciiDigit)) continue;
                offsets[name] = resourcesStart + resource.Offset;
            }
            return offsets.Count > 0 ? new RecitationPack(reciter, file, pe, offsets) { Signed = HasSignature(path) } : Close(file, pe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or
                                   InvalidOperationException or ArgumentException)
        {
            return Close(file, pe);
        }
    }

    private static bool HasSignature(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057 // only asks whether a signer exists; the signature was verified above
            using var signer = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            return true;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return false;
        }
    }

    private static RecitationPack? Close(FileStream? file, PEReader? pe)
    {
        pe?.Dispose();
        file?.Dispose();
        return null;
    }

    public bool Has(int surah, int ayah) => _offsets.ContainsKey(Name(surah, ayah));

    // The recitation's MP3 bytes, or null.
    public byte[]? Read(int surah, int ayah)
    {
        if (!_offsets.TryGetValue(Name(surah, ayah), out var offset)) return null;
        try
        {
            lock (_file)
            {
                // Each resource is a 4-byte little-endian length, then the data.
                var header = new byte[4];
                _file.Position = offset;
                _file.ReadExactly(header);
                var length = BitConverter.ToInt32(header);
                if (length <= 0 || length > MaxResourceBytes || offset + 4 + length > _file.Length) return null;
                var data = new byte[length];
                _file.ReadExactly(data);
                return data;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static string Name(int surah, int ayah) => $"{surah:000}{ayah:000}.mp3";

    public void Dispose()
    {
        _pe.Dispose();
        _file.Dispose();
    }
}
