using System;
using System.Runtime.InteropServices;
using System.Text;

namespace CloCloWidget;

internal static class UserAccountName
{
    private const int NameSamCompatible = 2;
    private const int UserInfoLevel10 = 10;
    private const int MaxDisplayNameLength = 80;
    private const int MaxUserNameLength = 40;

    public static string? GetDisplayName()
    {
        // Only query the local SAM when Windows confirms this is a local
        // account. We intentionally do not look up domain display names,
        // which can require contacting a domain controller.
        var samName = GetFormattedWindowsName(NameSamCompatible);
        var separator = samName?.IndexOf('\\') ?? -1;
        if (separator <= 0 || !string.Equals(samName![..separator], Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return null;

        var userName = samName[(separator + 1)..];
        if (!IsSafeName(userName, MaxUserNameLength)) return null;

        try
        {
            var status = NetUserGetInfo(null, userName, UserInfoLevel10, out var buffer);
            try
            {
                if (status == 0 && buffer != IntPtr.Zero)
                {
                    var info = Marshal.PtrToStructure<UserInfo10>(buffer);
                    var fullName = ReadBoundedString(info.FullName, MaxDisplayNameLength);
                    if (IsSafeName(fullName, MaxDisplayNameLength)) return fullName!.Trim();
                }
            }
            finally { if (buffer != IntPtr.Zero) NetApiBufferFree(buffer); }
        }
        catch { /* local account details may not be available */ }
        return null;
    }

    private static string? GetFormattedWindowsName(int format)
    {
        try
        {
            var size = 256u;
            var buffer = new StringBuilder((int)size);
            if (GetUserNameEx(format, buffer, ref size) != 0)
            {
                return buffer.ToString();
            }
        }
        catch { /* the requested Windows name format isn't available */ }
        return null;
    }

    public static string? GetUserName()
    {
        try
        {
            var userName = Environment.UserName;
            return IsSafeName(userName, MaxUserNameLength) ? userName.Trim() : null;
        }
        catch { return null; }
    }

    private static bool IsSafeName(string? value, int maxLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maxLength &&
        value.Trim().Length > 0 &&
        IsSafeNameCharacters(value);

    private static bool IsSafeNameCharacters(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsLetterOrDigit(character) && character is not (' ' or '-' or '_' or '.' or '\''))
                return false;
        }
        return true;
    }

    // Bound reads from native account data in case a local account's name
    // is unusually long or malformed.
    private static string? ReadBoundedString(IntPtr value, int maxLength)
    {
        if (value == IntPtr.Zero) return null;
        var characters = new char[maxLength + 1];
        for (var i = 0; i <= maxLength; i++)
        {
            var character = (char)Marshal.ReadInt16(value, i * sizeof(char));
            if (character == '\0') return new string(characters, 0, i);
            if (i == maxLength) return null;
            characters[i] = character;
        }
        return null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UserInfo10
    {
        public IntPtr Name;
        public IntPtr Comment;
        public IntPtr UserComment;
        public IntPtr FullName;
    }

    [DllImport("Netapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int NetUserGetInfo(string? serverName, string userName, int level, out IntPtr buffer);

    [DllImport("Netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    [DllImport("secur32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetUserNameExW")]
    private static extern byte GetUserNameEx(int nameFormat, StringBuilder nameBuffer, ref uint size);
}
