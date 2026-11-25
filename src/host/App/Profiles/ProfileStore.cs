using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButtonBox.App.Lan;
using ButtonBox.Core;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using ButtonBox.Security;

namespace ButtonBox.App.Profiles;

/// <summary>
/// The user's profiles (decisions §6): one file per profile, <c>profiles\&lt;id&gt;.json</c>, and the active profile id in
/// <c>active-profile.json</c>. A profile file that cannot be read or does not pass the <see cref="ProfileGate"/> is damaged: it is
/// listed, never rewritten and only deleted when the user asks. Every call is serialized by one lock and every write is atomic.
/// A Save or an activation publishes the new engine to the <see cref="GameSession"/> after the file is written and never before
/// (decisions §3 "Publishing"). The publish runs inside the store's own lock so that two calls cannot land in the wrong order;
/// no phone path ever takes this lock and <see cref="GameSession.Publish"/> never waits for a phone.
/// </summary>
public sealed class ProfileStore
{
    public const string ActiveFileName = "active-profile.json";
    private const int ActiveMaxBytes = 4096;
    private const int FormatVersion = 1;
    private const int SlugMaxLength = 40;
    private const string CopySuffix = " copy";

    private static readonly JsonSerializerOptions ActiveJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly object _gate = new();
    private readonly string _profilesDir;
    private readonly string _activePath;
    private readonly ProfileGate _validation;
    private readonly CatalogStore _catalogs;
    private readonly GameSession _session;
    private readonly TimeProvider _time;
    private readonly Action<string, byte[]> _write;
    private readonly List<string> _problems = [];
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private string? _activeId;

    /// <param name="time">The clock of the engines this store builds. It must be the clock of <paramref name="session"/> (their deadlines are absolute times); null is the system clock.</param>
    public ProfileStore(string dataDir, ProfileGate gate, CatalogStore catalogs, GameSession session, TimeProvider? time = null)
        : this(dataDir, gate, catalogs, session, (path, bytes) => AtomicFile.WriteAllBytes(path, bytes), time)
    {
    }

    /// <param name="write">Test seam: how a file is written (default: <see cref="AtomicFile"/>). It may throw <see cref="IOException"/>.</param>
    internal ProfileStore(string dataDir, ProfileGate gate, CatalogStore catalogs, GameSession session, Action<string, byte[]> write, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        ArgumentNullException.ThrowIfNull(gate);
        ArgumentNullException.ThrowIfNull(catalogs);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(write);
        _profilesDir = Path.Combine(dataDir, "profiles");
        _activePath = Path.Combine(dataDir, ActiveFileName);
        _validation = gate;
        _catalogs = catalogs;
        _session = session;
        _write = write;
        _activeId = ReadActiveId();
    }

    /// <summary>Faults found at start (the active-profile file), as fixed sentences.</summary>
    public IReadOnlyList<string> Problems
    {
        get
        {
            lock (_gate)
            {
                return [.. _problems];
            }
        }
    }

    /// <summary>The active profile's id, or null when none is set or its file is missing or damaged.</summary>
    public string? ActiveId
    {
        get
        {
            lock (_gate)
            {
                return EffectiveActiveId();
            }
        }
    }

    /// <summary>The engine for the active profile, or null when there is none. Used once at start to publish to the game session.</summary>
    public ProfileEngine? CreateActiveEngine()
    {
        lock (_gate)
        {
            return _activeId is { } id && Load(id).Profile is { } profile ? BuildEngine(profile) : null;
        }
    }

    /// <summary>Every profile file with a valid name: healthy ones by name, then damaged ones (without name and game) by id.</summary>
    public ProfileList List()
    {
        lock (_gate)
        {
            var summaries = new List<ProfileSummary>();
            foreach (var id in Ids())
            {
                summaries.Add(Load(id).Profile is { } profile
                    ? new ProfileSummary { Id = id, Damaged = false, Name = profile.Name, Game = profile.Game }
                    : new ProfileSummary { Id = id, Damaged = true });
            }

            var ordered = summaries
                .OrderBy(s => s.Damaged)
                .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(s => s.Id, StringComparer.Ordinal)
                .ToList();
            return new ProfileList { ActiveId = EffectiveActiveId(), Profiles = ordered };
        }
    }

    public StoreResult<Profile> Get(string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            return StoreResult<Profile>.NotFound();
        }

        lock (_gate)
        {
            return Existing(Load(id));
        }
    }

    /// <summary>An empty profile (no layouts) for a game that has a catalog. The new id is a slug of the name plus four random hex digits.</summary>
    public StoreResult<Profile> Create(string name, Game game)
    {
        ArgumentNullException.ThrowIfNull(name);
        lock (_gate)
        {
            var profile = new Profile { SchemaVersion = ProfileValidator.SchemaVersion, Id = NewId(name), Name = name, Game = game, Layouts = [] };
            return Store(profile, publish: false);
        }
    }

    /// <summary>
    /// Replaces the stored profile. Its id and game must be the stored ones. The stored file must exist and be healthy: a damaged file
    /// is a conflict, never replaced. Order: validate, write, then swap the engine only when the profile is active.
    /// </summary>
    public StoreResult<Profile> Save(string id, Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!ContractValidator.IsValidId(id))
        {
            return StoreResult<Profile>.NotFound();
        }

        lock (_gate)
        {
            var current = Load(id);
            if (!current.Exists)
            {
                return StoreResult<Profile>.NotFound();
            }

            if (current.Profile is not { } stored)
            {
                return StoreResult<Profile>.Conflict("The stored profile is damaged and is never replaced by the app.");
            }

            if (profile.Id != id)
            {
                return StoreResult<Profile>.Invalid(["The profile id does not match."]);
            }

            if (profile.Game != stored.Game)
            {
                return StoreResult<Profile>.Invalid(["The game of a profile cannot be changed."]);
            }

            return Store(profile, publish: string.Equals(_activeId, id, StringComparison.Ordinal));
        }
    }

    /// <summary>Adds a profile from its JSON. It always gets a new id, never overwrites a profile and must pass the whole gate.</summary>
    public StoreResult<Profile> Import(ReadOnlySpan<byte> json)
    {
        var parsed = _validation.Parse(json);
        if (parsed.Profile is not { } profile)
        {
            return StoreResult<Profile>.Invalid(parsed.Problems);
        }

        lock (_gate)
        {
            profile.Id = NewId(profile.Name ?? string.Empty);
            return Store(profile, publish: false);
        }
    }

    /// <summary>A copy named "name copy" (cut to 64 characters) with a new id. A damaged profile cannot be copied.</summary>
    public StoreResult<Profile> Duplicate(string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            return StoreResult<Profile>.NotFound();
        }

        lock (_gate)
        {
            var existing = Existing(Load(id));
            if (existing.Value is not { } source)
            {
                return existing;
            }

            var copy = JsonSerializer.Deserialize<Profile>(Serialize(source), ContractJson.Options)!;
            copy.Name = CutToCodePoints(source.Name + CopySuffix, ProfileValidator.ProfileNameMaxLength);
            copy.Id = NewId(copy.Name);
            // A copy is exactly as usable as its source: a profile that runs can be duplicated, whatever the full gate says about it.
            return Store(copy, publish: false, full: false);
        }
    }

    /// <summary>
    /// Removes the file. The active profile cannot be deleted (switch first). A damaged file can, because the user asked; when it was
    /// the active one the phones are told there is no layout.
    /// </summary>
    public StoreResult<string> Delete(string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            return StoreResult<string>.NotFound();
        }

        lock (_gate)
        {
            var loaded = Load(id);
            if (!loaded.Exists)
            {
                return StoreResult<string>.NotFound();
            }

            var active = string.Equals(_activeId, id, StringComparison.Ordinal);
            if (active && loaded.Profile is not null)
            {
                return StoreResult<string>.Conflict("The active profile cannot be deleted; switch to another profile first.");
            }

            try
            {
                File.Delete(ProfilePath(id));
                _cache.Remove(ProfilePath(id));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return StoreResult<string>.WriteFailed();
            }

            if (active)
            {
                _session.Publish(null, keepSwitchPositions: false);
            }

            return StoreResult<string>.Success(id);
        }
    }

    /// <summary>Makes a healthy profile the active one and publishes it; every switch starts at position 0 (decisions §3 "Publishing").</summary>
    public StoreResult<Profile> Activate(string id)
    {
        if (!ContractValidator.IsValidId(id))
        {
            return StoreResult<Profile>.NotFound();
        }

        lock (_gate)
        {
            var loaded = Load(id);
            var existing = Existing(loaded);
            if (loaded.Profile is not { } profile)
            {
                return existing;
            }

            // A stray click on the profile that already runs must not reset the switches. Only when the phones really have it: after a
            // start with a damaged file the session has no engine, and activating the repaired profile has to publish it.
            if (string.Equals(_activeId, id, StringComparison.Ordinal) && _session.Engine is not null)
            {
                return existing;
            }

            var engine = BuildEngine(profile);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new ActiveModel { SchemaVersion = FormatVersion, ProfileId = id }, ActiveJson);
            if (!TryWrite(_activePath, bytes))
            {
                return StoreResult<Profile>.WriteFailed();
            }

            _activeId = id;
            _session.Publish(engine, keepSwitchPositions: false);
            return existing;
        }
    }

    /// <summary>Ids of the profiles that use the image. Profiles that cannot be parsed at all are not known to use anything.</summary>
    public IReadOnlyList<string> ProfilesUsing(string assetId)
    {
        lock (_gate)
        {
            return [.. Usage().Where(u => u.Value.Contains(assetId, StringComparer.Ordinal)).Select(u => u.Key)];
        }
    }

    /// <summary>One pass over the profiles: image id to the ids of the profiles that use it. For the asset list's "used by".</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<string>> AssetUsage()
    {
        lock (_gate)
        {
            return Usage()
                .SelectMany(u => u.Value.Select(asset => (Asset: asset, Profile: u.Key)))
                .GroupBy(x => x.Asset, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)[.. g.Select(x => x.Profile).Order(StringComparer.Ordinal)], StringComparer.Ordinal);
        }
    }

    /// <summary>Every image some profile uses, which the library must therefore keep (decisions §7).</summary>
    public IReadOnlySet<string> UsedAssetIds()
    {
        lock (_gate)
        {
            return Usage().SelectMany(u => u.Value).ToHashSet(StringComparer.Ordinal);
        }
    }

    // ---- everything below runs inside the lock ----

    private sealed record Loaded(bool Exists, Profile? Profile, Profile? Parsed, IReadOnlyList<string> Problems);

    private string ProfilePath(string id) => Path.Combine(_profilesDir, id + ".json");

    private string? EffectiveActiveId() => _activeId is { } id && Load(id).Profile is not null ? id : null;

    private List<string> Ids()
    {
        if (!Directory.Exists(_profilesDir))
        {
            return [];
        }

        return [.. Directory.EnumerateFiles(_profilesDir, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OfType<string>()
            .Where(ContractValidator.IsValidId)
            .Order(StringComparer.Ordinal)];
    }

    private Dictionary<string, IReadOnlyList<string>> Usage()
    {
        var usage = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var id in Ids())
        {
            // Parsed, not Profile: a profile whose only fault is a missing image still uses the images it names.
            if (Load(id).Parsed is { } parsed)
            {
                usage[id] = ProfileGate.AssetIds(parsed);
            }
        }

        return usage;
    }

    // Stored files are checked once per (path, length, last write time). Our own writes and deletes drop the entry; a file that
    // changed or appeared outside the app has another stamp or no entry, so the next call reads it again.
    private Loaded Load(string id)
    {
        var path = ProfilePath(id);
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists)
            {
                _cache.Remove(path);
                return new Loaded(false, null, null, ["The profile does not exist."]);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Damaged("The profile file cannot be read.");
        }

        var length = info.Length;
        var stamp = info.LastWriteTimeUtc;
        if (_cache.TryGetValue(path, out var cached) && cached.Length == length && cached.Stamp == stamp)
        {
            return cached.Loaded;
        }

        var (loaded, cacheable) = LoadFile(path, id, length);
        if (cacheable)
        {
            _cache[path] = new CacheEntry(length, stamp, loaded);
        }

        return loaded;
    }

    // The file is untrusted: its size is checked before it is read, and it must pass the stored-file gate and carry its own file name as id.
    private (Loaded Loaded, bool Cacheable) LoadFile(string path, string id, long length)
    {
        if (length > ProfileGate.MaxBytes)
        {
            return (Damaged("The profile file is larger than 1 MiB."), true);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (Damaged("The profile file cannot be read."), false); // maybe a passing lock: try again next time
        }

        var parsed = _validation.Parse(bytes);
        if (parsed.Profile is not { } profile)
        {
            return (new Loaded(true, null, null, parsed.Problems), true);
        }

        if (!string.Equals(profile.Id, id, StringComparison.Ordinal))
        {
            return (Damaged("The profile id does not match its file name."), true);
        }

        var gated = _validation.CheckStored(profile);
        return (new Loaded(true, gated.Ok ? profile : null, gated.StructureValid ? profile : null, gated.Problems), true);
    }

    private sealed record CacheEntry(long Length, DateTime Stamp, Loaded Loaded);

    private static Loaded Damaged(string problem) => new(true, null, null, [problem]);

    private static StoreResult<Profile> Existing(Loaded loaded) => loaded switch
    {
        { Exists: false } => StoreResult<Profile>.NotFound(),
        { Profile: { } profile } => StoreResult<Profile>.Success(Clone(profile)),
        _ => StoreResult<Profile>.Conflict([.. loaded.Problems]),
    };

    // Gate, serialize, write, publish: in this order, and the publish only after the write succeeded.
    private StoreResult<Profile> Store(Profile profile, bool publish, bool full = true)
    {
        var gated = full ? _validation.Check(profile) : _validation.CheckStored(profile);
        if (!gated.Ok)
        {
            return StoreResult<Profile>.Invalid(gated.Problems);
        }

        var bytes = Serialize(profile);
        if (bytes.Length > ProfileGate.MaxBytes)
        {
            return StoreResult<Profile>.Invalid(["The profile is larger than 1 MiB."]);
        }

        var engine = publish ? BuildEngine(profile) : null;
        if (!TryWrite(ProfilePath(profile.Id), bytes))
        {
            return StoreResult<Profile>.WriteFailed();
        }

        if (publish)
        {
            _session.Publish(engine, keepSwitchPositions: true);
        }

        return StoreResult<Profile>.Success(profile);
    }

    private ProfileEngine BuildEngine(Profile profile) =>
        _catalogs.TryGet(profile.Game, out var catalog)
            ? new ProfileEngine(profile, catalog, _time)
            : throw new InvalidOperationException("The profile passed the gate, so its catalog exists.");

    private bool TryWrite(string path, byte[] bytes)
    {
        try
        {
            _cache.Remove(path);
            _write(path, bytes);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The cache keeps its own copy: whatever a caller does with a returned profile cannot change what the store believes is on disk.
    private static Profile Clone(Profile profile) => JsonSerializer.Deserialize<Profile>(Serialize(profile), ContractJson.Options)!;

    private static byte[] Serialize(Profile profile) => JsonSerializer.SerializeToUtf8Bytes(profile, ContractJson.Options);

    // A slug of the name plus four random hex digits, never an id that already has a file (decisions §6).
    private string NewId(string name)
    {
        var slug = Slug(name);
        while (true)
        {
            var id = slug + "-" + RandomNumberGenerator.GetHexString(4, lowercase: true);
            if (!File.Exists(ProfilePath(id)))
            {
                return id;
            }
        }
    }

    internal static string Slug(string name)
    {
        var builder = new StringBuilder();
        foreach (var c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                builder.Append(c);
            }
            else if (builder.Length > 0 && builder[^1] != '-')
            {
                builder.Append('-');
            }
        }

        var slug = builder.ToString().Trim('-');
        if (slug.Length > SlugMaxLength)
        {
            slug = slug[..SlugMaxLength].Trim('-');
        }

        return slug.Length == 0 ? "profile" : slug;
    }

    private static string CutToCodePoints(string text, int maxCodePoints)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length && count < maxCodePoints)
        {
            index += char.IsHighSurrogate(text[index]) && index + 1 < text.Length ? 2 : 1;
            count++;
        }

        return text[..index];
    }

    // Missing means no active profile. A file that cannot be trusted also does; it is overwritten by the next activation
    // because it holds nothing precious.
    private string? ReadActiveId()
    {
        try
        {
            var info = new FileInfo(_activePath);
            if (!info.Exists)
            {
                return null;
            }

            if (info.Length <= ActiveMaxBytes
                && JsonSerializer.Deserialize<ActiveModel>(File.ReadAllBytes(_activePath), ActiveJson) is { SchemaVersion: FormatVersion } model
                && ContractValidator.IsValidId(model.ProfileId))
            {
                return model.ProfileId;
            }
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            // Same outcome as a file that does not validate.
        }

        _problems.Add(ActiveFileName + " is damaged or cannot be read, so no profile is active.");
        return null;
    }

    private sealed class ActiveModel
    {
        [JsonPropertyName("schemaVersion")]
        [JsonRequired]
        public int SchemaVersion { get; init; }

        [JsonPropertyName("profileId")]
        [JsonRequired]
        public string? ProfileId { get; init; }
    }
}
