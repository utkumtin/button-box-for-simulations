using System.Text.Json;
using ButtonBox.Core;
using ButtonBox.Discovery;

namespace ButtonBox.App;

/// <summary>Outcome of reading <c>settings.json</c>. Exactly one of the two is set.</summary>
public sealed record SettingsResult(string? MdnsName, string? Problem);

/// <summary>
/// <c>settings.json</c> holds the generated mDNS name (decisions §8, §12). It is created once. A file that exists but cannot be
/// trusted is never overwritten: a new name would change the phones' home-screen address.
/// </summary>
public static class AppSettings
{
    public const string FileName = "settings.json";
    private const int MaxBytes = 4096;
    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static SettingsResult LoadOrCreate(string dataDir)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        var path = Path.Combine(dataDir, FileName);
        try
        {
            if (File.Exists(path))
            {
                return Read(path);
            }

            var name = MdnsName.Generate();
            var model = new Model { SchemaVersion = FormatVersion, MdnsName = name };
            AtomicFile.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(model, Json));
            return new SettingsResult(name, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new SettingsResult(null, FileName + " cannot be read or written.");
        }
    }

    private static SettingsResult Read(string path)
    {
        var invalid = new SettingsResult(null, FileName + " is damaged or not valid.");
        if (new FileInfo(path).Length > MaxBytes)
        {
            return invalid;
        }

        try
        {
            var model = JsonSerializer.Deserialize<Model>(File.ReadAllBytes(path), Json);
            return model is { SchemaVersion: FormatVersion } && MdnsName.IsValid(model.MdnsName)
                ? new SettingsResult(model.MdnsName, null)
                : invalid;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return invalid;
        }
    }

    private sealed class Model
    {
        [System.Text.Json.Serialization.JsonPropertyName("schemaVersion")]
        [System.Text.Json.Serialization.JsonRequired]
        public int SchemaVersion { get; init; }

        [System.Text.Json.Serialization.JsonPropertyName("mdnsName")]
        [System.Text.Json.Serialization.JsonRequired]
        public string? MdnsName { get; init; }
    }
}
