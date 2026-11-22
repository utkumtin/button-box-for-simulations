using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace ButtonBox.Security;

/// <summary>Per-launch secret of the loopback editor API, compared in constant time (decisions §9).</summary>
public sealed class EditorSecret
{
    private readonly byte[] _bytes;

    private EditorSecret(string value)
    {
        Value = value;
        _bytes = Encoding.UTF8.GetBytes(value);
    }

    public string Value { get; }

    public static EditorSecret Create() => new(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32)));

    public bool Matches(string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        // A different length is rejected up front; the length of the secret is not secret.
        var bytes = Encoding.UTF8.GetBytes(candidate);
        return bytes.Length == _bytes.Length && CryptographicOperations.FixedTimeEquals(bytes, _bytes);
    }
}
