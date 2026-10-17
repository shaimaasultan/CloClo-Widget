using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;

namespace CloCloWidget;

// Bluetooth and headphones for the small icons beside the internet icon:
// whether the Bluetooth radio is on and how many devices are connected to
// it, and whether the default playback device is headphones or a headset.
// Event-driven: Windows' radio StateChanged, a DeviceWatcher over connected
// Bluetooth devices, and the audio endpoint notifications. Only read, never
// changed; device names are left out, like network names.
internal sealed class DeviceStatus : IDisposable
{
    // RadioOn is null when the PC has no Bluetooth radio.
    public record BluetoothState(bool? RadioOn, int Connected);

    public enum AudioKind { Headphones, Headset }

    // Null when the default playback device isn't headphones or a headset.
    public record HeadphonesState(AudioKind Kind, bool Bluetooth);

    // Raised on a background thread whenever either may have changed.
    public event Action? Changed;

    private readonly object _gate = new();
    private readonly Dictionary<string, string> _connected = new(); // device id -> Bluetooth address
    private Radio? _radio;
    private DeviceWatcher? _classic;
    private DeviceWatcher? _lowEnergy;
    private MMDeviceEnumerator? _audio;
    private MMDeviceNotificationClient? _audioNotifications;
    private bool _disposed;

    public async Task StartAsync()
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            _radio = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_radio != null) _radio.StateChanged += OnRadioChanged;
        }
        catch { /* No radio access: the Bluetooth icon stays hidden. */ }

        _classic = Watch(BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));
        _lowEnergy = Watch(BluetoothLEDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected));

        try
        {
            _audio = new MMDeviceEnumerator();
            _audioNotifications = _audio.CreateNotificationClient(useSynchronizationContext: false);
            _audioNotifications.DefaultDeviceChanged += (_, _) => Raise();
            _audioNotifications.DeviceStateChanged += (_, _) => Raise();
            _audioNotifications.DeviceAdded += (_, _) => Raise();
            _audioNotifications.DeviceRemoved += (_, _) => Raise();
        }
        catch { /* Without notifications the headphones icon shows the state at startup. */ }
        Raise();
    }

    private DeviceWatcher? Watch(string selector)
    {
        try
        {
            var watcher = DeviceInformation.CreateWatcher(selector);
            watcher.Added += (_, info) => { lock (_gate) _connected[info.Id] = Address(info.Id); Raise(); };
            watcher.Removed += (_, update) => { lock (_gate) _connected.Remove(update.Id); Raise(); };
            watcher.Updated += (_, _) => { };
            watcher.Start();
            return watcher;
        }
        catch { return null; }
    }

    // A device id ends with its Bluetooth address ("...-aa:bb:cc:dd:ee:ff");
    // a dual-mode device is listed by both watchers, so count addresses.
    private static string Address(string id)
    {
        var dash = id.LastIndexOf('-');
        return dash >= 0 ? id[(dash + 1)..].ToLowerInvariant() : id;
    }

    private void OnRadioChanged(Radio sender, object args) => Raise();

    private void Raise()
    {
        if (!_disposed) Changed?.Invoke();
    }

    public BluetoothState ReadBluetooth()
    {
        bool? on = null;
        try { if (_radio != null) on = _radio.State == RadioState.On; } catch { }
        int count;
        lock (_gate) count = _connected.Values.Distinct().Count();
        return new BluetoothState(on, on == true ? count : 0);
    }

    // PKEY_AudioEndpoint_FormFactor and PKEY_Device_EnumeratorName.
    private static readonly PropertyKey FormFactorKey = new(new Guid("1da5d803-d492-4edd-8c23-e0c0ffee7f0e"), 0);
    private static readonly PropertyKey EnumeratorKey = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    public HeadphonesState? ReadHeadphones()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (!enumerator.HasDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)) return null;
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var properties = device.Properties;
            var formFactor = properties.Contains(FormFactorKey) ? Convert.ToInt32(properties[FormFactorKey].Value) : -1;
            // EndpointFormFactor: 3 Headphones, 5 Headset.
            AudioKind? kind = formFactor switch { 3 => AudioKind.Headphones, 5 => AudioKind.Headset, _ => null };
            if (kind == null) return null;
            var enumeratorName = properties.Contains(EnumeratorKey) ? properties[EnumeratorKey].Value as string : null;
            var bluetooth = enumeratorName?.StartsWith("BTH", StringComparison.OrdinalIgnoreCase) == true;
            return new HeadphonesState(kind.Value, bluetooth);
        }
        catch { return null; }
    }

    public void Dispose()
    {
        _disposed = true;
        try { if (_radio != null) _radio.StateChanged -= OnRadioChanged; } catch { }
        foreach (var watcher in new[] { _classic, _lowEnergy })
            try { if (watcher?.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) watcher.Stop(); } catch { }
        try { _audioNotifications?.Dispose(); } catch { }
        _audio?.Dispose();
    }

    // ---- Wording ----

    public static string BluetoothSpoken(BluetoothState s) =>
        "Bluetooth on, " + (s.Connected switch { 0 => "no devices connected", 1 => "1 device connected", var n => $"{n} devices connected" });

    public static string HeadphonesSpoken(HeadphonesState s) =>
        (s.Kind == AudioKind.Headset ? "Headset" : "Headphones") + " connected" + (s.Bluetooth ? " over Bluetooth" : "");
}
