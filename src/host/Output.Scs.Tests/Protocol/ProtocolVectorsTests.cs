using System.Text;
using ButtonBox.Output.Scs.Protocol;
using Xunit;

namespace ButtonBox.Output.Scs.Tests.Protocol;

// Why: the C++ plugin and this host must agree byte for byte (decisions §9). Both sides run the same vectors.txt, so a
// grammar drift on either side fails a test instead of showing up as a closed pipe in the game.
public class ProtocolVectorsTests
{
    private static readonly string[] Lines = LoadLines();

    public static TheoryData<string> PayloadLines => Select("payload ");

    public static TheoryData<string> FrameLines => Select("frame ");

    [Fact]
    public void Pipe_name_constant_equals_the_vectors_file()
    {
        var line = Assert.Single(Lines, l => l.StartsWith("pipe-name ", StringComparison.Ordinal));
        Assert.Equal(@"\\.\pipe\" + ScsProtocol.PipeName, line["pipe-name ".Length..]);
    }

    [Fact]
    public void Vectors_file_is_not_empty()
    {
        // A missing or moved file would otherwise make every vector test vanish silently.
        Assert.True(PayloadLines.Count > 50);
        Assert.True(FrameLines.Count >= 8);
    }

    [Theory]
    [MemberData(nameof(PayloadLines))]
    public void Payload_vector(string line)
    {
        var parts = line.Split(' ', 4);
        var toHost = parts[1] == "to-host";
        var ok = parts[2] == "ok";
        var bytes = Unescape(parts[3]);

        if (toHost)
        {
            var parsed = ScsProtocol.TryParsePluginMessage(bytes, out var message);
            Assert.Equal(ok, parsed);
            if (ok)
            {
                Assert.Equal(bytes, ScsProtocol.Encode(message!));
            }
        }
        else
        {
            var parsed = ScsProtocol.TryParseHostMessage(bytes, out var message);
            Assert.Equal(ok, parsed);
            if (ok)
            {
                Assert.Equal(bytes, ScsProtocol.Encode(message!));
            }
        }
    }

    [Theory]
    [MemberData(nameof(FrameLines))]
    public void Frame_vector_whole_and_byte_by_byte(string line)
    {
        var parts = line.Split(' ');
        var bytes = Convert.FromHexString(parts[2]);

        // Whole stream at once.
        var whole = new FrameReader();
        whole.Push(bytes.AsSpan(0, Math.Min(bytes.Length, FrameReader.MaxChunk)));
        var wholeResult = whole.TryRead(out var payload);
        Assert.Equal(Expected(parts[1]), wholeResult);
        if (parts[1] == "ok")
        {
            Assert.Equal(bytes.AsSpan(2).ToArray(), payload);
            Assert.Equal(0, whole.BufferedBytes);
        }

        // One byte at a time gives the same verdict.
        var single = new FrameReader();
        var result = FrameReadResult.NeedMore;
        foreach (var b in bytes)
        {
            single.Push([b]);
            result = single.TryRead(out payload);
            if (result != FrameReadResult.NeedMore)
            {
                break;
            }
        }

        Assert.Equal(Expected(parts[1]), result);
        if (parts[1] == "ok")
        {
            Assert.Equal(bytes.AsSpan(2).ToArray(), payload);
        }
    }

    [Fact]
    public void Two_frames_in_one_push_come_out_in_order_and_a_failed_reader_stays_failed()
    {
        var reader = new FrameReader();
        reader.Push([1, 0, 0x41, 2, 0, 0x42, 0x43]);
        Assert.Equal(FrameReadResult.Frame, reader.TryRead(out var first));
        Assert.Equal(FrameReadResult.Frame, reader.TryRead(out var second));
        Assert.Equal(FrameReadResult.NeedMore, reader.TryRead(out _));
        Assert.Equal("A", Encoding.ASCII.GetString(first));
        Assert.Equal("BC", Encoding.ASCII.GetString(second));

        reader.Push([0, 0]);
        Assert.Equal(FrameReadResult.Error, reader.TryRead(out _));
        reader.Push([1, 0, 0x41]);
        Assert.Equal(FrameReadResult.Error, reader.TryRead(out _));
    }

    private static FrameReadResult Expected(string verdict) => verdict switch
    {
        "ok" => FrameReadResult.Frame,
        "bad" => FrameReadResult.Error,
        _ => FrameReadResult.NeedMore,
    };

    private static TheoryData<string> Select(string prefix)
    {
        var data = new TheoryData<string>();
        foreach (var line in Lines.Where(l => l.StartsWith(prefix, StringComparison.Ordinal)))
        {
            data.Add(line);
        }

        return data;
    }

    private static string[] LoadLines()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "plugins", "scs", "protocol", "vectors.txt");
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate, Encoding.ASCII)
                    .Split('\n')
                    .Where(l => l.Length > 0 && l[0] != '#')
                    .ToArray();
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("src/plugins/scs/protocol/vectors.txt not found above " + AppContext.BaseDirectory);
    }

    /// <summary>Escapes of vectors.txt: <c>\\</c> is one backslash, <c>\xNN</c> is one byte.</summary>
    private static byte[] Unescape(string text)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\\')
            {
                bytes.Add((byte)text[i]);
            }
            else if (text[i + 1] == '\\')
            {
                bytes.Add((byte)'\\');
                i++;
            }
            else
            {
                Assert.Equal('x', text[i + 1]);
                bytes.Add(Convert.ToByte(text.Substring(i + 2, 2), 16));
                i += 3;
            }
        }

        return bytes.ToArray();
    }
}
