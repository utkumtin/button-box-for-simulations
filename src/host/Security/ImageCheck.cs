using System.Buffers.Binary;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Security;

/// <summary>Outcome of <see cref="ImageCheck.Check"/>: <see cref="Type"/> is null when the file is rejected, then <see cref="Problem"/> is one sentence for the user.</summary>
public sealed record ImageCheckResult(AssetType? Type, int Size, string? Problem);

/// <summary>
/// Decides from the file header alone whether imported bytes are an acceptable PNG or WebP asset (decisions §7).
/// The host never decodes pixels; the type comes from the content, never from a file name or declared type.
/// Problems are fixed sentences: they contain no input bytes and no file names.
/// </summary>
public static class ImageCheck
{
    public const int MaxBytes = 4 * 1024 * 1024;
    public const int MinSize = 512;
    public const int MaxSize = 2048;

    private const string SvgProblem = "SVG files cannot be imported; use PNG or WebP.";
    private const string PngDamaged = "The PNG file is damaged.";
    private const string WebpDamaged = "The WebP file is damaged.";
    private const string NoAlpha = "The image has no transparency.";
    private const string Animated = "Animated images cannot be imported.";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static ImageCheckResult Check(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0)
        {
            return Reject("The file is empty.");
        }

        if (data.Length > MaxBytes)
        {
            return Reject("The file is larger than 4 MiB.");
        }

        if (LooksLikeMarkup(data))
        {
            return Reject(SvgProblem);
        }

        if (data.StartsWith(PngSignature))
        {
            return CheckPng(data);
        }

        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return CheckWebp(data);
        }

        return Reject("Only PNG and WebP images can be imported.");
    }

    // Optional UTF-8 BOM, whitespace, then '<': SVG, any other XML and HTML.
    private static bool LooksLikeMarkup(ReadOnlySpan<byte> data)
    {
        var i = data.StartsWith<byte>([0xEF, 0xBB, 0xBF]) ? 3 : 0;
        while (i < data.Length && data[i] is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n' or 0x0C)
        {
            i++;
        }

        return i < data.Length && data[i] == (byte)'<';
    }

    private static ImageCheckResult CheckPng(ReadOnlySpan<byte> data)
    {
        var pos = PngSignature.Length;
        var first = true;
        var size = 0;
        var hasAlpha = false;
        var sawIdat = false;
        while (data.Length - pos >= 12)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(data[pos..]);
            // The length must fit the file with type and CRC (also rules out 0xFFFFFFFF and any overflow).
            if (length > int.MaxValue || length > (uint)(data.Length - pos - 12))
            {
                return Reject(PngDamaged);
            }

            var body = data.Slice(pos + 4, (int)length + 4);
            if (!IsLetters(body[..4]))
            {
                return Reject(PngDamaged);
            }

            if (Crc32(body) != BinaryPrimitives.ReadUInt32BigEndian(data[(pos + 8 + (int)length)..]))
            {
                return Reject(PngDamaged);
            }

            var type = body[..4];
            var payload = body[4..];
            if (first)
            {
                if (!type.SequenceEqual("IHDR"u8) || payload.Length != 13)
                {
                    return Reject(PngDamaged);
                }

                var header = ParsePngHeader(payload);
                if (header.Problem is not null)
                {
                    return Reject(header.Problem);
                }

                size = header.Size;
                hasAlpha = header.HasAlpha;
                first = false;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return Reject(PngDamaged);
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawIdat = true;
            }
            else if (type.SequenceEqual("tRNS"u8))
            {
                // Only a tRNS before the image data counts as transparency.
                hasAlpha |= !sawIdat;
            }
            else if (type.SequenceEqual("acTL"u8))
            {
                return Reject(Animated);
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                // IEND ends the file: trailing bytes would be a second file hidden behind the image (polyglot).
                if (length != 0 || pos + 12 != data.Length || !sawIdat)
                {
                    return Reject(PngDamaged);
                }

                return hasAlpha ? new ImageCheckResult(AssetType.Png, size, null) : Reject(NoAlpha);
            }

            pos += 12 + (int)length;
        }

        // Truncated, or no IEND.
        return Reject(PngDamaged);
    }

    private static (int Size, bool HasAlpha, string? Problem) ParsePngHeader(ReadOnlySpan<byte> ihdr)
    {
        var width = BinaryPrimitives.ReadUInt32BigEndian(ihdr);
        var height = BinaryPrimitives.ReadUInt32BigEndian(ihdr[4..]);
        var depth = ihdr[8];
        var colour = ihdr[9];
        var validDepth = colour switch
        {
            0 => depth is 1 or 2 or 4 or 8 or 16,
            2 or 4 or 6 => depth is 8 or 16,
            3 => depth is 1 or 2 or 4 or 8,
            _ => false,
        };
        if (width == 0 || height == 0 || width > int.MaxValue || height > int.MaxValue || !validDepth
            || ihdr[10] != 0 || ihdr[11] != 0 || ihdr[12] > 1)
        {
            return (0, false, PngDamaged);
        }

        var problem = SizeProblem((int)width, (int)height);
        return (problem is null ? (int)width : 0, colour is 4 or 6, problem);
    }

    private static ImageCheckResult CheckWebp(ReadOnlySpan<byte> data)
    {
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
        if ((long)riffSize + 8 != data.Length || (riffSize & 1) != 0)
        {
            return Reject(WebpDamaged);
        }

        var pos = 12;
        var index = 0;
        var extended = false;
        var canvas = 0;
        var imageChunks = 0;
        var sawAlph = false;
        while (pos < data.Length)
        {
            if (data.Length - pos < 8)
            {
                return Reject(WebpDamaged);
            }

            var fourCc = data.Slice(pos, 4);
            var length = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            var padded = (long)length + (length & 1);
            if (padded > data.Length - pos - 8)
            {
                return Reject(WebpDamaged);
            }

            var payload = data.Slice(pos + 8, (int)length);
            if (index == 0)
            {
                if (fourCc.SequenceEqual("VP8X"u8))
                {
                    if (payload.Length != 10)
                    {
                        return Reject(WebpDamaged);
                    }

                    if ((payload[0] & 0x02) != 0)
                    {
                        return Reject(Animated);
                    }

                    if ((payload[0] & 0x10) == 0)
                    {
                        return Reject(NoAlpha);
                    }

                    var width = 1 + (payload[4] | (payload[5] << 8) | (payload[6] << 16));
                    var height = 1 + (payload[7] | (payload[8] << 8) | (payload[9] << 16));
                    var problem = SizeProblem(width, height);
                    if (problem is not null)
                    {
                        return Reject(problem);
                    }

                    extended = true;
                    canvas = width;
                }
                else if (fourCc.SequenceEqual("VP8L"u8))
                {
                    // Simple lossless file: this one chunk is the whole image.
                    if (pos + 8 + padded != data.Length)
                    {
                        return Reject(WebpDamaged);
                    }

                    var simple = ReadLosslessHeader(payload);
                    if (simple is null)
                    {
                        return Reject(WebpDamaged);
                    }

                    if (!simple.Value.Alpha)
                    {
                        return Reject(NoAlpha);
                    }

                    var problem = SizeProblem(simple.Value.Width, simple.Value.Height);
                    return problem is null ? new ImageCheckResult(AssetType.Webp, simple.Value.Width, null) : Reject(problem);
                }
                else if (fourCc.SequenceEqual("VP8 "u8))
                {
                    // Plain lossy WebP cannot carry transparency.
                    return Reject(NoAlpha);
                }
                else
                {
                    return Reject(WebpDamaged);
                }
            }
            else if (!extended)
            {
                return Reject(WebpDamaged);
            }
            else if (fourCc.SequenceEqual("ANIM"u8) || fourCc.SequenceEqual("ANMF"u8))
            {
                return Reject(Animated);
            }
            else if (fourCc.SequenceEqual("ALPH"u8))
            {
                sawAlph = imageChunks == 0;
            }
            else if (fourCc.SequenceEqual("VP8 "u8))
            {
                // Lossy image data carries its alpha in a separate ALPH chunk that must come first.
                if (++imageChunks > 1 || !sawAlph || !LossyMatches(payload, canvas))
                {
                    return Reject(WebpDamaged);
                }
            }
            else if (fourCc.SequenceEqual("VP8L"u8))
            {
                var lossless = ReadLosslessHeader(payload);
                if (++imageChunks > 1 || lossless is null || lossless.Value.Width != canvas || lossless.Value.Height != canvas)
                {
                    return Reject(WebpDamaged);
                }
            }

            pos += 8 + (int)padded;
            index++;
        }

        return extended && imageChunks == 1 ? new ImageCheckResult(AssetType.Webp, canvas, null) : Reject(WebpDamaged);
    }

    // VP8L header: signature 0x2F, then 14 bits width-1, 14 bits height-1, 1 bit alpha_is_used, 3 bits version (0).
    private static (int Width, int Height, bool Alpha)? ReadLosslessHeader(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 5 || payload[0] != 0x2F)
        {
            return null;
        }

        var bits = BinaryPrimitives.ReadUInt32LittleEndian(payload[1..]);
        if ((bits >> 29) != 0)
        {
            return null;
        }

        return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1, ((bits >> 28) & 1) == 1);
    }

    // VP8 key frame: 3 bytes frame tag, start code 9D 01 2A, then 14-bit width and height (little endian).
    private static bool LossyMatches(ReadOnlySpan<byte> payload, int canvas)
    {
        if (payload.Length < 10 || payload[3] != 0x9D || payload[4] != 0x01 || payload[5] != 0x2A)
        {
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(payload[6..]) & 0x3FFF;
        var height = BinaryPrimitives.ReadUInt16LittleEndian(payload[8..]) & 0x3FFF;
        return width == canvas && height == canvas;
    }

    private static string? SizeProblem(int width, int height)
    {
        if (width != height)
        {
            return $"The image must be square (it is {width} × {height} px).";
        }

        if (width < MinSize)
        {
            return $"The image must be at least {MinSize} × {MinSize} px.";
        }

        return width > MaxSize ? $"The image must be at most {MaxSize} × {MaxSize} px." : null;
    }

    private static bool IsLetters(ReadOnlySpan<byte> type)
    {
        foreach (var b in type)
        {
            if (!(b is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
            {
                return false;
            }
        }

        return true;
    }

    private static ImageCheckResult Reject(string problem) => new(null, 0, problem);

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            var c = n;
            for (var k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }

            table[n] = c;
        }

        return table;
    }

    /// <summary>CRC-32 (ISO-HDLC), as PNG uses it. System.IO.Hashing is a package and is not allowed (decisions §9).</summary>
    internal static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
        {
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        }

        return ~crc;
    }
}
