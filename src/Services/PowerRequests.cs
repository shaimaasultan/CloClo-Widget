using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace CloCloWidget;

// CloClo's own record of the shutdowns it asks Windows for. Windows logs no
// requester for one started with InitiateShutdown (no User32 1074; Kernel-
// Power 109 only says "Kernel API"), so without this a voice shutdown looks
// like any other. One line per request, "2026-10-19T18:51:15Z voice", in
// CloClo's local folder beside errors.log; kept short, never sent anywhere.
internal static class PowerRequests
{
    private const int MaxLines = 100;
    private static readonly object FileLock = new();

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CloCloWidget", "power-requests.log");

    // Called once Windows has accepted the request. Best-effort: a failure
    // only means the shutdown isn't named in Windows status.
    internal static void Record(string by)
    {
        try
        {
            lock (FileLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                var lines = File.Exists(FilePath) ? File.ReadAllLines(FilePath).TakeLast(MaxLines - 1).ToList() : [];
                lines.Add($"{DateTime.UtcNow:yyyy-MM-ddTHH:mm:ssZ} {by}");
                File.WriteAllLines(FilePath, lines);
            }
        }
        catch
        {
            // Never let the record stop the shutdown.
        }
    }

    // The requests since a UTC time, oldest first: when (UTC) and by what.
    internal static List<(DateTime Time, string By)> Read(DateTime since)
    {
        try
        {
            if (!File.Exists(FilePath)) return [];
            return File.ReadAllLines(FilePath)
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && DateTime.TryParse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out _))
                .Select(parts => (DateTime.Parse(parts[0], CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal), parts[1].Trim()))
                .Where(r => r.Item1 >= since).OrderBy(r => r.Item1).ToList();
        }
        catch
        {
            return [];
        }
    }
}
