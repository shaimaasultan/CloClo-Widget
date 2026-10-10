using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// Process-wide protections, switched on as the very first thing in Main —
// before WPF (or anything else that loads DLLs by name) starts:
//
// - DLL search order: SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS)
//   takes the current directory and PATH out of the search for DLLs loaded by
//   name, leaving the app folder, System32 and folders added explicitly (the
//   single-file app's own unpack folder, which NativeLibraryCheck verifies).
// - Image-load policy: never load a DLL from a network share or from a
//   low-integrity location, and prefer System32's copy of any system DLL
//   loaded by name over a look-alike elsewhere.
// - Extension-point policy: turns off the legacy ways other programs can push
//   a DLL into every process - AppInit_DLLs, global window hooks
//   (SetWindowsHookEx), legacy IMEs and Winsock layered providers.
//
// Each call is best-effort: on a Windows version without it, CloClo still
// runs with the other protections.
public static class ProcessHardening
{
    private const uint LOAD_LIBRARY_SEARCH_DEFAULT_DIRS = 0x00001000;
    private const int ProcessImageLoadPolicy = 10;
    private const int ProcessExtensionPointDisablePolicy = 6;

    // PROCESS_MITIGATION_IMAGE_LOAD_POLICY bits.
    private const uint NoRemoteImages = 0x1;
    private const uint NoLowMandatoryLabelImages = 0x2;
    private const uint PreferSystem32Images = 0x4;
    // PROCESS_MITIGATION_EXTENSION_POINT_DISABLE_POLICY bit.
    private const uint DisableExtensionPoints = 0x1;

    public static void Apply(IEnumerable<string> extraDllDirectories)
    {
        SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS);
        foreach (var dir in extraDllDirectories)
        {
            if (Directory.Exists(dir)) AddDllDirectory(dir);
        }

        var imageLoad = NoRemoteImages | NoLowMandatoryLabelImages | PreferSystem32Images;
        SetProcessMitigationPolicy(ProcessImageLoadPolicy, ref imageLoad, sizeof(uint));

        var extensionPoints = DisableExtensionPoints;
        SetProcessMitigationPolicy(ProcessExtensionPointDisablePolicy, ref extensionPoints, sizeof(uint));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDefaultDllDirectories(uint directoryFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr AddDllDirectory(string newDirectory);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessMitigationPolicy(int policy, ref uint buffer, int length);
}
