using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace CloCloWidget;

// The single-file exe unpacks WPF's native DLLs into
// %TEMP%\.net\CloCloWidget\<id>\ on first run and simply reuses that folder
// afterwards — the .NET host doesn't re-check what it finds there (verified:
// a planted file is loaded as-is). Anything running as this user could drop
// a DLL there and have it run inside the signed CloClo process.
//
// So before WPF loads any of them, every DLL in that folder must carry an
// intact Authenticode signature from Microsoft (they're all Microsoft's own
// WPF/runtime binaries). If one doesn't, the folder is deleted and CloClo
// restarts once, which makes the host extract clean copies.
public static class NativeLibraryCheck
{
    private const string RetriedVariable = "CLOCLO_NATIVE_REEXTRACTED";

    // Returns false when the process should exit (it has restarted itself,
    // or refused to run).
    public static bool EnsureTrusted()
    {
        // Only a bundled single-file build extracts anything; in a normal
        // build the assembly has a real path and there's nothing to check.
        if (!string.IsNullOrEmpty(typeof(NativeLibraryCheck).Assembly.Location)) return true;

        var extractDirs = ExtractionDirectories().ToList();
        var bad = extractDirs
            .SelectMany(d => Directory.EnumerateFiles(d, "*.dll"))
            .Where(f => !IsMicrosoftSigned(f))
            .ToList();
        if (bad.Count == 0) return true;

        if (Environment.GetEnvironmentVariable(RetriedVariable) == "1")
        {
            System.Windows.Forms.MessageBox.Show(
                "CloClo found native files it can't verify and won't run with them:\n\n" +
                string.Join("\n", bad.Select(Path.GetFileName)) +
                "\n\nReinstall CloClo, and consider a malware scan.",
                "CloClo", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Warning);
            return false;
        }

        foreach (var dir in extractDirs)
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort; the retry check catches it */ }
        }
        var restart = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        restart.Environment[RetriedVariable] = "1";
        Process.Start(restart);
        return false;
    }

    // The runtime's native probing directories, minus the exe's own folder —
    // for a single-file build that leaves exactly the extraction folder.
    private static IEnumerable<string> ExtractionDirectories()
    {
        var appDir = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var dirs = (AppContext.GetData("NATIVE_DLL_SEARCH_DIRECTORIES") as string ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        return dirs.Where(d => !d.Equals(appDir, StringComparison.OrdinalIgnoreCase) && Directory.Exists(d));
    }

    private static bool IsMicrosoftSigned(string path)
    {
        if (WinVerifyTrustFile(path) != 0) return false; // missing, tampered or untrusted
        try
        {
#pragma warning disable SYSLIB0057 // only the signer certificate is read; the file's integrity came from WinVerifyTrust
            using var signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            // Microsoft signs these under several names ("Microsoft Windows",
            // ".NET"…) but always as the organization Microsoft Corporation,
            // and always under one of Microsoft's own roots — which no other
            // certificate authority can issue into.
            if (!IsMicrosoftCorporation(signer)) return false;
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // This chain only identifies the root. Whether the signature is
            // valid was already settled by WinVerifyTrust, which honours the
            // signing timestamp — several of these DLLs were signed with
            // certificates that have since expired, which is fine.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid | X509VerificationFlags.IgnoreCtlNotTimeValid;
            if (!chain.Build(signer)) return false;
            var root = chain.ChainElements[^1].Certificate;
            return IsMicrosoftCorporation(root)
                && root.GetNameInfo(X509NameType.SimpleName, false)?.StartsWith("Microsoft Root", StringComparison.Ordinal) == true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsMicrosoftCorporation(X509Certificate2 cert) =>
        cert.SubjectName.EnumerateRelativeDistinguishedNames()
            .Any(rdn => rdn.GetSingleElementType().Value == "2.5.4.10" // O=
                && rdn.GetSingleElementValue() == "Microsoft Corporation");

    // ---- WinVerifyTrust: checks the file's hash against its signature and
    // that the certificate chains to a trusted root. Offline only (no
    // revocation fetch), so startup never waits on the network.

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    private static int WinVerifyTrustFile(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2,            // WTD_UI_NONE
                fdwRevocationChecks = 0,   // WTD_REVOKE_NONE
                dwUnionChoice = 1,         // WTD_CHOICE_FILE
                pFile = fileInfoPtr,
                dwStateAction = 0,         // WTD_STATEACTION_IGNORE
                dwProvFlags = 0x1000,      // WTD_CACHE_ONLY_URL_RETRIEVAL
            };
            var action = GenericVerifyV2;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WINTRUST_FILE_INFO>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
