using System.Net;

namespace EasyMouse.App.Logic;

public enum StopReason { None, NoNetwork, PublicNetwork, EmergencyStop }

/// <summary>
/// Where (and whether) the server should listen. Never <c>0.0.0.0</c>: either one chosen interface address,
/// or no listener at all. The listener stops on a Public network profile and after the emergency hotkey.
/// </summary>
public sealed record ListenerPlan(IPAddress? Address, int Port, int PrefixLength, IReadOnlyList<string> AllowedHosts, StopReason Reason)
{
    public bool Listening => Address is not null;

    public static ListenerPlan Decide(AdapterInfo? chosen, NetCategory category, bool emergencyStopped, int port, string machineName)
    {
        if (emergencyStopped) return Stopped(StopReason.EmergencyStop, port);
        if (chosen is null) return Stopped(StopReason.NoNetwork, port);
        if (category == NetCategory.Public) return Stopped(StopReason.PublicNetwork, port);
        return new ListenerPlan(chosen.Address, port, chosen.PrefixLength,
            OriginSelector.AllowedHosts(machineName, chosen.Address.ToString()), StopReason.None);
    }

    private static ListenerPlan Stopped(StopReason reason, int port) => new(null, port, 0, [], reason);

    /// <summary>True when the running server can stay as it is (same address, port and name set).</summary>
    public bool SameTarget(ListenerPlan? other)
        => other is not null && Equals(Address, other.Address) && Port == other.Port && PrefixLength == other.PrefixLength
           && Reason == other.Reason && AllowedHosts.SequenceEqual(other.AllowedHosts);
}
