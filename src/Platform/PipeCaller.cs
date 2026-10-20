using System;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CloCloWidget;

// Who is on the other end of the ask pipe: the program file of the client
// process, and how it is recognised again next time.
//
// - Signed with a signature Windows trusts: by its full path and signer
//   certificate (thumbprint), so updates from the same publisher still match
//   but a different program at that path doesn't.
// - Unsigned, or signed by a certificate Windows doesn't trust (a self-signed
//   build): by its full path and the SHA-256 of the file, so any change to it
//   needs approving again.
// - A signature that doesn't match the file is never accepted.
//
// The client's process is opened before it is examined and the pipe is then
// checked to still be connected to that same process ID: if the client had
// exited and its ID been reused, the pipe would be broken, so the program
// examined is the one that connected. Windows doesn't let a running
// program's file be changed, so the file read is the image that runs.
internal sealed record PipeCaller(string Path, string? SignerThumbprint, string? Publisher, string? Sha256)
{
    public string FileName => System.IO.Path.GetFileName(Path);

    // For the consent dialog and the Privacy menu.
    public string Description => SignerThumbprint != null
        ? $"{Path}\nSigned by {Publisher}"
        : $"{Path}\nNot signed by a publisher Windows trusts: recognised by its exact file (SHA-256), so any update needs allowing again.";

    // Null when the caller can't be identified or its signature is broken.
    public static PipeCaller? Identify(NamedPipeServerStream pipe, out string? refusal)
    {
        refusal = null;
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var pid))
        {
            refusal = "CloClo couldn't tell which program is asking.";
            return null;
        }
        using var process = OpenProcess(ProcessQueryLimitedInformation | Synchronize, false, pid);
        if (process.IsInvalid ||
            !GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var again) || again != pid || !pipe.IsConnected)
        {
            refusal = "CloClo couldn't tell which program is asking.";
            return null;
        }
        var path = ImagePath(process);
        if (path == null || !File.Exists(path))
        {
            refusal = "CloClo couldn't find the program that is asking.";
            return null;
        }

        var (result, signer) = NativeLibraryCheck.ReadSignature(path);
        using (signer)
        {
            if (result == NativeLibraryCheck.SignatureTrusted && signer != null)
                return new PipeCaller(path, signer.Thumbprint, PublisherName(signer), null);
            if (result == NativeLibraryCheck.SignatureBadDigest)
            {
                refusal = "The asking program's signature doesn't match its file, so CloClo won't answer it.";
                return null;
            }
        }
        // Unsigned, or signed by a certificate Windows doesn't trust.
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return new PipeCaller(path, null, null, Convert.ToHexString(SHA256.HashData(file)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            refusal = "CloClo couldn't read the program that is asking.";
            return null;
        }
    }

    // The allowed entry this caller matches, if any.
    public bool Matches(AskCallerSettings allowed) =>
        string.Equals(allowed.Path, Path, StringComparison.OrdinalIgnoreCase) &&
        (SignerThumbprint != null
            ? string.Equals(allowed.SignerThumbprint, SignerThumbprint, StringComparison.OrdinalIgnoreCase)
            : allowed.SignerThumbprint == null && string.Equals(allowed.Sha256, Sha256, StringComparison.OrdinalIgnoreCase));

    public AskCallerSettings ToSettings() => new()
    {
        Path = Path,
        SignerThumbprint = SignerThumbprint,
        Publisher = Publisher,
        Sha256 = Sha256,
    };

    private static string PublisherName(System.Security.Cryptography.X509Certificates.X509Certificate2 signer)
    {
        var name = signer.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
        return string.IsNullOrWhiteSpace(name) ? signer.Subject : name;
    }

    private static string? ImagePath(SafeProcessHandle process)
    {
        var buffer = new StringBuilder(32768);
        var size = buffer.Capacity;
        return QueryFullProcessImageNameW(process, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint Synchronize = 0x00100000;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, bool inheritHandle, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder name, ref int size);
}
