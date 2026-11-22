using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace ButtonBox.App.Lan;

/// <summary>
/// Images by the SHA-256 of their bytes (decisions §7). The id is computed here, never taken from a name or a config, so an
/// id on the wire can only ever select bytes the host itself holds. Immutable after construction.
/// </summary>
public sealed class AssetStore
{
    /// <summary>64 lowercase hex characters. Anchored with \A..\z: in .NET "$" also matches before a trailing newline.</summary>
    public const string IdPattern = @"\A[0-9a-f]{64}\z";

    private static readonly Regex IdRegex = new(IdPattern, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, byte[]> _byId = new(StringComparer.Ordinal);

    public AssetStore(IEnumerable<byte[]> images)
    {
        ArgumentNullException.ThrowIfNull(images);
        foreach (var image in images)
        {
            _byId[Convert.ToHexStringLower(SHA256.HashData(image))] = image;
        }
    }

    public IReadOnlyCollection<string> Ids => _byId.Keys;

    public static bool IsValidId(string? id) => id is not null && IdRegex.IsMatch(id);

    /// <summary>The bytes for a valid id that the store holds; false for anything else, including a malformed id.</summary>
    public bool TryGet(string? id, out byte[] bytes)
    {
        if (IsValidId(id) && _byId.TryGetValue(id!, out var found))
        {
            bytes = found;
            return true;
        }

        bytes = [];
        return false;
    }
}
