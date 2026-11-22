using System.Text;
using Xunit;

namespace ButtonBox.Discovery.Tests;

// Every byte of an mDNS packet comes from "a foreign device on the LAN". The parser must reject malformed input with
// false (never an exception, never an answer), because an exception in the receive loop would be a remote denial of service.
public sealed class MdnsPacketTests
{
    private static readonly byte[] OneLabelQuestion = [1, (byte)'a', 0, 0, 1, 0, 1];

    private static byte[] Valid() => DnsTestHelper.Query("buttonbox-c295.local", 1);

    private static byte[] Packet(int qdCount, params byte[][] body)
        => [.. DnsTestHelper.Header(0, 0, (ushort)qdCount), .. body.SelectMany(b => b)];

    private static byte[] LongLabel(int length) => [(byte)length, .. Enumerable.Repeat((byte)'a', length)];

    public static TheoryData<string, byte[]> MalformedPackets() => new()
    {
        { "empty packet", [] },
        { "truncated header", new byte[11] },
        { "truncated name", Packet(1, [5, (byte)'a', (byte)'b']) },
        { "name never terminated", Packet(1, [1, (byte)'a']) },
        { "truncated question tail", Packet(1, [1, (byte)'a', 0, 0, 1, 0]) },
        { "pointer loop (points at itself)", Packet(1, [0xC0, 0x0C, 0, 1, 0, 1]) },
        { "forward pointer", Packet(1, [0xC0, 0x20, 0, 1, 0, 1]) },
        { "pointer into the header", Packet(1, [0xC0, 0x02, 0, 1, 0, 1]) },
        { "truncated pointer", Packet(1, [0xC0]) },
        { "reserved label type 0x80", Packet(1, [0x80, 0, 0, 1, 0, 1]) },
        { "reserved label type 0x40", Packet(1, [0x40, 0, 0, 1, 0, 1]) },
        { "label of 64 bytes", Packet(1, LongLabel(64), [0, 0, 1, 0, 1]) },
        { "name over 255 bytes", Packet(1, LongLabel(63), LongLabel(63), LongLabel(63), LongLabel(63), [0, 0, 1, 0, 1]) },
        { "qdcount 65535", Packet(65535, OneLabelQuestion) },
        { "qdcount 17", Packet(17, Enumerable.Repeat(OneLabelQuestion, 17).ToArray()) },
        { "qdcount larger than the data", Packet(3, OneLabelQuestion) },
        { "9001 bytes", [.. Valid(), .. new byte[9001 - Valid().Length]] },
        { "response flag set", DnsTestHelper.Query("buttonbox-c295.local", 1, flags: 0x8400) },
        { "non-QUERY opcode", DnsTestHelper.Query("buttonbox-c295.local", 1, flags: 0x2800) },
    };

    [Theory]
    [MemberData(nameof(MalformedPackets))]
    public void Malformed_packet_is_rejected_without_throwing(string description, byte[] packet)
    {
        Assert.False(MdnsPacket.TryParseQuery(packet, out var query), description);
        Assert.Null(query);
    }

    // The limits must sit exactly at the documented boundary, or they would reject real traffic (or let too much through).
    [Fact]
    public void Limits_accept_their_exact_boundary()
    {
        Assert.True(MdnsPacket.TryParseQuery(Packet(16, Enumerable.Repeat(OneLabelQuestion, 16).ToArray()), out var q));
        Assert.Equal(16, q.Questions.Count);

        Assert.True(MdnsPacket.TryParseQuery([.. Valid(), .. new byte[9000 - Valid().Length]], out _)); // trailing sections ignored

        Assert.False(MdnsPacket.TryParseQuery(Packet(1, LongLabel(63)), out _)); // unterminated: sanity
        Assert.True(MdnsPacket.TryParseQuery(Packet(1, LongLabel(63), [0, 0, 1, 0, 1]), out var label63));
        Assert.Equal(new string('a', 63), label63.Questions[0].Name);

        // 63 + 63 + 63 + 61 label bytes + 4 length bytes + terminator = 255 on the wire.
        Assert.True(MdnsPacket.TryParseQuery(Packet(1, LongLabel(63), LongLabel(63), LongLabel(63), LongLabel(61), [0, 0, 1, 0, 1]), out _));
        Assert.False(MdnsPacket.TryParseQuery(Packet(1, LongLabel(63), LongLabel(63), LongLabel(63), LongLabel(62), [0, 0, 1, 0, 1]), out _));
    }

    // Real resolvers compress repeated names. A chain of backward pointers (here 15 deep, the most 16 questions allow)
    // must resolve; the 16-jump cap itself is unreachable through questions alone and only guards against future changes.
    [Fact]
    public void Chained_backward_pointers_resolve_to_the_original_name()
    {
        var bytes = new List<byte>(DnsTestHelper.Header(0, 0, 16));
        bytes.AddRange(OneLabelQuestion); // question 1: "a" at offset 12
        var previousStart = 12;
        for (var i = 1; i < 16; i++)
        {
            var start = bytes.Count;
            bytes.AddRange([(byte)(0xC0 | (previousStart >> 8)), (byte)previousStart, 0, 1, 0, 1]);
            previousStart = start;
        }

        Assert.True(MdnsPacket.TryParseQuery(bytes.ToArray(), out var q));
        Assert.All(q.Questions, x => Assert.Equal("a", x.Name));
    }

    [Fact]
    public void Compressed_question_reuses_the_first_name_and_keeps_the_QU_bit()
    {
        var packet = DnsTestHelper.Query("buttonbox-c295.local", 0, 0, (65, 0x8001), (28, 0x8001), (1, 0x8001));

        Assert.True(MdnsPacket.TryParseQuery(packet, out var q));
        Assert.Equal([65, 28, 1], q.Questions.Select(x => (int)x.Type));
        Assert.All(q.Questions, x =>
        {
            Assert.Equal("buttonbox-c295.local", x.Name);
            Assert.True(x.WantsUnicast);
            Assert.Equal(1, x.ClassCode);
        });
    }

    // Names are decoded as Latin-1, so arbitrary bytes in labels cannot make the parser throw on a decoding error.
    [Fact]
    public void Non_ascii_label_bytes_do_not_throw()
    {
        Assert.True(MdnsPacket.TryParseQuery(Packet(1, [3, 0xFF, 0xFE, 0x80, 0, 0, 1, 0, 1]), out var q));
        Assert.Equal("ÿþ\u0080", q.Questions[0].Name);
        Assert.NotEmpty(Encoding.Latin1.GetBytes(q.Questions[0].Name));
    }

    // Fuzz: whatever the bytes are, parsing never throws.
    [Fact]
    public void Random_bytes_never_throw()
    {
        var rng = new Random(42);
        var buf = new byte[200];
        for (var i = 0; i < 5000; i++)
        {
            rng.NextBytes(buf);
            if (i % 2 == 0)
            {
                buf[2] = 0;
                buf[3] = 0; // plausible flags, so the parser gets past the header most of the time
                buf[4] = 0;
                buf[5] = (byte)rng.Next(1, 5);
            }

            MdnsPacket.TryParseQuery(buf.AsSpan(0, rng.Next(0, buf.Length)), out _);
        }
    }
}
