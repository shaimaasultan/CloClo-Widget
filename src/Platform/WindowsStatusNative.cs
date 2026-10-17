using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// The plain Windows calls behind the Windows status window: memory and
// processor counters, the power plan, the active display paths and the
// graphics adapters. Each one only reads; none needs elevation or the
// network, and a failure returns nothing rather than throwing.
internal static class WindowsStatusNative
{
    // ---- Processor and memory ----

    // Idle, kernel (which includes idle) and user time summed over every
    // processor since boot, in 100 ns units.
    public static (long Idle, long Kernel, long User)? SystemTimes() =>
        GetSystemTimes(out var idle, out var kernel, out var user) ? (idle, kernel, user) : null;

    public record Memory(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent, ulong? InstalledBytes);

    public static Memory? ReadMemory()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (!GlobalMemoryStatusEx(ref status)) return null;
        ulong? installed = GetPhysicallyInstalledSystemMemory(out var kilobytes) ? kilobytes * 1024 : null;
        return new Memory(status.ullTotalPhys, status.ullAvailPhys, status.dwMemoryLoad, installed);
    }

    public record Performance(ulong CommitBytes, ulong CommitLimitBytes, ulong CacheBytes, uint Processes, uint Threads, uint Handles);

    public static Performance? ReadPerformance()
    {
        var info = new PERFORMANCE_INFORMATION { cb = (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>() };
        if (!K32GetPerformanceInfo(ref info, info.cb)) return null;
        var page = (ulong)info.PageSize;
        return new Performance((ulong)info.CommitTotal * page, (ulong)info.CommitLimit * page,
            (ulong)info.SystemCache * page, info.ProcessCount, info.ThreadCount, info.HandleCount);
    }

    // ---- Power ----

    public record PowerStatus(bool? OnAc, bool BatterySaver);

    public static PowerStatus? ReadPowerStatus()
    {
        if (!GetSystemPowerStatus(out var power)) return null;
        bool? onAc = power.ACLineStatus switch { 0 => false, 1 => true, _ => null };
        return new PowerStatus(onAc, power.SystemStatusFlag == 1);
    }

    public static string? ActivePowerPlanName()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePointer) != 0 || schemePointer == IntPtr.Zero) return null;
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePointer);
            uint size = 0;
            if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ref size) != 0 || size == 0 || size > 1024)
                return null;
            var buffer = Marshal.AllocHGlobal((int)size);
            try
            {
                if (PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0) return null;
                var name = Marshal.PtrToStringUni(buffer)?.Trim();
                return string.IsNullOrEmpty(name) ? null : name;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { LocalFree(schemePointer); }
    }

    // The Settings > Power "Power mode" slider, which Windows layers over the plan.
    public static string? PowerMode()
    {
        try
        {
            if (PowerGetEffectiveOverlayScheme(out var overlay) != 0) return null;
            return overlay.ToString().ToLowerInvariant() switch
            {
                "00000000-0000-0000-0000-000000000000" => "Balanced",
                "961cc777-2547-4f9d-8174-7d86181b8a7a" => "Best power efficiency",
                "3af9b8d9-7c97-431d-ad78-34a8bfea439f" => "Better battery",
                "ded574b5-45a0-4f42-8737-46345c09c238" => "Best performance",
                _ => null,
            };
        }
        catch (EntryPointNotFoundException) { return null; } // Older Windows 10.
    }

    // ---- Displays ----

    public record DisplayPath(string GdiName, string? FriendlyName, string? Connection, double? RefreshHz,
        bool Portrait, bool? HdrSupported, bool? HdrOn, long AdapterLuid);

    // The active display paths: each connected monitor, its real model name
    // (from its EDID), cable type, refresh rate and HDR state.
    public static List<DisplayPath> ReadDisplayPaths()
    {
        var result = new List<DisplayPath>();
        if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount) != 0) return result;
        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];
        if (QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths, ref modeCount, modes, IntPtr.Zero) != 0) return result;

        for (var i = 0; i < pathCount; i++)
        {
            var path = paths[i];
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            source.header = Header(DeviceInfoGetSourceName, Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(), path.sourceInfo.adapterId, path.sourceInfo.id);
            if (DisplayConfigGetDeviceInfo(ref source) != 0) continue;

            var target = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
            target.header = Header(DeviceInfoGetTargetName, Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(), path.targetInfo.adapterId, path.targetInfo.id);
            var hasTarget = DisplayConfigGetDeviceInfo(ref target) == 0;

            var (hdrSupported, hdrOn) = ReadHdr(path.targetInfo.adapterId, path.targetInfo.id);

            var rate = path.targetInfo.refreshRate;
            double? refresh = rate.Denominator == 0 ? null : Math.Round((double)rate.Numerator / rate.Denominator, 2);
            var technology = hasTarget ? target.outputTechnology : path.targetInfo.outputTechnology;
            var friendly = hasTarget ? target.monitorFriendlyDeviceName?.Trim() : null;

            result.Add(new DisplayPath(
                source.viewGdiDeviceName,
                string.IsNullOrEmpty(friendly) ? null : friendly,
                ConnectionName(technology),
                refresh,
                path.targetInfo.rotation is 2 or 4,
                hdrSupported,
                hdrOn,
                path.sourceInfo.adapterId.ToInt64()));
        }
        return result;
    }

    // HDR, as opposed to the SDR "advanced color" (automatic color
    // management) that the older query also reports as enabled.
    private static (bool? Supported, bool? On) ReadHdr(LUID adapter, uint target)
    {
        // Windows 11 24H2 and later: tells HDR apart from wide color.
        var color2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2();
        color2.header = Header(DeviceInfoGetAdvancedColorInfo2, Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(), adapter, target);
        if (DisplayConfigGetDeviceInfo(ref color2) == 0)
            return ((color2.value & 0x10) != 0, color2.activeColorMode == 2);

        var color = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO();
        color.header = Header(DeviceInfoGetAdvancedColorInfo, Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(), adapter, target);
        if (DisplayConfigGetDeviceInfo(ref color) != 0) return (null, null);
        // Bit 3 (wide color enforced) means advanced color is SDR color management.
        var wideColorOnly = (color.value & 8) != 0;
        return ((color.value & 1) != 0 && !wideColorOnly, (color.value & 2) != 0 && !wideColorOnly);
    }

    public record Monitor(string GdiName, int Width, int Height, bool Primary, int? ScalePercent);

    // Every monitor as Windows lays out the desktop, in physical pixels
    // (CloClo is per-monitor DPI aware), with its scaling setting.
    public static List<Monitor> ReadMonitors()
    {
        var monitors = new List<Monitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(handle, ref info))
            {
                int? scale = GetDpiForMonitor(handle, 0, out var dpiX, out _) == 0 && dpiX > 0
                    ? (int)Math.Round(dpiX * 100.0 / 96)
                    : null;
                monitors.Add(new Monitor(info.szDevice, info.rcMonitor.Right - info.rcMonitor.Left,
                    info.rcMonitor.Bottom - info.rcMonitor.Top, (info.dwFlags & 1) != 0, scale));
            }
            return true;
        }, IntPtr.Zero);
        return monitors;
    }

    private static string? ConnectionName(uint technology) => technology switch
    {
        0 => "VGA",
        1 or 2 => "S-Video",
        3 => "Component",
        4 => "DVI",
        5 => "HDMI",
        6 => "LVDS",
        10 => "DisplayPort",
        11 => "Built-in (DisplayPort)",
        12 => "UDI",
        13 => "Built-in (UDI)",
        15 => "Wireless (Miracast)",
        16 or 17 => "Virtual or indirect",
        0x80000000 => "Built-in",
        _ => null,
    };

    // ---- Graphics adapters (DXGI) ----

    public record GraphicsAdapter(string Name, ulong DedicatedBytes, ulong SharedBytes, long Luid);

    // Hardware adapters with their video memory, as DirectX sees them. Only
    // the adapter descriptions are read; no device is created.
    public static List<GraphicsAdapter> ReadGraphicsAdapters()
    {
        var adapters = new List<GraphicsAdapter>();
        var factoryId = new Guid("770aae78-f26f-4dba-a829-253c83d1b387"); // IDXGIFactory1
        if (CreateDXGIFactory1(ref factoryId, out var factory) != 0 || factory == IntPtr.Zero) return adapters;
        try
        {
            var enumAdapters = Method<EnumAdapters1Fn>(factory, 12);
            for (uint index = 0; index < 16; index++)
            {
                if (enumAdapters(factory, index, out var adapter) != 0 || adapter == IntPtr.Zero) break;
                try
                {
                    if (Method<GetDesc1Fn>(adapter, 10)(adapter, out var desc) != 0) continue;
                    if ((desc.Flags & 2) != 0) continue; // DXGI_ADAPTER_FLAG_SOFTWARE (Basic Render Driver)
                    adapters.Add(new GraphicsAdapter(desc.Description.Trim(), (ulong)desc.DedicatedVideoMemory,
                        (ulong)desc.SharedSystemMemory, desc.AdapterLuid.ToInt64()));
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return adapters;
    }

    private static T Method<T>(IntPtr comObject, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(comObject), slot * IntPtr.Size));

    private static void Release(IntPtr comObject) => Method<ReleaseFn>(comObject, 2)(comObject);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters1Fn(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDesc1Fn(IntPtr self, out DXGI_ADAPTER_DESC1 desc);

    // ---- Declarations ----

    private const uint QdcOnlyActivePaths = 2;
    private const uint DeviceInfoGetSourceName = 1;
    private const uint DeviceInfoGetTargetName = 2;
    private const uint DeviceInfoGetAdvancedColorInfo = 9;
    private const uint DeviceInfoGetAdvancedColorInfo2 = 15;

    private static DISPLAYCONFIG_DEVICE_INFO_HEADER Header(uint type, int size, LUID adapter, uint id) =>
        new() { type = type, size = (uint)size, adapterId = adapter, id = id };

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID
    {
        public uint LowPart;
        public int HighPart;
        public readonly long ToInt64() => ((long)HighPart << 32) | LowPart;
    }

    [StructLayout(LayoutKind.Sequential)] private struct DISPLAYCONFIG_RATIONAL { public uint Numerator, Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id, modeInfoIdx, statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id, modeInfoIdx, outputTechnology, rotation, scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public uint scanLineOrdering;
        public int targetAvailable;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Only the header is read; the 48-byte union after it is left opaque.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public uint infoType, id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER { public uint type, size; public LUID adapterId; public uint id; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags, outputTechnology;
        public ushort edidManufactureId, edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value, colorEncoding, bitsPerColorChannel;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint value, colorEncoding, bitsPerColorChannel, activeColorMode;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public LUID AdapterLuid;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public nuint CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor, rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX status);
    [DllImport("kernel32.dll")] private static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
    [DllImport("kernel32.dll")] private static extern bool K32GetPerformanceInfo(ref PERFORMANCE_INFORMATION info, uint size);
    [DllImport("kernel32.dll")] private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr rootPowerKey, out IntPtr activeScheme);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid scheme, IntPtr subGroup, IntPtr setting, IntPtr buffer, ref uint bufferSize);
    [DllImport("powrprof.dll")] private static extern uint PowerGetEffectiveOverlayScheme(out Guid overlay);
    [DllImport("user32.dll")] private static extern int GetDisplayConfigBufferSizes(uint flags, out uint pathCount, out uint modeCount);
    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(uint flags, ref uint pathCount, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
        ref uint modeCount, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topology);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO request);
    [DllImport("user32.dll")] private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 request);
    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
