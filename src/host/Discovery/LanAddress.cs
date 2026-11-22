using System.Net;
using System.Net.Sockets;

namespace ButtonBox.Discovery;

/// <summary>A private IPv4 address of a LAN interface. <see cref="InterfaceIndex"/> is the IPv4 interface index (what <c>IPPacketInformation.Interface</c> reports).</summary>
public sealed record LanAddress(int InterfaceIndex, IPAddress Address, int PrefixLength)
{
    /// <summary>True if <paramref name="other"/> is an IPv4 address inside this address's subnet.</summary>
    public bool Contains(IPAddress other)
    {
        if (other.AddressFamily != AddressFamily.InterNetwork || Address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var prefix = Math.Clamp(PrefixLength, 0, 32);
        Span<byte> a = stackalloc byte[4];
        Span<byte> b = stackalloc byte[4];
        if (!Address.TryWriteBytes(a, out _) || !other.TryWriteBytes(b, out _))
        {
            return false;
        }

        var full = prefix / 8;
        if (!a[..full].SequenceEqual(b[..full]))
        {
            return false;
        }

        var rest = prefix % 8;
        if (rest == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - rest));
        return (a[full] & mask) == (b[full] & mask);
    }
}
