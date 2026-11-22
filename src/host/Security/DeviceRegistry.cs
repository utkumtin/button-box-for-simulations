using System.Buffers.Text;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ButtonBox.Core;

namespace ButtonBox.Security;

/// <summary>A paired phone. The token itself is never stored; only its hash is (inside the registry).</summary>
public sealed record DeviceRecord(string Id, string Name, string TemplateId, DateTimeOffset PairedAt, bool ScreenMismatch);

/// <summary>devices.json is unreadable or invalid. The file is left untouched so no pairing is lost.</summary>
public sealed class DeviceRegistryException : Exception
{
    public DeviceRegistryException(string message) : base(message)
    {
    }

    public DeviceRegistryException(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>The registry already holds <see cref="DeviceRegistry.MaxDevices"/> devices.</summary>
public sealed class DeviceRegistryFullException : Exception
{
    public DeviceRegistryFullException() : base("The maximum number of paired devices has been reached.")
    {
    }
}

/// <summary>Paired devices (devices.json). Thread-safe; every change is persisted before it becomes visible.</summary>
public sealed class DeviceRegistry
{
    public const int MaxDevices = 64;
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    private readonly string _path;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private List<Entry> _entries;

    private DeviceRegistry(string path, TimeProvider time, List<Entry> entries)
    {
        _path = path;
        _time = time;
        _entries = entries;
    }

    /// <summary>Raised after a device was revoked and the change is on disk. The argument is the device id.</summary>
    public event Action<string>? Revoked;

    public IReadOnlyList<DeviceRecord> Devices
    {
        get
        {
            lock (_gate)
            {
                return _entries.Select(e => e.Record).ToArray();
            }
        }
    }

    /// <summary>A missing file is an empty registry. An unreadable or invalid file throws <see cref="DeviceRegistryException"/> and is never overwritten.</summary>
    public static DeviceRegistry Load(string devicesJsonPath, TimeProvider? time = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(devicesJsonPath);
        var entries = new List<Entry>();
        if (File.Exists(devicesJsonPath))
        {
            try
            {
                var file = JsonSerializer.Deserialize<FileModel>(File.ReadAllBytes(devicesJsonPath), JsonOptions)
                    ?? throw new DeviceRegistryException("devices.json is empty.");
                if (file.SchemaVersion != FormatVersion)
                {
                    throw new DeviceRegistryException("devices.json has an unsupported schemaVersion.");
                }

                if (file.Devices.Count > MaxDevices)
                {
                    throw new DeviceRegistryException("devices.json lists too many devices.");
                }

                foreach (var d in file.Devices)
                {
                    var hash = ParseHash(d.TokenHash);
                    if (!ContractValidator.IsValidId(d.Id) || !ContractValidator.IsValidId(d.TemplateId)
                        || !ContractValidator.IsValidDeviceName(d.Name) || hash is null
                        || entries.Any(e => e.Record.Id == d.Id))
                    {
                        throw new DeviceRegistryException("devices.json contains an invalid or duplicate device entry.");
                    }

                    entries.Add(new Entry(new DeviceRecord(d.Id, d.Name, d.TemplateId, d.PairedAt, d.ScreenMismatch), hash));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                throw new DeviceRegistryException("devices.json cannot be read.", e);
            }
        }

        return new DeviceRegistry(devicesJsonPath, time ?? TimeProvider.System, entries);
    }

    /// <summary>Adds a device and returns it with its new token, which exists nowhere else. Persisted before returning.</summary>
    public (DeviceRecord Device, string Token) Add(string name, string templateId, bool screenMismatch)
    {
        if (!ContractValidator.IsValidDeviceName(name))
        {
            throw new ArgumentException("Invalid device name.", nameof(name));
        }

        if (!ContractValidator.IsValidId(templateId))
        {
            throw new ArgumentException("Invalid template id.", nameof(templateId));
        }

        lock (_gate)
        {
            if (_entries.Count >= MaxDevices)
            {
                throw new DeviceRegistryFullException();
            }

            string id;
            do
            {
                id = "d-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));
            }
            while (_entries.Any(e => e.Record.Id == id));

            var tokenBytes = RandomNumberGenerator.GetBytes(ContractValidator.TokenBytes);
            var token = Base64Url.EncodeToString(tokenBytes);
            var record = new DeviceRecord(id, name, templateId, _time.GetUtcNow(), screenMismatch);
            var entry = new Entry(record, SHA256.HashData(tokenBytes));
            CryptographicOperations.ZeroMemory(tokenBytes);
            Persist([.. _entries, entry]);
            return (record, token);
        }
    }

    /// <summary>Returns the device owning the token, or null when the token is malformed or unknown ("token rejected").</summary>
    public DeviceRecord? Authenticate(string? token)
    {
        if (!ContractValidator.TryDecodeToken(token, out var bytes))
        {
            return null;
        }

        var hash = SHA256.HashData(bytes);
        CryptographicOperations.ZeroMemory(bytes);
        // Hash first, then compare every stored hash in constant time without an early exit: a lookup by hash would leak
        // through timing, and the token already has 256 bits of entropy so no slow hash is needed.
        DeviceRecord? found = null;
        lock (_gate)
        {
            foreach (var e in _entries)
            {
                if (CryptographicOperations.FixedTimeEquals(hash, e.Hash))
                {
                    found = e.Record;
                }
            }
        }

        return found;
    }

    public bool Rename(string id, string name)
    {
        if (!ContractValidator.IsValidDeviceName(name))
        {
            throw new ArgumentException("Invalid device name.", nameof(name));
        }

        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Record.Id == id);
            if (index < 0)
            {
                return false;
            }

            var next = new List<Entry>(_entries);
            next[index] = next[index] with { Record = next[index].Record with { Name = name } };
            Persist(next);
            return true;
        }
    }

    /// <summary>Removes the device, persists, then raises <see cref="Revoked"/>. False when the id is unknown.</summary>
    public bool Revoke(string id)
    {
        lock (_gate)
        {
            var index = _entries.FindIndex(e => e.Record.Id == id);
            if (index < 0)
            {
                return false;
            }

            var next = new List<Entry>(_entries);
            next.RemoveAt(index);
            Persist(next);
        }

        // Outside the lock so a handler may call back into the registry.
        Revoked?.Invoke(id);
        return true;
    }

    private void Persist(List<Entry> next)
    {
        var model = new FileModel
        {
            SchemaVersion = FormatVersion,
            Devices = next.Select(e => new DeviceModel
            {
                Id = e.Record.Id,
                Name = e.Record.Name,
                TemplateId = e.Record.TemplateId,
                TokenHash = Convert.ToHexStringLower(e.Hash),
                PairedAt = e.Record.PairedAt,
                ScreenMismatch = e.Record.ScreenMismatch,
            }).ToList(),
        };
        AtomicFile.WriteAllBytes(_path, JsonSerializer.SerializeToUtf8Bytes(model, JsonOptions));
        _entries = next;
    }

    private static byte[]? ParseHash(string? hex)
    {
        if (hex is not { Length: 64 })
        {
            return null;
        }

        foreach (var c in hex)
        {
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return null;
            }
        }

        return Convert.FromHexString(hex);
    }

    private sealed record Entry(DeviceRecord Record, byte[] Hash);

    private sealed class FileModel
    {
        [JsonPropertyName("schemaVersion")]
        public required int SchemaVersion { get; init; }

        [JsonPropertyName("devices")]
        public required List<DeviceModel> Devices { get; init; }
    }

    private sealed class DeviceModel
    {
        [JsonPropertyName("id")]
        public required string Id { get; init; }

        [JsonPropertyName("name")]
        public required string Name { get; init; }

        [JsonPropertyName("templateId")]
        public required string TemplateId { get; init; }

        [JsonPropertyName("tokenHash")]
        public required string TokenHash { get; init; }

        [JsonPropertyName("pairedAt")]
        public required DateTimeOffset PairedAt { get; init; }

        [JsonPropertyName("screenMismatch")]
        public required bool ScreenMismatch { get; init; }
    }
}
