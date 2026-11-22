using System.Net;
using System.Net.NetworkInformation;
using Xunit;

namespace ButtonBox.Discovery.Tests;

public sealed class LanInterfacesTests
{
    private static readonly IPAddress Gateway = IPAddress.Parse("192.168.1.1");

    private static InterfaceSnapshot Nic(
        int index,
        string address,
        IPAddress? gateway = null,
        OperationalStatus status = OperationalStatus.Up,
        NetworkInterfaceType type = NetworkInterfaceType.Ethernet,
        int prefix = 24)
        => new(index, "nic" + index, status, type, [new SnapshotAddress(IPAddress.Parse(address), prefix)], gateway is null ? [] : [gateway]);

    // The server must bind to the real LAN: this is the one interface that qualifies.
    [Fact]
    public void Ethernet_with_gateway_and_private_address_is_selected()
    {
        var lan = LanInterfaces.Select([Nic(7, "192.168.1.20", Gateway)]);

        var a = Assert.Single(lan);
        Assert.Equal(new LanAddress(7, IPAddress.Parse("192.168.1.20"), 24), a);
    }

    // Each exclusion is a way the app could expose itself (or advertise an unreachable name) on a network that is not
    // the user's LAN. Every case is the only candidate, so a wrongly accepted rule shows up as a non-empty result.
    [Theory]
    [InlineData("hyper-v: private IP but no gateway", "172.28.80.1", null, OperationalStatus.Up, NetworkInterfaceType.Ethernet)]
    [InlineData("gateway of 0.0.0.0", "192.168.1.20", "0.0.0.0", OperationalStatus.Up, NetworkInterfaceType.Ethernet)]
    [InlineData("tunnel (VPN)", "10.8.0.2", "192.168.1.1", OperationalStatus.Up, NetworkInterfaceType.Tunnel)]
    [InlineData("loopback", "127.0.0.1", "192.168.1.1", OperationalStatus.Up, NetworkInterfaceType.Loopback)]
    [InlineData("link-local 169.254/16", "169.254.12.9", "192.168.1.1", OperationalStatus.Up, NetworkInterfaceType.Ethernet)]
    [InlineData("public IP", "203.0.113.9", "192.168.1.1", OperationalStatus.Up, NetworkInterfaceType.Ethernet)]
    [InlineData("172.32/16 is outside 172.16/12", "172.32.0.5", "192.168.1.1", OperationalStatus.Up, NetworkInterfaceType.Ethernet)]
    [InlineData("interface down", "192.168.1.20", "192.168.1.1", OperationalStatus.Down, NetworkInterfaceType.Ethernet)]
    public void Excluded_interfaces(string why, string address, string? gateway, OperationalStatus status, NetworkInterfaceType type)
    {
        Assert.False(LanInterfaces.Select([Nic(3, address, gateway is null ? null : IPAddress.Parse(gateway), status, type)]).Any(), why);
    }

    [Theory]
    [InlineData("10.0.0.1")]
    [InlineData("10.255.255.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.254")]
    [InlineData("192.168.0.1")]
    public void Private_ranges_are_accepted(string address)
    {
        Assert.Single(LanInterfaces.Select([Nic(3, address, Gateway)]));
    }

    // An IPv6-only gateway does not make an IPv4 LAN: the responder advertises IPv4 only.
    [Fact]
    public void IPv6_gateway_alone_does_not_count()
    {
        Assert.Empty(LanInterfaces.Select([Nic(3, "192.168.1.20", IPAddress.Parse("fe80::1"))]));
    }

    // Only the IPv4 private address matters, even if the interface also carries IPv6 or public addresses.
    [Fact]
    public void Only_private_IPv4_addresses_of_a_qualifying_interface_are_taken()
    {
        var nic = new InterfaceSnapshot(
            4,
            "wifi",
            OperationalStatus.Up,
            NetworkInterfaceType.Wireless80211,
            [
                new SnapshotAddress(IPAddress.Parse("fe80::1"), 64),
                new SnapshotAddress(IPAddress.Parse("203.0.113.9"), 24),
                new SnapshotAddress(IPAddress.Parse("10.0.0.5"), 24),
            ],
            [Gateway]);

        Assert.Equal([new LanAddress(4, IPAddress.Parse("10.0.0.5"), 24)], LanInterfaces.Select([nic]));
    }

    // The result is compared to detect network changes; an unstable order would look like a change on every poll.
    [Fact]
    public void Order_is_deterministic_regardless_of_input_order()
    {
        var eth = Nic(7, "192.168.1.20", Gateway);
        var wifi = Nic(12, "10.0.0.5", Gateway);

        Assert.Equal(LanInterfaces.Select([eth, wifi]), LanInterfaces.Select([wifi, eth]));
        Assert.Equal([7, 12], LanInterfaces.Select([wifi, eth]).Select(a => a.InterfaceIndex));
    }

    // The subnet test decides which neighbours we answer.
    [Theory]
    [InlineData("192.168.1.20", 24, "192.168.1.250", true)]
    [InlineData("192.168.1.20", 24, "192.168.2.1", false)]
    [InlineData("10.0.0.5", 20, "10.0.15.9", true)]
    [InlineData("10.0.0.5", 20, "10.0.16.9", false)]
    [InlineData("10.0.0.5", 8, "10.200.0.1", true)]
    public void Contains_respects_the_prefix_length(string own, int prefix, string other, bool expected)
    {
        Assert.Equal(expected, new LanAddress(1, IPAddress.Parse(own), prefix).Contains(IPAddress.Parse(other)));
    }

    // Smoke test of the real mapping: it must not throw on whatever adapters this machine has. It reads, never sends.
    [Fact]
    public void Current_runs_on_this_machine_and_only_returns_private_addresses()
    {
        Assert.All(LanInterfaces.Current(), a => Assert.True(LanInterfaces.IsPrivateIPv4(a.Address)));
    }
}
