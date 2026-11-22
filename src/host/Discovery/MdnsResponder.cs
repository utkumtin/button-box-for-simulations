using System.Net;
using System.Net.Sockets;

namespace ButtonBox.Discovery;

/// <summary>
/// Hand-written, IPv4-only mDNS responder for one name (<c>&lt;label&gt;.local</c>). It shares UDP 5353 with other
/// responders (Windows DNS cache, Spotify, Steam) and answers only on LAN interfaces, always with the address of the
/// interface the query arrived on.
/// </summary>
public sealed class MdnsResponder : IAsyncDisposable
{
    public const int StandardPort = 5353;
    private static readonly IPAddress Group = IPAddress.Parse("224.0.0.251");

    private readonly string _label;
    private readonly int _port;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _gate = new();
    private Socket? _socket;
    private Task? _receiveTask;
    private Task? _announceTask;
    private CancellationTokenSource? _announceCts;
    private volatile LanAddress[] _joined = [];
    private bool _disposed;

    /// <param name="label">A valid <see cref="MdnsName"/> label; anything else is refused, so a tampered persisted value cannot reach the wire.</param>
    /// <param name="port">Only tests use a port other than 5353.</param>
    public MdnsResponder(string label, int port = StandardPort, TimeProvider? timeProvider = null)
    {
        if (!MdnsName.IsValid(label))
        {
            throw new ArgumentException("Not a valid mDNS label.", nameof(label));
        }

        _label = label;
        _port = port;
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>The LAN addresses whose multicast group join succeeded; queries on any other interface are ignored.</summary>
    public IReadOnlyList<LanAddress> JoinedAddresses => _joined;

    /// <summary>Binds the shared socket (throws <see cref="SocketException"/> if that fails), joins the group on each address and announces.</summary>
    public void Start(IReadOnlyList<LanAddress> addresses)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is not null)
            {
                throw new InvalidOperationException("Already started.");
            }

            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            try
            {
                // Windows: an ICMP port-unreachable caused by one of our unicast replies would otherwise make the next
                // receive throw ConnectionReset, letting a LAN peer stall the loop. SIO_UDP_CONNRESET off.
                if (OperatingSystem.IsWindows())
                {
                    socket.IOControl(unchecked((int)0x9800000C), new byte[4], null);
                }

                socket.ExclusiveAddressUse = false;
                socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255); // RFC 6762 section 11
                socket.Bind(new IPEndPoint(IPAddress.Any, _port));
            }
            catch
            {
                socket.Dispose();
                throw;
            }

            _socket = socket;
            ApplyAddresses(socket, addresses);
            _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, _cts.Token));
        }
    }

    /// <summary>Re-joins the group for the new set (leaving interfaces that went away) and announces again.</summary>
    public void UpdateAddresses(IReadOnlyList<LanAddress> addresses)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_socket is null)
            {
                throw new InvalidOperationException("Not started.");
            }

            ApplyAddresses(_socket, addresses);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Task?[] tasks;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _cts.Cancel();
            _announceCts?.Cancel();
            _socket?.Dispose();
            tasks = [_receiveTask, _announceTask];
        }

        foreach (var t in tasks)
        {
            if (t is null)
            {
                continue;
            }

            try
            {
                await t.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Shutting down: the loops end with cancellation or a disposed-socket error.
            }
        }

        _announceCts?.Dispose();
        _cts.Dispose();
    }

    /// <summary>
    /// The whole decision for one received datagram, without sockets. Returns null (and never throws) unless the packet is
    /// a valid query for our name that arrived on a LAN interface from a source on that interface's subnet.
    /// </summary>
    public static MdnsReply? TryAnswer(
        ReadOnlySpan<byte> packet,
        IPEndPoint source,
        int interfaceIndex,
        string label,
        IReadOnlyList<LanAddress> lan,
        out LanAddress? arrival)
    {
        arrival = null;
        try
        {
            if (source.Port == 0)
            {
                return null;
            }

            // Several addresses may share one interface: the one whose subnet holds the asker is the one it can reach.
            // A source outside every subnet of the interface is not on the link and is ignored (RFC 6762 section 11).
            arrival = lan.FirstOrDefault(a => a.InterfaceIndex == interfaceIndex && a.Contains(source.Address));
            if (arrival is null || !MdnsPacket.TryParseQuery(packet, out var query))
            {
                arrival = null;
                return null;
            }

            var reply = MdnsResponseBuilder.Build(query, label, arrival.Address, legacyUnicast: source.Port != StandardPort);
            if (reply is null)
            {
                arrival = null;
            }

            return reply;
        }
        catch (Exception)
        {
            arrival = null;
            return null; // network input must never take the responder down
        }
    }

    private void ApplyAddresses(Socket socket, IReadOnlyList<LanAddress> addresses)
    {
        var wanted = addresses.DistinctBy(a => a.Address).ToList();
        var result = new List<LanAddress>();

        foreach (var old in _joined)
        {
            if (!wanted.Any(w => w.Address.Equals(old.Address)))
            {
                try
                {
                    socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.DropMembership, new MulticastOption(Group, old.Address));
                }
                catch (SocketException)
                {
                    // The interface is already gone; the OS dropped the membership with it.
                }
            }
        }

        foreach (var w in wanted)
        {
            if (_joined.Any(j => j.Address.Equals(w.Address)))
            {
                result.Add(w);
                continue;
            }

            try
            {
                socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, w.Address));
                result.Add(w);
            }
            catch (SocketException)
            {
                // Not joined, so not in JoinedAddresses: queries on this interface are not answered.
            }
        }

        _joined = [.. result];

        _announceCts?.Cancel();
        _announceCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        var token = _announceCts.Token;
        var snapshot = _joined;
        _announceTask = Task.Run(() => AnnounceAsync(socket, snapshot, token));
    }

    private async Task AnnounceAsync(Socket socket, LanAddress[] addresses, CancellationToken ct)
    {
        try
        {
            for (var i = 0; i < 2; i++)
            {
                if (i > 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), _time, ct).ConfigureAwait(false);
                }

                foreach (var a in addresses)
                {
                    await SendMulticastAsync(socket, a, MdnsResponseBuilder.BuildAnnouncement(_label, a.Address), ct).ConfigureAwait(false);
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Cancelled by a newer announcement or shutdown, or the interface vanished.
        }
    }

    private async Task ReceiveLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[MdnsPacket.MaxPacketSize + 1]; // one extra byte so an oversized datagram is detectable
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await socket.ReceiveMessageFromAsync(buffer, SocketFlags.None, any, ct).ConfigureAwait(false);
                if (r.ReceivedBytes > MdnsPacket.MaxPacketSize || r.RemoteEndPoint is not IPEndPoint source)
                {
                    continue;
                }

                var reply = TryAnswer(buffer.AsSpan(0, r.ReceivedBytes), source, r.PacketInformation.Interface, _label, _joined, out var arrival);
                if (reply is null || arrival is null)
                {
                    continue;
                }

                if (reply.Unicast)
                {
                    await SendUnicastAsync(socket, source, reply.Bytes, ct).ConfigureAwait(false);
                }
                else
                {
                    await SendMulticastAsync(socket, arrival, reply.Bytes, ct).ConfigureAwait(false);
                }
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (Exception)
            {
                // One bad packet or a transient socket error (e.g. oversize datagram, ICMP reset) must not stop the loop.
                try
                {
                    await Task.Delay(50, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task SendUnicastAsync(Socket socket, IPEndPoint target, byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendToAsync(bytes, SocketFlags.None, target, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    // The outgoing multicast interface is a socket-wide option, so set-and-send is done under a lock.
    private async Task SendMulticastAsync(Socket socket, LanAddress via, byte[] bytes, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, via.Address.GetAddressBytes());
            await socket.SendToAsync(bytes, SocketFlags.None, new IPEndPoint(Group, _port), ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }
}
