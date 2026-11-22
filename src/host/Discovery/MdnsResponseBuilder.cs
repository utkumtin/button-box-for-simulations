using System.Net;
using System.Text;

namespace ButtonBox.Discovery;

/// <param name="Bytes">The datagram to send.</param>
/// <param name="Unicast">True: send to the asker. False: multicast on the arrival interface.</param>
public sealed record MdnsReply(byte[] Bytes, bool Unicast);

/// <summary>Builds mDNS answers for the single name <c>&lt;label&gt;.local</c>. Pure: no sockets.</summary>
public static class MdnsResponseBuilder
{
    public const uint Ttl = 120;
    private const uint LegacyTtl = 10; // RFC 6762 section 6.7: legacy unicast answers use a TTL of at most 10 s
    private const ushort TypeA = 1;
    private const ushort TypeAaaa = 28;
    private const ushort TypeNsec = 47;
    private const ushort TypeAny = 255;
    private const ushort ClassIn = 1;
    private const ushort CacheFlush = 0x8000;
    private const ushort ResponseFlags = 0x8400; // QR + AA

    public static string FullName(string label) => label + ".local";

    /// <summary>
    /// Answers the questions for our name, or returns null if there is nothing to say. A answers with the A record, AAAA
    /// with an NSEC saying only A exists, ANY with A; every other type (e.g. HTTPS, 65) stays unanswered.
    /// </summary>
    /// <param name="arrivalAddress">The address of the interface the query arrived on, never any other.</param>
    /// <param name="legacyUnicast">The query came from a source port other than 5353: copy the ID, echo the question.</param>
    public static MdnsReply? Build(MdnsQuery query, string label, IPAddress arrivalAddress, bool legacyUnicast = false)
    {
        var fqdn = FullName(label);
        var ours = query.Questions
            .Where(q => string.Equals(q.Name, fqdn, StringComparison.OrdinalIgnoreCase) && q.ClassCode is ClassIn or TypeAny)
            .ToList();
        var wantA = ours.Any(q => q.Type is TypeA or TypeAny);
        var wantAaaa = ours.Any(q => q.Type == TypeAaaa);
        if (!wantA && !wantAaaa)
        {
            return null;
        }

        var ttl = legacyUnicast ? LegacyTtl : Ttl;
        var flush = !legacyUnicast;
        var nsecInAnswer = wantAaaa;
        var nsecInAdditional = !legacyUnicast && wantA && !wantAaaa;
        var answerCount = (wantA ? 1 : 0) + (nsecInAnswer ? 1 : 0);

        var ms = new MemoryStream();
        WriteU16(ms, legacyUnicast ? query.Id : 0);
        WriteU16(ms, ResponseFlags);
        WriteU16(ms, legacyUnicast ? ours.Count : 0);
        WriteU16(ms, answerCount);
        WriteU16(ms, 0);
        WriteU16(ms, nsecInAdditional ? 1 : 0);
        if (legacyUnicast)
        {
            foreach (var q in ours)
            {
                WriteName(ms, fqdn);
                WriteU16(ms, q.Type);
                WriteU16(ms, q.Class);
            }
        }

        if (wantA)
        {
            WriteARecord(ms, fqdn, arrivalAddress, flush, ttl);
        }

        if (nsecInAnswer)
        {
            WriteNsecRecord(ms, fqdn, flush, ttl);
        }

        if (nsecInAdditional)
        {
            WriteNsecRecord(ms, fqdn, flush, ttl);
        }

        return new MdnsReply(ms.ToArray(), legacyUnicast || ours.Any(q => q.WantsUnicast));
    }

    /// <summary>Unsolicited announcement (RFC 6762 section 8.3): the A record, with an NSEC in the additional section.</summary>
    public static byte[] BuildAnnouncement(string label, IPAddress address)
    {
        var fqdn = FullName(label);
        var ms = new MemoryStream();
        WriteU16(ms, 0);
        WriteU16(ms, ResponseFlags);
        WriteU16(ms, 0);
        WriteU16(ms, 1);
        WriteU16(ms, 0);
        WriteU16(ms, 1);
        WriteARecord(ms, fqdn, address, flush: true, Ttl);
        WriteNsecRecord(ms, fqdn, flush: true, Ttl);
        return ms.ToArray();
    }

    private static void WriteARecord(MemoryStream ms, string fqdn, IPAddress address, bool flush, uint ttl)
    {
        WriteName(ms, fqdn);
        WriteU16(ms, TypeA);
        WriteU16(ms, flush ? ClassIn | CacheFlush : ClassIn);
        WriteU32(ms, ttl);
        WriteU16(ms, 4);
        ms.Write(address.GetAddressBytes());
    }

    // NSEC for our own name whose bitmap has only the A bit (type 1) set: "AAAA does not exist".
    private static void WriteNsecRecord(MemoryStream ms, string fqdn, bool flush, uint ttl)
    {
        var next = EncodeName(fqdn);
        WriteName(ms, fqdn);
        WriteU16(ms, TypeNsec);
        WriteU16(ms, flush ? ClassIn | CacheFlush : ClassIn);
        WriteU32(ms, ttl);
        WriteU16(ms, next.Length + 3);
        ms.Write(next);
        ms.WriteByte(0); // window block 0
        ms.WriteByte(1); // bitmap length
        ms.WriteByte(0x40); // bit for type 1 (A)
    }

    private static void WriteName(MemoryStream ms, string name) => ms.Write(EncodeName(name));

    private static byte[] EncodeName(string name)
    {
        var ms = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }

        ms.WriteByte(0);
        return ms.ToArray();
    }

    private static void WriteU16(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)value);
    }

    private static void WriteU32(MemoryStream ms, uint value)
    {
        WriteU16(ms, (int)(value >> 16));
        WriteU16(ms, (int)(value & 0xFFFF));
    }
}
