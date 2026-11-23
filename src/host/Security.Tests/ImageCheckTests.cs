using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.Security.Tests;

// Why: imported images are untrusted files from the user's disk (decisions §7, §9). The host never decodes pixels, so the
// header walk is the only gate before the bytes reach the editor's WebView2 and the phone. These tests hold that gate shut.
public class ImageCheckTests
{
    // ---- builders -----------------------------------------------------------------------------------------------

    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

    private static byte[] Be32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Le32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }

    private static byte[] Chunk(string type, byte[] data, bool badCrc = false)
    {
        var typeAndData = Concat(Encoding.ASCII.GetBytes(type), data);
        var crc = ImageCheck.Crc32(typeAndData) ^ (badCrc ? 1u : 0u);
        return Concat(Be32((uint)data.Length), typeAndData, Be32(crc));
    }

    private static byte[] Ihdr(uint width, uint height, byte depth = 8, byte colour = 6, byte compression = 0, byte filter = 0, byte interlace = 0) =>
        Concat(Be32(width), Be32(height), [depth, colour, compression, filter, interlace]);

    // A real zlib stream of an all-zero image, so the accepted files are valid PNGs and not only valid headers.
    private static byte[] RealIdat(int size, int bytesPerPixel)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            var row = new byte[1 + (size * bytesPerPixel)];
            for (var y = 0; y < size; y++)
            {
                zlib.Write(row);
            }
        }

        return output.ToArray();
    }

    // Header checks never look inside IDAT, so rejection cases use a tiny stand-in payload.
    private static readonly byte[] DummyIdat = [0x78, 0x01, 0x00];

    private static byte[] Iend => Chunk("IEND", []);

    private static byte[] RgbaPng(int size, byte[]? idat = null) =>
        Concat(Signature, Chunk("IHDR", Ihdr((uint)size, (uint)size)), Chunk("IDAT", idat ?? RealIdat(size, 4)), Iend);

    private static byte[] WebpChunk(string fourCc, byte[] payload)
    {
        var padded = payload.Length % 2 == 0 ? payload : Concat(payload, [0]);
        return Concat(Encoding.ASCII.GetBytes(fourCc), Le32((uint)payload.Length), padded);
    }

    private static byte[] Riff(params byte[][] chunks)
    {
        var body = Concat(chunks);
        return Concat("RIFF"u8.ToArray(), Le32((uint)body.Length + 4), "WEBP"u8.ToArray(), body);
    }

    private static byte[] Vp8X(byte flags, int width, int height) =>
        WebpChunk("VP8X", [flags, 0, 0, 0, .. Le24(width - 1), .. Le24(height - 1)]);

    private static byte[] Le24(int value) => [(byte)value, (byte)(value >> 8), (byte)(value >> 16)];

    private const byte AlphaFlag = 0x10;
    private const byte AnimationFlag = 0x02;

    // The WebP bytes are headers plus dummy payload on purpose: the host only reads headers, never decodes.
    private static byte[] Vp8LPayload(int width, int height, bool alpha = true, int version = 0)
    {
        var bits = (uint)(width - 1) | ((uint)(height - 1) << 14) | (alpha ? 1u << 28 : 0) | ((uint)version << 29);
        return Concat([0x2F], Le32(bits), [0, 0, 0, 0]);
    }

    private static byte[] Vp8Payload(int width, int height) =>
        [0x10, 0x02, 0x00, 0x9D, 0x01, 0x2A, (byte)width, (byte)(width >> 8), (byte)height, (byte)(height >> 8), 0, 0];

    private static byte[] Alph => WebpChunk("ALPH", [0, 0, 0, 0]);

    private static ImageCheckResult Check(byte[] data) => ImageCheck.Check(data);

    // ---- accepted -------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(512)]
    [InlineData(1254)]
    [InlineData(2048)]
    public void Rgba_png_within_the_size_limits_is_accepted(int size)
    {
        var result = Check(RgbaPng(size));
        Assert.Null(result.Problem);
        Assert.Equal(AssetType.Png, result.Type);
        Assert.Equal(size, result.Size);
    }

    // Why: the three images of the demo profile are imported into the library on first run (decisions §6); a validator that
    // rejects them breaks the seed. (logo/logo.png is RGB without tRNS, but it is never imported: it is the tray icon.)
    [Theory]
    [InlineData("engine-button/body.png")]
    [InlineData("rotary/base.png")]
    [InlineData("rotary/knob.png")]
    public void Demo_pngs_with_alpha_are_accepted(string relativePath)
    {
        var result = Check(File.ReadAllBytes(SourceAsset(relativePath)));
        Assert.Null(result.Problem);
        Assert.Equal(AssetType.Png, result.Type);
        Assert.Equal(1254, result.Size);
    }

    // Why: a palette or RGB PNG gets its transparency from tRNS, which the format only honours before the image data.
    [Fact]
    public void Rgb_png_with_trns_before_idat_is_accepted()
    {
        var png = Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 8, 2)), Chunk("tRNS", [0, 0, 0, 0, 0, 0]), Chunk("IDAT", RealIdat(512, 3)), Iend);
        Assert.Equal(AssetType.Png, Check(png).Type);
    }

    // Why: the demo images carry a caBX chunk; unknown ancillary chunks must not cause a rejection.
    [Fact]
    public void Unknown_ancillary_chunk_is_allowed()
    {
        var png = Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Chunk("caBX", [1, 2, 3]), Chunk("IDAT", RealIdat(512, 4)), Iend);
        Assert.Equal(AssetType.Png, Check(png).Type);
    }

    [Fact]
    public void Interlaced_rgba_png_is_accepted()
    {
        var png = Concat(Signature, Chunk("IHDR", Ihdr(512, 512, interlace: 1)), Chunk("IDAT", RealIdat(512, 4)), Iend);
        Assert.Equal(AssetType.Png, Check(png).Type);
    }

    [Fact]
    public void Webp_extended_lossy_with_alph_is_accepted()
    {
        var data = Riff(Vp8X(AlphaFlag, 512, 512), Alph, WebpChunk("VP8 ", Vp8Payload(512, 512)));
        var result = Check(data);
        Assert.Equal(AssetType.Webp, result.Type);
        Assert.Equal(512, result.Size);
    }

    [Fact]
    public void Webp_simple_lossless_with_alpha_bit_is_accepted()
    {
        var result = Check(Riff(WebpChunk("VP8L", Vp8LPayload(1024, 1024))));
        Assert.Equal(AssetType.Webp, result.Type);
        Assert.Equal(1024, result.Size);
    }

    [Fact]
    public void Webp_extended_lossless_is_accepted()
    {
        var result = Check(Riff(Vp8X(AlphaFlag, 2048, 2048), WebpChunk("VP8L", Vp8LPayload(2048, 2048))));
        Assert.Equal(AssetType.Webp, result.Type);
        Assert.Equal(2048, result.Size);
    }

    // Why: the CRC table is hand-written (no package allowed); a wrong table would reject every real PNG.
    [Fact]
    public void Crc32_matches_the_standard_check_value()
    {
        Assert.Equal(0xCBF43926u, ImageCheck.Crc32("123456789"u8));
    }

    // ---- rejected ---------------------------------------------------------------------------------------------------

    public static TheoryData<string, byte[], string> Rejected()
    {
        const string svg = "SVG files cannot be imported";
        const string other = "Only PNG and WebP";
        const string pngBad = "The PNG file is damaged.";
        const string webpBad = "The WebP file is damaged.";
        const string noAlpha = "no transparency";
        var rgba = RgbaPng(512, DummyIdat);
        var data = new TheoryData<string, byte[], string>();

        void Add(string name, byte[] bytes, string expected) => data.Add(name, bytes, expected);
        byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

        // Why: SVG can carry script and external references; it is rejected by content, whatever the file is called.
        // (Renamed to .png the bytes are identical, and only bytes are ever passed to the check.)
        Add("svg with script", Text("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), svg);
        Add("svg with onload", Text("<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"></svg>"), svg);
        Add("svg with external image", Text("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"http://evil.example/x.png\"/></svg>"), svg);
        Add("svg with BOM and leading whitespace", Concat([0xEF, 0xBB, 0xBF], Text(" \r\n\t<svg/>")), svg);
        Add("xml entity bomb", Text("<?xml version=\"1.0\"?><!DOCTYPE lolz [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;\">]><lolz>&b;</lolz>"), svg);
        Add("html", Text("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>"), svg);

        Add("gif", Text("GIF89a\u0001\0\u0001\0\0\0\0;"), other);
        Add("jpeg", [0xFF, 0xD8, 0xFF, 0xE0, 0, 0x10, 0x4A, 0x46, 0x49, 0x46, 0, 1], other);
        Add("bmp", Concat(Text("BM"), new byte[60]), other);
        Add("ico", [0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0], other);
        Add("empty", [], "empty");
        Add("MaxBytes + 1", new byte[ImageCheck.MaxBytes + 1], "larger than 4 MiB");

        // PNG structure.
        var badCrc = Concat(Signature, Chunk("IHDR", Ihdr(512, 512), badCrc: true), Chunk("IDAT", DummyIdat), Iend);
        Add("png bad IHDR CRC", badCrc, pngBad);
        Add("png bad IDAT CRC", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Chunk("IDAT", DummyIdat, badCrc: true), Iend), pngBad);
        Add("png IHDR not first", Concat(Signature, Chunk("tEXt", [65]), Chunk("IHDR", Ihdr(512, 512)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png IHDR length 12", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)[..12]), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png chunk length 0xFFFFFFFF", Concat(Signature, Be32(0xFFFFFFFF), "IHDR"u8.ToArray(), new byte[17]), pngBad);
        Add("png chunk length past the end", Concat(Signature, Be32(1000), "IHDR"u8.ToArray(), new byte[17]), pngBad);
        Add("png truncated", rgba[..(rgba.Length - 5)], pngBad);
        Add("png truncated after signature", Signature, pngBad);
        Add("png missing IEND", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Chunk("IDAT", DummyIdat)), pngBad);
        // Why: a PNG with a ZIP (or anything) behind IEND is a polyglot; the file must end at IEND.
        Add("png plus zip polyglot", Concat(rgba, [0x50, 0x4B, 0x03, 0x04, 0, 0, 0, 0]), pngBad);
        Add("png without IDAT", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Iend), pngBad);
        Add("png chunk type with a digit", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Chunk("ab1d", [1]), Chunk("IDAT", DummyIdat), Iend), pngBad);

        // Transparency.
        // Why: a tRNS after the image data is ignored by decoders, so it does not make the image transparent.
        Add("png tRNS after IDAT", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 8, 2)), Chunk("IDAT", DummyIdat), Chunk("tRNS", [0, 0, 0, 0, 0, 0]), Iend), noAlpha);
        Add("png rgb without tRNS", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 8, 2)), Chunk("IDAT", DummyIdat), Iend), noAlpha);
        Add("png greyscale without tRNS", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 8, 0)), Chunk("IDAT", DummyIdat), Iend), noAlpha);

        // IHDR contents.
        Add("png colour type 6 with depth 4", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 4, 6)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png colour type 5", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 8, 5)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png colour type 3 with depth 16", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, 16, 3)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png compression method 1", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, compression: 1)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png filter method 1", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, filter: 1)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png interlace 2", Concat(Signature, Chunk("IHDR", Ihdr(512, 512, interlace: 2)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png width 0", Concat(Signature, Chunk("IHDR", Ihdr(0, 512)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png width 2^31", Concat(Signature, Chunk("IHDR", Ihdr(0x80000000, 0x80000000)), Chunk("IDAT", DummyIdat), Iend), pngBad);
        Add("png acTL (APNG)", Concat(Signature, Chunk("IHDR", Ihdr(512, 512)), Chunk("acTL", [0, 0, 0, 2, 0, 0, 0, 0]), Chunk("IDAT", DummyIdat), Iend), "Animated");

        // Sizes.
        Add("png not square", Concat(Signature, Chunk("IHDR", Ihdr(800, 600)), Chunk("IDAT", DummyIdat), Iend), "must be square (it is 800 × 600 px)");
        Add("png 511 px", RgbaPng(511, DummyIdat), "at least 512");
        Add("png 2049 px", RgbaPng(2049, DummyIdat), "at most 2048");

        // WebP RIFF container.
        var lossless = Riff(WebpChunk("VP8L", Vp8LPayload(512, 512)));
        var longer = Concat(lossless, [0, 0]);
        Add("webp trailing bytes (RIFF size too small)", longer, webpBad);
        var shorter = lossless[..(lossless.Length - 2)];
        Add("webp RIFF size larger than the file", shorter, webpBad);
        var oddPayload = Concat([0x2F], Le32((512 - 1) | ((512u - 1) << 14) | (1u << 28)), [0, 0, 0, 0]);
        Add("webp odd RIFF size", Concat("RIFF"u8.ToArray(), Le32((uint)(4 + 8 + oddPayload.Length)), "WEBP"u8.ToArray(), "VP8L"u8.ToArray(), Le32((uint)oddPayload.Length), oddPayload), webpBad);
        Add("webp chunk size overflow", Concat("RIFF"u8.ToArray(), Le32(4 + 8 + 4), "WEBP"u8.ToArray(), "VP8L"u8.ToArray(), Le32(0xFFFFFFFF), [0, 0, 0, 0]), webpBad);
        Add("webp lossy only", Riff(WebpChunk("VP8 ", Vp8Payload(512, 512))), noAlpha);
        Add("webp VP8X without alpha flag", Riff(Vp8X(0, 512, 512), WebpChunk("VP8L", Vp8LPayload(512, 512))), noAlpha);
        Add("webp VP8X with animation flag", Riff(Vp8X(AlphaFlag | AnimationFlag, 512, 512), WebpChunk("ANIM", [0, 0, 0, 0, 0, 0]), WebpChunk("ANMF", new byte[16])), "Animated");
        Add("webp VP8X with ANMF but no flag", Riff(Vp8X(AlphaFlag, 512, 512), WebpChunk("ANMF", new byte[16])), "Animated");
        Add("webp VP8X + VP8 without ALPH", Riff(Vp8X(AlphaFlag, 512, 512), WebpChunk("VP8 ", Vp8Payload(512, 512))), webpBad);
        Add("webp VP8X + VP8 with ALPH after VP8", Riff(Vp8X(AlphaFlag, 512, 512), WebpChunk("VP8 ", Vp8Payload(512, 512)), Alph), webpBad);
        Add("webp VP8L version 1", Riff(WebpChunk("VP8L", Vp8LPayload(512, 512, version: 1))), webpBad);
        Add("webp VP8L alpha bit 0", Riff(WebpChunk("VP8L", Vp8LPayload(512, 512, alpha: false))), noAlpha);
        Add("webp VP8L signature byte wrong", Riff(WebpChunk("VP8L", [0x2E, 0, 0, 0, 0, 0])), webpBad);
        Add("webp VP8L dimensions differ from canvas", Riff(Vp8X(AlphaFlag, 512, 512), WebpChunk("VP8L", Vp8LPayload(513, 513))), webpBad);
        Add("webp VP8 dimensions differ from canvas", Riff(Vp8X(AlphaFlag, 512, 512), Alph, WebpChunk("VP8 ", Vp8Payload(600, 600))), webpBad);
        Add("webp VP8 start code wrong", Riff(Vp8X(AlphaFlag, 512, 512), Alph, WebpChunk("VP8 ", [0x10, 0x02, 0x00, 0, 0, 0, 0, 2, 0, 2, 0, 0])), webpBad);
        Add("webp two image chunks", Riff(Vp8X(AlphaFlag, 512, 512), WebpChunk("VP8L", Vp8LPayload(512, 512)), WebpChunk("VP8L", Vp8LPayload(512, 512))), webpBad);
        Add("webp VP8X without image", Riff(Vp8X(AlphaFlag, 512, 512)), webpBad);
        Add("webp VP8X wrong length", Riff(WebpChunk("VP8X", [AlphaFlag, 0, 0, 0, 0, 0, 0, 0])), webpBad);
        Add("webp unknown first chunk", Riff(WebpChunk("JUNK", [0, 0])), webpBad);
        Add("webp simple lossless with a second chunk", Riff(WebpChunk("VP8L", Vp8LPayload(512, 512)), WebpChunk("EXIF", [0, 0])), webpBad);
        Add("webp canvas not square", Riff(Vp8X(AlphaFlag, 800, 600), WebpChunk("VP8L", Vp8LPayload(800, 600))), "must be square (it is 800 × 600 px)");
        Add("webp canvas 511", Riff(Vp8X(AlphaFlag, 511, 511), WebpChunk("VP8L", Vp8LPayload(511, 511))), "at least 512");
        Add("webp lossless 2049", Riff(WebpChunk("VP8L", Vp8LPayload(2049, 2049))), "at most 2048");

        return data;
    }

    [Theory]
    [MemberData(nameof(Rejected))]
    public void Hostile_or_unsuitable_input_is_rejected_with_a_fixed_sentence(string name, byte[] data, string expected)
    {
        _ = name;
        var result = Check(data);
        Assert.Null(result.Type);
        Assert.Equal(0, result.Size);
        Assert.NotNull(result.Problem);
        Assert.Contains(expected, result.Problem);
        // Why: the sentence is shown in the editor; it must stay short and must never carry input bytes.
        Assert.True(result.Problem.Length <= 300);
    }

    // Why: a truncated file must be rejected cleanly at every cut point, never throw (no out-of-range slice on bad input).
    [Fact]
    public void Every_truncation_of_valid_files_is_rejected_without_throwing()
    {
        var files = new[]
        {
            RgbaPng(512, DummyIdat),
            Riff(Vp8X(AlphaFlag, 512, 512), Alph, WebpChunk("VP8 ", Vp8Payload(512, 512))),
            Riff(WebpChunk("VP8L", Vp8LPayload(512, 512))),
        };
        foreach (var file in files)
        {
            Assert.Null(Check(file).Problem);
            for (var cut = 0; cut < file.Length; cut++)
            {
                Assert.NotNull(Check(file[..cut]).Problem);
            }
        }
    }

    // Why: the import limits are published in editor-api.schema.json (assetInfo); the schema drift test compares them.
    [Fact]
    public void Limits_are_the_documented_ones()
    {
        Assert.Equal(512, ImageCheck.MinSize);
        Assert.Equal(2048, ImageCheck.MaxSize);
        Assert.Equal(4194304, ImageCheck.MaxBytes);
    }

    private static string SourceAsset(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "assets", "source", relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        throw new FileNotFoundException("assets/source/" + relativePath + " not found above " + AppContext.BaseDirectory);
    }
}
