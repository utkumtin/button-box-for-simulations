using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace ButtonBox.Discovery;

public readonly record struct SnapshotAddress(IPAddress Address, int PrefixLength);

/// <summary>The facts about one network interface that the LAN rule needs; lets the rule be tested without real adapters.</summary>
public sealed record InterfaceSnapshot(
    int Index,
    string Name,
    OperationalStatus Status,
    NetworkInterfaceType Type,
    IReadOnlyList<SnapshotAddress> UnicastAddresses,
    IReadOnlyList<IPAddress> Gateways);

public static class LanInterfaces
{
    /// <summary>
    /// LAN rule: interface is Up, not loopback or tunnel, has a non-0.0.0.0 IPv4 gateway, and contributes each of its private
    /// IPv4 addresses (10/8, 172.16/12, 192.168/16; never 169.254/16). Sorted by interface index, then address.
    /// </summary>
    public static IReadOnlyList<LanAddress> Select(IEnumerable<InterfaceSnapshot> interfaces)
    {
        var result = new List<LanAddress>();
        foreach (var ni in interfaces)
        {
            if (ni.Status != OperationalStatus.Up || ni.Type is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            if (!ni.Gateways.Any(g => g.AddressFamily == AddressFamily.InterNetwork && !g.Equals(IPAddress.Any)))
            {
                continue;
            }

            foreach (var u in ni.UnicastAddresses)
            {
                if (IsPrivateIPv4(u.Address))
                {
                    result.Add(new LanAddress(ni.Index, u.Address, u.PrefixLength));
                }
            }
        }

        result.Sort(static (x, y) =>
        {
            var c = x.InterfaceIndex.CompareTo(y.InterfaceIndex);
            return c != 0 ? c : x.Address.GetAddressBytes().AsSpan().SequenceCompareTo(y.Address.GetAddressBytes());
        });
        return result;
    }

    public static IReadOnlyList<LanAddress> Current() => Select(Snapshot());

    public static bool IsPrivateIPv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168);
    }

    private static List<InterfaceSnapshot> Snapshot()
    {
        var list = new List<InterfaceSnapshot>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                var props = ni.GetIPProperties();
                var index = props.GetIPv4Properties().Index; // throws if the interface has no IPv4
                list.Add(new InterfaceSnapshot(
                    index,
                    ni.Name,
                    ni.OperationalStatus,
                    ni.NetworkInterfaceType,
                    [.. props.UnicastAddresses.Select(u => new SnapshotAddress(u.Address, u.PrefixLength))],
                    [.. props.GatewayAddresses.Select(g => g.Address)]));
            }
            catch (NetworkInformationException)
            {
                // Interface vanished or has no IPv4: not a LAN candidate.
            }
        }

        return list;
    }
}
