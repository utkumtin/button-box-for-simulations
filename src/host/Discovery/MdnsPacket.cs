using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ButtonBox.Discovery;

public readonly record struct DnsQuestion(string Name, ushort Type, ushort Class)
{
    /// <summary>The top bit of the class field: the asker prefers a unicast reply (RFC 6762 section 5.4).</summary>
    public bool WantsUnicast => (Class & 0x8000) != 0;

    public ushort ClassCode => (ushort)(Class & 0x7FFF);
}

public sealed record MdnsQuery(ushort Id, ushort Flags, IReadOnlyList<DnsQuestion> Questions);

/// <summary>
/// Parser for incoming mDNS queries. Everything here is attacker-controlled: every read is bounds-checked and
/// malformed input returns false instead of throwing.
/// </summary>
public static class MdnsPacket
{
    public const int MaxPacketSize = 9000;
    public const int MaxQuestions = 16;
    private const int HeaderSize = 12;
    private const int MaxLabel = 63;
    private const int MaxName = 255;
    private const int MaxPointerJumps = 16;

    /// <summary>
    /// Parses a standard query (QR=0, opcode 0). Only the header and questions are read; the answer, authority and
    /// additional counts are ignored. Returns false for responses, other opcodes and anything malformed.
    /// </summary>
    public static bool TryParseQuery(ReadOnlySpan<byte> packet, [NotNullWhen(true)] out MdnsQuery? query)
    {
        query = null;
        if (packet.Length < HeaderSize || packet.Length > MaxPacketSize)
        {
            return false;
        }

        var id = (ushort)((packet[0] << 8) | packet[1]);
        var flags = (ushort)((packet[2] << 8) | packet[3]);
        if ((flags & 0x8000) != 0 || (flags & 0x7800) != 0)
        {
            return false; // a response, or an opcode other than QUERY
        }

        var qdCount = (packet[4] << 8) | packet[5];
        if (qdCount > MaxQuestions)
        {
            return false;
        }

        var questions = new List<DnsQuestion>(qdCount);
        var pos = HeaderSize;
        for (var i = 0; i < qdCount; i++)
        {
            if (!TryReadName(packet, pos, out var name, out pos) || pos + 4 > packet.Length)
            {
                return false;
            }

            var type = (ushort)((packet[pos] << 8) | packet[pos + 1]);
            var cls = (ushort)((packet[pos + 2] << 8) | packet[pos + 3]);
            pos += 4;
            questions.Add(new DnsQuestion(name, type, cls));
        }

        query = new MdnsQuery(id, flags, questions);
        return true;
    }

    /// <summary>
    /// Reads a possibly compressed name. Pointers must point strictly backwards (and not into the header), at most
    /// 16 are followed, labels are at most 63 bytes and the whole name at most 255 bytes on the wire.
    /// </summary>
    private static bool TryReadName(ReadOnlySpan<byte> p, int start, out string name, out int next)
    {
        name = string.Empty;
        next = -1;
        var sb = new StringBuilder();
        var cur = start;
        var jumps = 0;
        var wireLength = 1; // the terminating zero

        while (true)
        {
            if (cur >= p.Length)
            {
                return false;
            }

            int len = p[cur];
            if (len == 0)
            {
                if (next < 0)
                {
                    next = cur + 1;
                }

                break;
            }

            switch (len & 0xC0)
            {
                case 0xC0:
                    if (cur + 1 >= p.Length)
                    {
                        return false;
                    }

                    var target = ((len & 0x3F) << 8) | p[cur + 1];
                    if (target >= cur || target < HeaderSize || ++jumps > MaxPointerJumps)
                    {
                        return false;
                    }

                    if (next < 0)
                    {
                        next = cur + 2;
                    }

                    cur = target;
                    continue;
                case 0x00:
                    break;
                default:
                    return false; // 0x40 and 0x80 label types are reserved; also rejects labels of 64+ bytes
            }

            if (len > MaxLabel || cur + 1 + len > p.Length)
            {
                return false;
            }

            wireLength += len + 1;
            if (wireLength > MaxName)
            {
                return false;
            }

            if (sb.Length > 0)
            {
                sb.Append('.');
            }

            for (var i = 0; i < len; i++)
            {
                sb.Append((char)p[cur + 1 + i]); // Latin-1: never throws on arbitrary bytes
            }

            cur += 1 + len;
        }

        name = sb.ToString();
        return true;
    }
}
