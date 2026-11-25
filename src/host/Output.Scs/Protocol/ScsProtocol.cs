using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ButtonBox.Core.Contracts;

namespace ButtonBox.Output.Scs.Protocol;

/// <summary>A message the plugin sends to the host (PROTOCOL.md).</summary>
public abstract record ScsPluginMessage;

/// <summary>First frame after every connect.</summary>
public sealed record ScsHello(uint Protocol, ScsGame Game, string Version) : ScsPluginMessage;

/// <summary>A telemetry channel's current value.</summary>
public sealed record ScsTel(string Field, int Value) : ScsPluginMessage;

/// <summary>1 while the game reports truck values.</summary>
public sealed record ScsAvail(bool Available) : ScsPluginMessage;

/// <summary>1 while the game accepts input.</summary>
public sealed record ScsListening(bool Listening) : ScsPluginMessage;

/// <summary>Answer to a ping.</summary>
public sealed record ScsPong(uint Number) : ScsPluginMessage;

/// <summary>A message the host sends to the plugin (PROTOCOL.md).</summary>
public abstract record ScsHostMessage;

/// <summary>One momentary press of a semantical input.</summary>
public sealed record ScsPress(string Input) : ScsHostMessage;

/// <summary>Liveness check.</summary>
public sealed record ScsPing(uint Number) : ScsHostMessage;

/// <summary>Codec for the SCS plugin pipe protocol, version 1. The grammars are exactly those of PROTOCOL.md.</summary>
public static class ScsProtocol
{
    /// <summary>The protocol number this host speaks.</summary>
    public const uint Version = 1;

    /// <summary>Largest payload of one frame.</summary>
    public const int MaxPayload = 256;

    /// <summary>The pipe name without the <c>\\.\pipe\</c> prefix; equal to the <c>pipe-name</c> line of vectors.txt.</summary>
    public const string PipeName = "button-box-for-simulations-scs";

    /// <summary>Parses a plugin-to-host payload. False for anything that is not a valid message.</summary>
    public static bool TryParsePluginMessage(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ScsPluginMessage? message)
    {
        message = null;
        if (!TryTokenize(payload, out var t))
        {
            return false;
        }

        switch (t[0])
        {
            case "hello" when t.Length == 4:
                if (IsUInt(t[1], out var protocol) && TryGame(t[2], out var game) && IsVersion(t[3]))
                {
                    message = new ScsHello(protocol, game, t[3]);
                }

                break;
            case "tel" when t.Length == 3:
                if (IsName(t[1]) && IsInt(t[2], out var value))
                {
                    message = new ScsTel(t[1], value);
                }

                break;
            case "avail" when t.Length == 2:
                if (IsBit(t[1], out var avail))
                {
                    message = new ScsAvail(avail);
                }

                break;
            case "listening" when t.Length == 2:
                if (IsBit(t[1], out var listening))
                {
                    message = new ScsListening(listening);
                }

                break;
            case "pong" when t.Length == 2:
                if (IsUInt(t[1], out var number))
                {
                    message = new ScsPong(number);
                }

                break;
        }

        return message is not null;
    }

    /// <summary>Parses a host-to-plugin payload (the plugin's view; used by tests and the fake plugin).</summary>
    public static bool TryParseHostMessage(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out ScsHostMessage? message)
    {
        message = null;
        if (!TryTokenize(payload, out var t))
        {
            return false;
        }

        switch (t[0])
        {
            case "press" when t.Length == 2:
                if (IsName(t[1]))
                {
                    message = new ScsPress(t[1]);
                }

                break;
            case "ping" when t.Length == 2:
                if (IsUInt(t[1], out var number))
                {
                    message = new ScsPing(number);
                }

                break;
        }

        return message is not null;
    }

    /// <summary>The payload bytes (no length prefix) of a plugin message. Throws for a message that violates a grammar.</summary>
    public static byte[] Encode(ScsPluginMessage message)
    {
        var text = message switch
        {
            ScsHello m => $"hello {U(m.Protocol)} {GameText(m.Game)} {RequireVersion(m.Version)}",
            ScsTel m => $"tel {RequireName(m.Field)} {m.Value.ToString(CultureInfo.InvariantCulture)}",
            ScsAvail m => $"avail {(m.Available ? '1' : '0')}",
            ScsListening m => $"listening {(m.Listening ? '1' : '0')}",
            ScsPong m => $"pong {U(m.Number)}",
            _ => throw new ArgumentException("Unknown message.", nameof(message)),
        };
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>The payload bytes (no length prefix) of a host message. Throws for a message that violates a grammar.</summary>
    public static byte[] Encode(ScsHostMessage message)
    {
        var text = message switch
        {
            ScsPress m => $"press {RequireName(m.Input)}",
            ScsPing m => $"ping {U(m.Number)}",
            _ => throw new ArgumentException("Unknown message.", nameof(message)),
        };
        return Encoding.ASCII.GetBytes(text);
    }

    /// <summary>Prefixes a payload with its 2-byte little-endian length.</summary>
    public static byte[] Frame(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > MaxPayload)
        {
            throw new ArgumentOutOfRangeException(nameof(payload));
        }

        var frame = new byte[2 + payload.Length];
        frame[0] = (byte)(payload.Length & 0xFF);
        frame[1] = (byte)(payload.Length >> 8);
        payload.CopyTo(frame.AsSpan(2));
        return frame;
    }

    /// <summary><c>[a-z][a-z0-9_.]*</c>, 1 to 64 bytes.</summary>
    public static bool IsName(string text)
    {
        if (text.Length is < 1 or > 64 || text[0] is < 'a' or > 'z')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsVersion(string text)
    {
        if (text.Length is < 1 or > 32)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or '.' or '+' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsBit(string text, out bool bit)
    {
        bit = text == "1";
        return text is "0" or "1";
    }

    /// <summary><c>0</c> or <c>[1-9][0-9]*</c>, 0 to 4294967295.</summary>
    private static bool IsUInt(string text, out uint value)
    {
        value = 0;
        if (!IsCanonicalDigits(text) || text.Length > 10)
        {
            return false;
        }

        var parsed = ulong.Parse(text, NumberStyles.None, CultureInfo.InvariantCulture);
        if (parsed > uint.MaxValue)
        {
            return false;
        }

        value = (uint)parsed;
        return true;
    }

    /// <summary><c>0</c> or <c>-?[1-9][0-9]*</c>, within the int range.</summary>
    private static bool IsInt(string text, out int value)
    {
        value = 0;
        var negative = text.StartsWith('-');
        var digits = negative ? text[1..] : text;
        if (!IsCanonicalDigits(digits) || digits.Length > 10 || (negative && digits == "0"))
        {
            return false;
        }

        var parsed = long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);
        if (negative)
        {
            parsed = -parsed;
        }

        if (parsed is < int.MinValue or > int.MaxValue)
        {
            return false;
        }

        value = (int)parsed;
        return true;
    }

    private static bool IsCanonicalDigits(string text)
    {
        if (text.Length == 0 || (text[0] == '0' && text.Length > 1))
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryGame(string text, out ScsGame game)
    {
        game = text == "ats" ? ScsGame.Ats : ScsGame.Ets2;
        return text is "ets2" or "ats";
    }

    private static string GameText(ScsGame game) => game switch
    {
        ScsGame.Ets2 => "ets2",
        ScsGame.Ats => "ats",
        _ => throw new ArgumentOutOfRangeException(nameof(game)),
    };

    private static string U(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string RequireName(string name) =>
        IsName(name) ? name : throw new ArgumentException("Not a valid name.", nameof(name));

    private static string RequireVersion(string version) =>
        IsVersion(version) ? version : throw new ArgumentException("Not a valid version.", nameof(version));

    /// <summary>Printable ASCII tokens separated by exactly one space; 1 to 256 bytes.</summary>
    private static bool TryTokenize(ReadOnlySpan<byte> payload, [NotNullWhen(true)] out string[]? tokens)
    {
        tokens = null;
        if (payload.Length is < 1 or > MaxPayload)
        {
            return false;
        }

        foreach (var b in payload)
        {
            if (b is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        var parts = Encoding.ASCII.GetString(payload).Split(' ');
        foreach (var part in parts)
        {
            if (part.Length == 0)
            {
                return false;
            }
        }

        tokens = parts;
        return true;
    }
}
