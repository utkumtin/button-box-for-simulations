using System.Security.Cryptography;

namespace ButtonBox.Discovery;

/// <summary>The generated host label, e.g. <c>buttonbox-7f3a</c> (the full name is <c>buttonbox-7f3a.local</c>).</summary>
public static class MdnsName
{
    private const string Prefix = "buttonbox-";
    private const int SuffixLength = 4;

    public static string Generate() => Prefix + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(SuffixLength / 2));

    /// <summary>True only for exactly <c>buttonbox-</c> plus 4 lower-case hex digits. Also used on a persisted value before it is trusted.</summary>
    public static bool IsValid(string? label)
    {
        if (label is null || label.Length != Prefix.Length + SuffixLength || !label.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in label.AsSpan(Prefix.Length))
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        return true;
    }
}
