using System.Text;

namespace ButtonBox.Output.Scs.Install;

/// <summary>A parsed KeyValues object. Keys are case-insensitive like Steam's; a duplicate key keeps its first value.</summary>
internal sealed class VdfObject
{
    private readonly Dictionary<string, object> _items = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<KeyValuePair<string, object>> Entries => _items;

    internal bool TryAdd(string key, object value) => _items.TryAdd(key, value);

    public string? GetString(string key) => _items.TryGetValue(key, out var value) ? value as string : null;

    public VdfObject? GetObject(string key) => _items.TryGetValue(key, out var value) ? value as VdfObject : null;
}

/// <summary>
/// A small reader for the KeyValues text subset Steam uses in <c>libraryfolders.vdf</c> and <c>appmanifest_*.acf</c>: quoted keys and
/// values, nested braces and <c>//</c> comments. These files are user-writable, so the reader is strict and bounded (file size,
/// nesting depth, string length, node count) and never throws: anything it does not understand is "not parsed" (null).
/// </summary>
internal static class Vdf
{
    public const int MaxDepth = 16;
    public const int MaxKeyLength = 256;
    public const int MaxValueLength = 4096;
    public const int MaxNodes = 50_000;

    /// <summary>Parses text; null when it is malformed or over a limit.</summary>
    public static VdfObject? TryParse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new VdfObject();
        var stack = new Stack<VdfObject>();
        stack.Push(root);
        string? pendingKey = null;
        var nodes = 0;
        var i = 0;
        while (true)
        {
            if (!SkipBlanks(text, ref i))
            {
                return null;
            }

            if (i >= text.Length)
            {
                break;
            }

            if (++nodes > MaxNodes)
            {
                return null;
            }

            var c = text[i];
            if (c == '"')
            {
                var s = ReadString(text, ref i, pendingKey is null ? MaxKeyLength : MaxValueLength);
                if (s is null)
                {
                    return null;
                }

                if (pendingKey is null)
                {
                    pendingKey = s;
                }
                else
                {
                    stack.Peek().TryAdd(pendingKey, s);
                    pendingKey = null;
                }
            }
            else if (c == '{')
            {
                if (pendingKey is null || stack.Count - 1 >= MaxDepth)
                {
                    return null;
                }

                var child = new VdfObject();
                stack.Peek().TryAdd(pendingKey, child);
                stack.Push(child);
                pendingKey = null;
                i++;
            }
            else if (c == '}')
            {
                if (pendingKey is not null || stack.Count == 1)
                {
                    return null;
                }

                stack.Pop();
                i++;
            }
            else
            {
                return null;
            }
        }

        return pendingKey is null && stack.Count == 1 ? root : null;
    }

    /// <summary>Reads and parses a file of at most <paramref name="maxBytes"/> bytes; null when missing, too large, not UTF-8 or malformed.</summary>
    public static VdfObject? TryReadFile(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1);
            // Read at most one byte more than the cap: a file over the cap is detected without reading it all, and a file that
            // grows while it is read is still bounded.
            var buffer = new byte[(int)Math.Min(stream.Length, maxBytes) + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = stream.Read(buffer, total, buffer.Length - total);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }

            if (total > maxBytes)
            {
                return null;
            }

            var start = total >= 3 && buffer[0] == 0xEF && buffer[1] == 0xBB && buffer[2] == 0xBF ? 3 : 0;
            return TryParse(new UTF8Encoding(false, true).GetString(buffer, start, total - start));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    // Skips whitespace and // comments. False when a lone '/' is found.
    private static bool SkipBlanks(string text, ref int i)
    {
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '/')
            {
                if (i + 1 >= text.Length || text[i + 1] != '/')
                {
                    return false;
                }

                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }
            }
            else
            {
                break;
            }
        }

        return true;
    }

    // At the opening quote. Escapes: \\ \" \n \t; any other backslash is kept as written. A raw line break or a missing closing
    // quote fails.
    private static string? ReadString(string text, ref int i, int maxLength)
    {
        i++;
        var sb = new StringBuilder();
        while (i < text.Length)
        {
            var c = text[i++];
            if (c == '"')
            {
                return sb.ToString();
            }

            if (c is '\n' or '\r')
            {
                return null;
            }

            if (c == '\\' && i < text.Length)
            {
                var next = text[i];
                var mapped = next switch { '\\' => '\\', '"' => '"', 'n' => '\n', 't' => '\t', _ => '\0' };
                if (mapped != '\0')
                {
                    c = mapped;
                    i++;
                }
            }

            if (sb.Length >= maxLength)
            {
                return null;
            }

            sb.Append(c);
        }

        return null;
    }
}
