using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using EasyMouse.App.Logic;

namespace EasyMouse.App.Platform;

/// <summary>Collects adapters (System.Net.NetworkInformation) and Windows network profiles (Network List Manager COM).</summary>
public static class NetworkProbe
{
    public static NetworkSnapshot Collect()
    {
        var adapters = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback) continue;
            }
            catch (NetworkInformationException) { continue; } // an adapter that vanishes while it is being read
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            var ua = props.UnicastAddresses.FirstOrDefault(a =>
                a.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !a.Address.Equals(IPAddress.Any));
            if (ua is null) continue;
            bool gateway = props.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
            int index = 0;
            try { index = props.GetIPv4Properties().Index; } catch (NetworkInformationException) { }
            adapters.Add(new AdapterInfo(nic.Id, nic.Name, nic.Description, nic.NetworkInterfaceType,
                ua.Address, ua.PrefixLength, gateway, index));
        }
        return new NetworkSnapshot(adapters, ReadCategories());
    }

    /// <summary>Adapter id (GUID in braces) to profile category. Empty when the COM service is unavailable.</summary>
    private static Dictionary<string, NetCategory> ReadCategories()
    {
        var result = new Dictionary<string, NetCategory>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var manager = (INetworkListManager)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"))!)!;
            var connections = manager.GetNetworkConnections();
            while (true)
            {
                connections.Next(1, out var connection, out var fetched);
                if (fetched == 0 || connection is null) break;
                var id = connection.GetAdapterId().ToString("B").ToUpperInvariant();
                result[id] = connection.GetNetwork().GetCategory() switch
                {
                    0 => NetCategory.Public,
                    1 => NetCategory.Private,
                    2 => NetCategory.Domain,
                    _ => NetCategory.Unknown,
                };
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or UnauthorizedAccessException)
        {
            // Category stays Unknown: we then listen, relying on the firewall rule, rather than refuse to run.
            result.Clear();
        }
        return result;
    }

    // ---- Network List Manager (netlistmgr.h). Only the vtable slots we call carry real signatures. ----


    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetworks(int flags);
        [return: MarshalAs(UnmanagedType.Interface)] object GetNetwork(Guid id);
        IEnumNetworkConnections GetNetworkConnections();
    }

    [ComImport, Guid("DCB00006-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IEnumNetworkConnections
    {
        [return: MarshalAs(UnmanagedType.Interface)] object get__NewEnum();
        void Next(uint count, [MarshalAs(UnmanagedType.Interface)] out INetworkConnection? connection, out uint fetched);
    }

    [ComImport, Guid("DCB00005-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkConnection
    {
        INetwork GetNetwork();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
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
        void GetTimeCreatedAndConnected(out uint a, out uint b, out uint c, out uint d);
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnectedToInternet();
        [return: MarshalAs(UnmanagedType.VariantBool)] bool get_IsConnected();
        int GetConnectivity();
        int GetCategory();
    }
}
