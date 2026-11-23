using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Security;

namespace ButtonBox.App.Profiles;

/// <summary>An image in the library: its id is the SHA-256 of its bytes.</summary>
public sealed record LibraryAsset(string Id, AssetType Type, int Size, int Bytes);

/// <summary>Outcome of <see cref="AssetLibrary.Import"/>. Exactly one of the two is set; the problem is one fixed sentence.</summary>
public sealed record AssetImportResult(LibraryAsset? Asset, string? Problem);

/// <summary>
/// The user's images, <c>assets\&lt;sha256&gt;.png|.webp</c> under the data folder (decisions §7). A file whose name, hash or
/// header does not check out is ignored: never indexed, never served, never overwritten and never deleted. A file is verified
/// when it is indexed (start, import) and again on a read only when its length or last write time differs from what was verified,
/// so an unauthenticated request never costs a hash of up to 4 MiB; a file that changed and no longer verifies is dropped from the
/// index. Thread-safe.
/// </summary>
public sealed class AssetLibrary : IAssetSource
{
    private static readonly Regex FileName = new(@"\A([0-9a-f]{64})\.(png|webp)\z", RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);

    private readonly string _dir;
    private readonly object _gate = new();
    private readonly SortedDictionary<string, Entry> _byId = new(StringComparer.Ordinal);
    private int _hashCount;

    public AssetLibrary(string directory)
    {
        ArgumentException.ThrowIfNullOrEmpty(directory);
        _dir = directory;
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var match = FileName.Match(Path.GetFileName(file));
            if (match.Success && Verify(file, match.Groups[1].Value) is { } entry)
            {
                _byId[entry.Asset.Id] = entry;
            }
        }
    }

    /// <summary>Test hook: how many times a file was hashed to verify it.</summary>
    internal int HashCount => Volatile.Read(ref _hashCount);

    private sealed record Entry(LibraryAsset Asset, long Length, DateTime Stamp);

    public bool Contains(string? id)
    {
        lock (_gate)
        {
            return id is not null && _byId.ContainsKey(id);
        }
    }

    /// <summary>Sorted by id.</summary>
    public IReadOnlyList<LibraryAsset> List()
    {
        lock (_gate)
        {
            return [.. _byId.Values.Select(e => e.Asset)];
        }
    }

    /// <summary>The same bytes always give the same id; importing them again changes nothing.</summary>
    public AssetImportResult Import(ReadOnlySpan<byte> bytes)
    {
        // The size is checked before anything is hashed.
        var check = ImageCheck.Check(bytes);
        if (check.Type is not { } type)
        {
            return new AssetImportResult(null, check.Problem ?? "The image cannot be imported.");
        }

        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        lock (_gate)
        {
            if (_byId.TryGetValue(id, out var existing))
            {
                return new AssetImportResult(existing.Asset, null);
            }

            var path = PathFor(id, type);
            try
            {
                if (File.Exists(path))
                {
                    // Named like this image but not usable (so it is not indexed). It is the user file: never replaced.
                    return new AssetImportResult(null, "The asset library holds a damaged file for this image.");
                }

                AtomicFile.WriteAllBytes(path, bytes);
                var info = new FileInfo(path);
                var asset = new LibraryAsset(id, type, check.Size, bytes.Length);
                _byId[id] = new Entry(asset, info.Length, info.LastWriteTimeUtc);
                return new AssetImportResult(asset, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return new AssetImportResult(null, "The image could not be saved.");
            }
        }
    }

    public bool TryGet(string? id, out AssetType type, out byte[] bytes)
    {
        type = default;
        bytes = [];
        lock (_gate)
        {
            if (id is null || !_byId.TryGetValue(id, out var entry))
            {
                return false;
            }

            var path = PathFor(entry.Asset.Id, entry.Asset.Type);
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length != entry.Length || info.LastWriteTimeUtc != entry.Stamp)
                {
                    // Changed behind our back: verify again, and drop it when it no longer checks out.
                    var fresh = info.Exists ? Verify(path, entry.Asset.Id) : null;
                    if (fresh is null)
                    {
                        _byId.Remove(id);
                        return false;
                    }

                    entry = fresh;
                    _byId[id] = entry;
                }

                bytes = File.ReadAllBytes(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _byId.Remove(id);
                bytes = [];
                return false;
            }

            type = entry.Asset.Type;
            return true;
        }
    }

    /// <summary>
    /// Removes the image. The caller has checked that no profile uses it (decisions §7). False when the library does not hold it
    /// or the file could not be removed.
    /// </summary>
    public bool Delete(string? id)
    {
        lock (_gate)
        {
            if (id is null || !_byId.TryGetValue(id, out var entry))
            {
                return false;
            }

            try
            {
                File.Delete(PathFor(entry.Asset.Id, entry.Asset.Type));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return false;
            }

            _byId.Remove(id);
            return true;
        }
    }

    private string PathFor(string id, AssetType type) => Path.Combine(_dir, id + (type == AssetType.Webp ? ".webp" : ".png"));

    // The name, the hash and the header must all agree; the type must match the extension. Null when anything does not.
    private Entry? Verify(string path, string id)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Length > ImageCheck.MaxBytes)
            {
                return null;
            }

            var data = File.ReadAllBytes(path);
            var check = ImageCheck.Check(data);
            var expected = path.EndsWith(".webp", StringComparison.Ordinal) ? AssetType.Webp : AssetType.Png;
            if (check.Type != expected)
            {
                return null;
            }

            Interlocked.Increment(ref _hashCount);
            if (!string.Equals(Convert.ToHexStringLower(SHA256.HashData(data)), id, StringComparison.Ordinal))
            {
                return null;
            }

            return new Entry(new LibraryAsset(id, expected, check.Size, data.Length), info.Length, info.LastWriteTimeUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
