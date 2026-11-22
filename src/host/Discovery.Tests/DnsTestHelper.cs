using Xunit;
using System.Text;

namespace ButtonBox.Discovery.Tests;

internal sealed record Rr(string Name, ushort Type, ushort Class, uint Ttl, byte[] Data);

internal sealed record Decoded(ushort Id, ushort Flags, List<(string Name, ushort Type, ushort Class)> Questions, List<Rr> Answers, List<Rr> Additional);

/// <summary>Builds query packets and decodes our (uncompressed) responses, independently of the production code.</summary>
internal static class DnsTestHelper
{
    public const ushort Qu = 0x8000;

    public static byte[] Name(string name)
    {
        var ms = new MemoryStream();
        foreach (var label in name.Split('.'))
        {
            ms.WriteByte((byte)label.Length);
            ms.Write(Encoding.ASCII.GetBytes(label));
        }

        ms.WriteByte(0);
        return ms.ToArray();
    }

    public static byte[] Header(ushort id, ushort flags, int qd)
    {
        var ms = new MemoryStream();
        U16(ms, id);
        U16(ms, flags);
        U16(ms, qd);
        U16(ms, 0);
        U16(ms, 0);
        U16(ms, 0);
        return ms.ToArray();
    }

    /// <summary>A query like real resolvers send: the name is written once, later questions point back to it (0xC00C).</summary>
    public static byte[] Query(string name, ushort id, ushort flags, params (ushort Type, ushort Class)[] questions)
    {
        var ms = new MemoryStream();
        ms.Write(Header(id, flags, questions.Length));
        for (var i = 0; i < questions.Length; i++)
        {
            if (i == 0)
            {
                ms.Write(Name(name));
            }
            else
            {
                ms.WriteByte(0xC0);
                ms.WriteByte(0x0C);
            }

            U16(ms, questions[i].Type);
            U16(ms, questions[i].Class);
        }

        return ms.ToArray();
    }

    public static byte[] Query(string name, ushort type, bool qu = false, ushort id = 0, ushort flags = 0)
        => Query(name, id, flags, (type, (ushort)(qu ? Qu | 1 : 1)));

    public static Decoded Decode(byte[] d)
    {
        var pos = 0;
        var id = R16(d, ref pos);
        var flags = R16(d, ref pos);
        int qd = R16(d, ref pos), an = R16(d, ref pos), ns = R16(d, ref pos), ar = R16(d, ref pos);
        var qs = new List<(string, ushort, ushort)>();
        for (var i = 0; i < qd; i++)
        {
            qs.Add((ReadName(d, ref pos), R16(d, ref pos), R16(d, ref pos)));
        }

        List<Rr> Rrs(int count)
        {
            var list = new List<Rr>();
            for (var i = 0; i < count; i++)
            {
                var n = ReadName(d, ref pos);
                var t = R16(d, ref pos);
                var c = R16(d, ref pos);
                var ttl = ((uint)R16(d, ref pos) << 16) | R16(d, ref pos);
                int len = R16(d, ref pos);
                list.Add(new Rr(n, t, c, ttl, d[pos..(pos + len)]));
                pos += len;
            }

            return list;
        }

        var answers = Rrs(an);
        Rrs(ns);
        var additional = Rrs(ar);
        Assert.Equal(d.Length, pos); // the counts in the header must account for every byte
        return new Decoded(id, flags, qs, answers, additional);
    }

    private static string ReadName(byte[] d, ref int pos)
    {
        var labels = new List<string>();
        while (d[pos] != 0)
        {
            int len = d[pos];
            Assert.True(len < 0x40, "responses are written without compression");
            labels.Add(Encoding.ASCII.GetString(d, pos + 1, len));
            pos += 1 + len;
        }

        pos++;
        return string.Join('.', labels);
    }

    private static ushort R16(byte[] d, ref int pos)
    {
        var v = (ushort)((d[pos] << 8) | d[pos + 1]);
        pos += 2;
        return v;
    }

    private static void U16(Stream s, int v)
    {
        s.WriteByte((byte)(v >> 8));
        s.WriteByte((byte)v);
    }
}
