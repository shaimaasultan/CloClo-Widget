using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;

namespace CloCloWidget;

// Restricted local storage for text explicitly saved from the speech editor.
public static class SpeechToText
{
    // Per-user and private: %LocalAppData%\CloCloWidget\Dictation. Not
    // Documents — Controlled Folder Access (Windows' ransomware protection)
    // blocks unrecognized apps from writing there by default — and not next
    // to the exe either, which fails under Program Files and can leak
    // dictated text into whatever synced or shared folder the exe sits in.
    // LocalAppData is neither protected by CFA nor roamed/synced.
    public static string DictationFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloCloWidget", "Dictation");

    // settings.json only ever points dictation inside DictationFolder — a
    // path anywhere else (an older build's location, or a hand-edited
    // settings file) gets a fresh file here instead of being appended to.
    public static bool IsInDictationFolder([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            var folder = Path.GetFullPath(DictationFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var fullPath = Path.GetFullPath(path);
            return string.Equals(Path.GetDirectoryName(fullPath)?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                       folder, StringComparison.OrdinalIgnoreCase)
                && IsDictationFileName(Path.GetFileName(fullPath));
        }
        catch
        {
            return false;
        }
    }

    // The new file must not exist yet (CreateNew): the name is predictable,
    // so something else could have put a file or a link there first to have
    // dictation written - or an existing file emptied - somewhere else. A
    // name that's taken (also by a second new file within the same second,
    // which used to empty the first) gets a number added instead.
    public static string CreateNewFile()
    {
        Directory.CreateDirectory(DictationFolder);
        return CreateNewFileCore(null);
    }

    public static string CreateNewFileWithText(string text)
    {
        Directory.CreateDirectory(DictationFolder);
        return CreateNewFileCore(text);
    }

    private static string CreateNewFileCore(string? initialText)
    {
        var stamp = $"Dictation-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";
        for (var n = 1; n <= 100; n++)
        {
            var path = Path.Combine(DictationFolder, n == 1 ? $"{stamp}.txt" : $"{stamp}-{n}.txt");
            try
            {
                using var stream = OpenDictationFile(path, createNew: true);
                if (initialText != null) WriteAppend(stream, initialText);
                return path;
            }
            catch (IOException) when (n < 100 && Path.Exists(path))
            {
                // Taken - try the next number.
            }
        }
        throw new IOException("Couldn't create a new dictation file because all available names are taken.");
    }

    // Open without following the final reparse point, then inspect the opened
    // handle before writing. Reject directories, reparse points and hardlinks.
    public static bool TryAppend(string? filePath, string text)
    {
        if (!IsInDictationFolder(filePath)) return false;

        FileStream stream;
        try
        {
            stream = OpenDictationFile(filePath!, createNew: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return false;
        }

        using (stream) WriteAppend(stream, text);
        return true;
    }

    private static void WriteAppend(FileStream stream, string text)
    {
        stream.Seek(0, SeekOrigin.End);
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 1024, leaveOpen: true);
        writer.Write($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        writer.Flush();
    }

    private static bool IsDictationFileName(string fileName)
    {
        if (!fileName.StartsWith("Dictation-", StringComparison.Ordinal) || !fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            return false;

        var stem = fileName["Dictation-".Length..^".txt".Length];
        const int timestampLength = 19;
        if (stem.Length > timestampLength && stem[timestampLength] == '-' &&
            int.TryParse(stem[(timestampLength + 1)..], out var suffix) && suffix is >= 2 and <= 100)
            stem = stem[..timestampLength];

        return stem.Length == timestampLength && DateTime.TryParseExact(stem, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out _);
    }

    private static FileStream OpenDictationFile(string path, bool createNew)
    {
        const uint GenericWrite = 0x40000000;
        const uint ShareRead = 0x00000001;
        const uint CreateNew = 1;
        const uint OpenExisting = 3;
        const uint OpenReparsePoint = 0x00200000;

        var handle = CreateFileW(path, GenericWrite, ShareRead, IntPtr.Zero,
            createNew ? CreateNew : OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (createNew && error is 80 or 183)
                throw new IOException("Dictation file name is already taken.");
            throw new Win32Exception(error);
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out var info))
                throw new Win32Exception(Marshal.GetLastWin32Error());

            const uint DirectoryAttribute = 0x00000010;
            const uint ReparsePointAttribute = 0x00000400;
            if ((info.FileAttributes & (DirectoryAttribute | ReparsePointAttribute)) != 0 || info.NumberOfLinks != 1)
                throw new IOException("Dictation path isn't a plain, single-link file.");

            return new FileStream(handle, FileAccess.Write);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);
}
