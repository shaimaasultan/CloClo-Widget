using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Windows.Networking.Connectivity;

namespace CloCloWidget;

// How this PC reaches the internet right now, for the small icon in the
// widget's top-left column. Read from Windows' own record of the current
// connection (the same one WindowsStatus.ReadNetwork shows in the
// dashboard) and the Network List Manager for private/public. Nothing is
// sent: CloClo never tests internet access itself. Network names are left
// out, as in Windows status.
internal static class NetworkStatus
{
    public enum Kind { Offline, Ethernet, WiFi, Cellular, Other }

    public enum Category { Unknown, Public, Private, Domain }

    // Bars is 0-4 (Wi-Fi and cellular only). Generation is "5G", "4G",
    // "3G" or "2G" for cellular. Limited means connected without full
    // internet access (a sign-in page, or the local network only).
    public record State(Kind Kind, int? Bars, string? Generation, bool Limited, string? LimitedReason,
        Category Category, bool Metered, bool Roaming, bool OverDataLimit);

    public static readonly State Offline = new(Kind.Offline, null, null, false, null, Category.Unknown, false, false, false);

    // Never throws; anything unreadable is left at its default.
    public static State Read()
    {
        ConnectionProfile? profile;
        try { profile = NetworkInformation.GetInternetConnectionProfile(); }
        catch { return Offline; }
        if (profile == null) return Offline;

        var level = NetworkConnectivityLevel.None;
        try { level = profile.GetNetworkConnectivityLevel(); } catch { }
        if (level == NetworkConnectivityLevel.None) return Offline;
        var limited = level != NetworkConnectivityLevel.InternetAccess;
        var limitedReason = level switch
        {
            NetworkConnectivityLevel.ConstrainedInternetAccess => "limited internet",
            NetworkConnectivityLevel.LocalAccess => "no internet",
            _ => null,
        };

        var kind = Kind.Other;
        string? generation = null;
        try
        {
            if (profile.IsWlanConnectionProfile) kind = Kind.WiFi;
            else if (profile.IsWwanConnectionProfile)
            {
                kind = Kind.Cellular;
                try { generation = Generation((uint)profile.WwanConnectionProfileDetails.GetCurrentDataClass()); } catch { }
            }
            else
            {
                // IANA ifType: 6 is Ethernet; 71 Wi-Fi and 243/244 mobile
                // broadband in case the flags above weren't set.
                kind = profile.NetworkAdapter?.IanaInterfaceType switch
                {
                    6 => Kind.Ethernet,
                    71 => Kind.WiFi,
                    243 or 244 => Kind.Cellular,
                    _ => Kind.Other,
                };
            }
        }
        catch { }

        int? bars = null;
        if (kind is Kind.WiFi or Kind.Cellular)
        {
            // Windows reports 0-5; the icon draws four, like the taskbar.
            try { if (profile.GetSignalBars() is byte b) bars = (int)Math.Ceiling(Math.Min((int)b, 5) * 4 / 5.0); } catch { }
        }

        bool metered = false, roaming = false, overLimit = false;
        try
        {
            var cost = profile.GetConnectionCost();
            metered = cost.NetworkCostType is NetworkCostType.Fixed or NetworkCostType.Variable;
            roaming = cost.Roaming;
            overLimit = cost.OverDataLimit;
        }
        catch { }

        Guid? adapter = null;
        try { if (profile.NetworkAdapter is { } a) adapter = a.NetworkAdapterId; } catch { }

        return new State(kind, bars, generation, limited, limitedReason,
            adapter is Guid id ? ReadCategory(id) : Category.Unknown, metered || roaming, roaming, overLimit);
    }

    // WwanDataClass flags, by value: the 19041 SDK this builds against
    // predates the 5G names. 0x20 is LTE (WinRT calls it LteAdvanced),
    // 0x40/0x80 5G non-standalone/standalone.
    private static string? Generation(uint dataClass) =>
        (dataClass & 0xC0) != 0 ? "5G"
        : (dataClass & 0x20) != 0 ? "4G"
        : (dataClass & (0x4u | 0x8 | 0x10 | 0x20000 | 0x40000 | 0x80000 | 0x100000 | 0x200000 | 0x400000)) != 0 ? "3G"
        : (dataClass & (0x1u | 0x2 | 0x10000)) != 0 ? "2G"
        : null;

    // ---- Private / public (Network List Manager, as Get-NetConnectionProfile) ----

    private static Category ReadCategory(Guid adapterId)
    {
        object? manager = null;
        var released = new List<object>();
        try
        {
            manager = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!);
            if (manager is not INetworkListManager list) return Category.Unknown;
            var connections = list.GetNetworkConnections();
            released.Add(connections);
            while (true)
            {
                connections.Next(1, out var connection, out var fetched);
                if (fetched == 0 || connection == null) break;
                released.Add(connection);
                if (connection.GetAdapterId() != adapterId) continue;
                var network = connection.GetNetwork();
                released.Add(network);
                return network.GetCategory() switch
                {
                    0 => Category.Public,
                    1 => Category.Private,
                    2 => Category.Domain,
                    _ => Category.Unknown,
                };
            }
            return Category.Unknown;
        }
        catch { return Category.Unknown; }
        finally
        {
            foreach (var o in released) try { Marshal.ReleaseComObject(o); } catch { }
            if (manager != null) try { Marshal.ReleaseComObject(manager); } catch { }
        }
    }

    // netlistmgr.h. Only the methods up to the last one used are declared;
    // the vtable order must match the header.
    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid networkId);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.IUnknown)] object NewEnum();
        void Next(uint celt, out INetworkConnection? connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        Guid GetConnectionId();
        Guid GetAdapterId();
    }

    [ComImport, Guid("DCB00002-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetwork
    {
        [return: MarshalAs(UnmanagedType.BStr)] string GetName();
        void SetName([MarshalAs(UnmanagedType.BStr)] string name);
        [return: MarshalAs(UnmanagedType.BStr)] string GetDescription();
        void SetDescription([MarshalAs(UnmanagedType.BStr)] string description);
        Guid GetNetworkId();
        int GetDomainType();
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworkConnections();
        void GetTimeCreatedAndConnected(out uint createdLow, out uint createdHigh, out uint connectedLow, out uint connectedHigh);
        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }
        int GetConnectivity();
        int GetCategory();
    }

    // ---- Wording ----

    // "Wi-Fi, 3 of 4 bars, private network, not metered".
    public static string SpokenName(State s)
    {
        if (s.Kind == Kind.Offline) return "Network: offline, no internet connection";
        var parts = new List<string> { Title(s) };
        if (s.Bars is int bars) parts.Add($"{bars} of 4 bars");
        if (s.LimitedReason != null) parts.Add(s.LimitedReason);
        if (CategoryWords(s.Category) is { } category) parts.Add(category);
        parts.Add(MeteredWords(s));
        return string.Join(", ", parts);
    }

    // "Wi-Fi", "Wired (Ethernet)", "Cellular 5G"...
    public static string Title(State s) => s.Kind switch
    {
        Kind.Offline => "Offline",
        Kind.Ethernet => "Wired (Ethernet)",
        Kind.WiFi => "Wi-Fi",
        Kind.Cellular => s.Generation switch
        {
            "4G" => "Cellular 4G/LTE",
            { } g => "Cellular " + g,
            null => "Cellular",
        },
        _ => "Connected",
    };

    public static string? CategoryWords(Category c) => c switch
    {
        Category.Public => "public network",
        Category.Private => "private network",
        Category.Domain => "work domain network",
        _ => null,
    };

    public static string MeteredWords(State s) =>
        s.Roaming ? "metered, roaming" + (s.OverDataLimit ? ", over data limit" : "")
        : s.Metered ? "metered" + (s.OverDataLimit ? ", over data limit" : "")
        : "not metered";
}
