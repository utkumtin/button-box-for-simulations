using ButtonBox.Core.Contracts;

namespace ButtonBox.App.Lan;

/// <summary>Where the LAN route <c>/assets/{sha256}</c> gets its images (decisions §7). Implementations are thread-safe.</summary>
public interface IAssetSource
{
    /// <summary>
    /// The verified bytes and type for an id the source holds; false for anything else, including a malformed id.
    /// Never returns bytes whose hash differs from the id.
    /// </summary>
    bool TryGet(string? id, out AssetType type, out byte[] bytes);
}
