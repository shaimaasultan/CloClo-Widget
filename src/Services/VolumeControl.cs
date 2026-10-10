using System;
using System.Runtime.InteropServices;

namespace CloCloWidget;

// Direct COM interop with Windows' own Core Audio API (mmdeviceapi.h /
// endpointvolume.h) — the same interfaces the volume mixer and the
// hardware mute key go through. There's no public .NET wrapper for this;
// declaring just the vtable slots actually needed, in their real order
// (COM interop maps declared methods to vtable slots positionally, so
// anything called must be declared, but trailing unused slots can be left
// out) is lighter than pulling in a whole audio
// library for this one toggle.
public static class VolumeControl
{
    public record State(double Level, bool Muted);

    public static State? GetState()
    {
        try
        {
            var vol = OpenDefaultEndpointVolume();
            if (vol == null) return null;
            try
            {
                vol.GetMasterVolumeLevelScalar(out var level);
                vol.GetMute(out var muted);
                return new State(level, muted);
            }
            finally { ReleaseComObject(vol); }
        }
        catch
        {
            return null;
        }
    }

    public static State? ToggleMute()
    {
        try
        {
            var vol = OpenDefaultEndpointVolume();
            if (vol == null) return null;

            try
            {
                vol.GetMute(out var muted);
                var eventContext = Guid.Empty;
                vol.SetMute(!muted, ref eventContext);
                vol.GetMasterVolumeLevelScalar(out var level);
                return new State(level, !muted);
            }
            finally { ReleaseComObject(vol); }
        }
        catch
        {
            return null;
        }
    }

    private const int ERender = 0;
    private const int ERoleMultimedia = 1;
    private const int ClsctxAll = 23;

    private static IAudioEndpointVolume? OpenDefaultEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? device = null;
        try
        {
            var hr = enumerator.GetDefaultAudioEndpoint(ERender, ERoleMultimedia, out var endpoint);
            device = endpoint;
            if (hr != 0 || device == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            hr = device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out var vol);
            return hr == 0 ? vol : null;
        }
        finally
        {
            ReleaseComObject(device);
            ReleaseComObject(enumerator);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IntPtr ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.Interface)] out IAudioEndpointVolume ppInterface);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint channelCount);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
