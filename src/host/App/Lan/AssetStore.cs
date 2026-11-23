using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ButtonBox.Core.Contracts;

namespace ButtonBox.App.Lan;

/// <summary>
/// Images by the SHA-256 of their bytes (decisions §7). The id is computed here, never taken from a name or a config, so an
/// id on the wire can only ever select bytes the host itself holds. Immutable after construction. Holds the bundled PNG demo
/// images and serves LAN tests; the app serves the user's <see cref="Profiles.AssetLibrary"/>.
/// </summary>
public sealed class AssetStore : IAssetSource
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

    bool IAssetSource.TryGet(string? id, out AssetType type, out byte[] bytes)
    {
        type = AssetType.Png; // the bundled images are PNG files
        return TryGet(id, out bytes);
    }
}
