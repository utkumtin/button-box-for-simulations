using System.Net;
using Xunit;

namespace ButtonBox.Discovery.Tests;

public sealed class MdnsResponderTests
{
    private const string Label = "buttonbox-c295";
    private const string Fqdn = "buttonbox-c295.local";
    private const int Eth = 7;
    private const int Wifi = 12;

    private static readonly LanAddress EthAddress = new(Eth, IPAddress.Parse("192.168.1.20"), 24);
    private static readonly LanAddress WifiAddress = new(Wifi, IPAddress.Parse("10.0.0.5"), 24);
    private static readonly IPEndPoint PhoneOnEth = new(IPAddress.Parse("192.168.1.50"), 5353);
    private static readonly IPEndPoint PhoneOnWifi = new(IPAddress.Parse("10.0.0.77"), 5353);

    private static MdnsReply? Answer(byte[] packet, IPEndPoint source, int ifIndex, params LanAddress[] lan)
        => MdnsResponder.TryAnswer(packet, source, ifIndex, Label, lan, out _);

    // This is the packet that the iPhone (iOS 27) sent in spike S1. If we ever answer it differently, pairing by name
    // breaks on real phones: the A record must be there, AAAA must be denied by NSEC (otherwise iOS waits for an IPv6
    // answer), the HTTPS type (65) must stay silent and, because of the QU bit, the reply goes straight to the phone.
    [Fact]
    public void IPhone_packet_gets_A_and_NSEC_for_AAAA_and_nothing_for_HTTPS_sent_unicast()
    {
        var packet = DnsTestHelper.Query(Fqdn, 0, 0, (65, 0x8001), (28, 0x8001), (1, 0x8001));

        var reply = Answer(packet, PhoneOnEth, Eth, EthAddress, WifiAddress);

        Assert.NotNull(reply);
        Assert.True(reply.Unicast);
        var d = DnsTestHelper.Decode(reply.Bytes);
        Assert.Equal(0x8400, d.Flags);
        Assert.Equal(0, d.Id);
        Assert.Empty(d.Questions);
        var a = Assert.Single(d.Answers, r => r.Type == 1);
        Assert.Equal(Fqdn, a.Name);
        Assert.Equal(0x8001, a.Class); // cache-flush bit set
        Assert.Equal(120u, a.Ttl);
        Assert.Equal(EthAddress.Address.GetAddressBytes(), a.Data);
        var nsec = Assert.Single(d.Answers, r => r.Type == 47);
        Assert.Equal([0, 1, 0x40], nsec.Data[^3..]); // bitmap: only type 1 exists, so no AAAA
        Assert.DoesNotContain(d.Answers.Concat(d.Additional), r => r.Type is 65 or 28);
    }

    // A plain A question without the QU bit is a multicast question; answering it unicast would starve other listeners
    // of the cache entry, answering multicast to a QU asker would waste the phone's request for a direct reply.
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void Reply_is_unicast_only_when_the_QU_bit_is_set(bool qu, bool expectedUnicast)
    {
        var reply = Answer(DnsTestHelper.Query(Fqdn, 1, qu), PhoneOnEth, Eth, EthAddress);

        Assert.NotNull(reply);
        Assert.Equal(expectedUnicast, reply.Unicast);
    }

    // The A question gets the NSEC as a hint in the additional section (so the phone need not ask AAAA separately).
    [Fact]
    public void A_question_carries_NSEC_as_additional_record()
    {
        var reply = Answer(DnsTestHelper.Query(Fqdn, 1), PhoneOnEth, Eth, EthAddress);

        var d = DnsTestHelper.Decode(reply!.Bytes);
        Assert.Equal(1, Assert.Single(d.Answers).Type);
        Assert.Equal(47, Assert.Single(d.Additional).Type);
    }

    // AAAA alone must still be answered, with "no such record", or the asker retries and times out.
    [Fact]
    public void AAAA_only_question_gets_NSEC_and_no_address()
    {
        var reply = Answer(DnsTestHelper.Query(Fqdn, 28), PhoneOnEth, Eth, EthAddress);

        var d = DnsTestHelper.Decode(reply!.Bytes);
        Assert.Equal(47, Assert.Single(d.Answers).Type);
    }

    [Fact]
    public void ANY_question_is_answered_with_the_A_record()
    {
        var reply = Answer(DnsTestHelper.Query(Fqdn, 255), PhoneOnEth, Eth, EthAddress);

        Assert.Contains(DnsTestHelper.Decode(reply!.Bytes).Answers, r => r.Type == 1);
    }

    [Theory]
    [InlineData(65)]
    [InlineData(16)]
    [InlineData(12)]
    public void Other_record_types_get_no_answer(ushort type)
    {
        Assert.Null(Answer(DnsTestHelper.Query(Fqdn, type), PhoneOnEth, Eth, EthAddress));
    }

    // DNS names are case-insensitive and phones do not normalise them; a case-sensitive match would silently not answer.
    [Fact]
    public void Name_match_is_case_insensitive()
    {
        var reply = Answer(DnsTestHelper.Query("ButtonBox-C295.LOCAL", 1), PhoneOnEth, Eth, EthAddress);

        Assert.NotNull(reply);
    }

    // We must not answer for (or reveal that we exist under) names that are not ours, e.g. someone probing neighbours.
    [Theory]
    [InlineData("buttonbox-0000.local")]
    [InlineData("other.local")]
    [InlineData("buttonbox-c295.local.evil")]
    [InlineData("x.buttonbox-c295.local")]
    [InlineData("buttonbox-c295")]
    public void Other_names_get_no_answer(string name)
    {
        Assert.Null(Answer(DnsTestHelper.Query(name, 1), PhoneOnEth, Eth, EthAddress));
    }

    // Responses on the multicast group (including our own announcements looping back) must never trigger another
    // response, or two responders would answer each other forever.
    [Fact]
    public void Responses_are_ignored()
    {
        var response = DnsTestHelper.Query(Fqdn, 1, flags: 0x8400);

        Assert.Null(Answer(response, PhoneOnEth, Eth, EthAddress));
    }

    // Spike lesson: a PC with Ethernet and Wi-Fi on different subnets. Advertising the Ethernet address to a phone on Wi-Fi
    // would make the name resolve to an address the phone cannot reach.
    [Fact]
    public void Answer_carries_the_address_of_the_arrival_interface()
    {
        var onWifi = Answer(DnsTestHelper.Query(Fqdn, 1), PhoneOnWifi, Wifi, EthAddress, WifiAddress);
        var onEth = Answer(DnsTestHelper.Query(Fqdn, 1), PhoneOnEth, Eth, EthAddress, WifiAddress);

        Assert.Equal(WifiAddress.Address.GetAddressBytes(), Assert.Single(DnsTestHelper.Decode(onWifi!.Bytes).Answers).Data);
        Assert.Equal(EthAddress.Address.GetAddressBytes(), Assert.Single(DnsTestHelper.Decode(onEth!.Bytes).Answers).Data);
    }

    // The server binds only to LAN interfaces. A query arriving on e.g. a VPN adapter must not be answered, or we would
    // announce ourselves to a network we never intended to be on.
    [Fact]
    public void Queries_from_interfaces_outside_the_LAN_set_are_ignored()
    {
        Assert.Null(Answer(DnsTestHelper.Query(Fqdn, 1), PhoneOnEth, ifIndex: 99, EthAddress, WifiAddress));
    }

    // RFC 6762 section 11: a source outside the link's subnet is not a neighbour. Replying unicast to it would let a
    // spoofed packet aim our reply at any host.
    [Fact]
    public void Queries_from_a_source_outside_the_arrival_subnet_are_ignored()
    {
        var offLink = new IPEndPoint(IPAddress.Parse("8.8.8.8"), 5353);

        Assert.Null(Answer(DnsTestHelper.Query(Fqdn, 1, qu: true), offLink, Eth, EthAddress));
    }

    // Two addresses on one interface: answer with the one in the asker's subnet.
    [Fact]
    public void Interface_with_two_addresses_answers_with_the_one_the_asker_can_reach()
    {
        var second = new LanAddress(Eth, IPAddress.Parse("10.9.9.9"), 24);
        var asker = new IPEndPoint(IPAddress.Parse("10.9.9.50"), 5353);

        var reply = Answer(DnsTestHelper.Query(Fqdn, 1), asker, Eth, EthAddress, second);

        Assert.Equal(second.Address.GetAddressBytes(), Assert.Single(DnsTestHelper.Decode(reply!.Bytes).Answers).Data);
    }

    // RFC 6762 section 6.7: a resolver that is not a full mDNS stack sends from a random port and matches the reply on
    // query ID and echoed question; without those it silently discards our answer. TTL stays low and no cache-flush.
    [Fact]
    public void Legacy_unicast_copies_the_id_and_echoes_the_question()
    {
        var legacySource = new IPEndPoint(PhoneOnEth.Address, 49152);

        var reply = Answer(DnsTestHelper.Query(Fqdn, 1, id: 0x1234), legacySource, Eth, EthAddress);

        Assert.NotNull(reply);
        Assert.True(reply.Unicast);
        var d = DnsTestHelper.Decode(reply.Bytes);
        Assert.Equal(0x1234, d.Id);
        var q = Assert.Single(d.Questions);
        Assert.Equal((Fqdn, (ushort)1, (ushort)1), q);
        var a = Assert.Single(d.Answers);
        Assert.Equal(1, a.Class);
        Assert.True(a.Ttl <= 10);
    }

    // The label comes from a persisted file; a tampered value must be refused before it can reach the wire format.
    [Theory]
    [InlineData("")]
    [InlineData("evil.local")]
    [InlineData("buttonbox-ZZZZ")]
    public void Constructor_rejects_labels_that_are_not_generated_names(string label)
    {
        Assert.Throws<ArgumentException>(() => new MdnsResponder(label));
    }

    // TryAnswer is the last line of defence between the network and the process: even a bug in the parser must not throw.
    [Fact]
    public void TryAnswer_never_throws_on_random_garbage()
    {
        var rng = new Random(1234);
        var buf = new byte[300];
        for (var i = 0; i < 2000; i++)
        {
            rng.NextBytes(buf);
            var len = rng.Next(0, buf.Length);
            MdnsResponder.TryAnswer(buf.AsSpan(0, len), PhoneOnEth, Eth, Label, [EthAddress], out _);
        }
    }

    // Announcements are the only unsolicited packets we send. They must be well formed and carry the given address.
    [Fact]
    public void Announcement_has_A_record_with_cache_flush_and_NSEC()
    {
        var d = DnsTestHelper.Decode(MdnsResponseBuilder.BuildAnnouncement(Label, WifiAddress.Address));

        Assert.Equal(0x8400, d.Flags);
        var a = Assert.Single(d.Answers);
        Assert.Equal(Fqdn, a.Name);
        Assert.Equal(0x8001, a.Class);
        Assert.Equal(WifiAddress.Address.GetAddressBytes(), a.Data);
        Assert.Equal(47, Assert.Single(d.Additional).Type);
    }
}
