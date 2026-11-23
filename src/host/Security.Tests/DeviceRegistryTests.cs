using Xunit;
using System.Buffers.Text;
using System.Security.Cryptography;
using ButtonBox.Security;

namespace ButtonBox.Security.Tests;

public sealed class DeviceRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "bb-reg-" + Guid.NewGuid().ToString("N"));
    private string PathOf => Path.Combine(_dir, "devices.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    // Why: a leaked devices.json must not let anyone impersonate a phone; only the hash may be stored.
    [Fact]
    public void File_never_contains_the_token_in_any_form()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (device, token) = registry.Add("Utku iPhone", "iphone-17", false);
        var text = File.ReadAllText(PathOf);
        var raw = Base64Url.DecodeFromChars(token);
        Assert.DoesNotContain(token, text);
        Assert.DoesNotContain(Convert.ToHexStringLower(raw), text);
        Assert.DoesNotContain(Convert.ToHexString(raw), text);
        Assert.DoesNotContain(Convert.ToBase64String(raw), text);
        Assert.Contains(device.Id, text);
    }

    // Why: the stored hash is the contract with the (future) reader; it must be SHA-256 of the decoded 32 bytes.
    [Fact]
    public void Stored_hash_is_sha256_of_the_decoded_token()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (_, token) = registry.Add("Phone", "generic", true);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Base64Url.DecodeFromChars(token)));
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(PathOf));
        var entry = doc.RootElement.GetProperty("devices")[0];
        Assert.Equal(expected, entry.GetProperty("tokenHash").GetString());
        Assert.Equal(1, doc.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(entry.GetProperty("screenMismatch").GetBoolean());
    }

    // Why: pairing must survive a restart, otherwise every start forces re-pairing.
    [Fact]
    public void Authenticate_works_after_reload_and_ids_follow_the_pattern()
    {
        var (device, token) = DeviceRegistry.Load(PathOf).Add("Phone", "generic", false);
        Assert.Matches(@"\Ad-[0-9a-f]{8}\z", device.Id);
        Assert.True(ContractValidator.IsValidId(device.Id));
        Assert.Equal(43, token.Length);
        Assert.True(ContractValidator.IsValidToken(token));
        var reloaded = DeviceRegistry.Load(PathOf);
        Assert.Equal(device, reloaded.Authenticate(token));
    }

    // Why: "revoked devices drop immediately" - after Revoke the token must be dead, on disk and in memory, and listeners told.
    [Fact]
    public void Revoke_kills_the_token_raises_event_after_persisting_and_removes_from_file()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (device, token) = registry.Add("Phone", "generic", false);
        var (other, otherToken) = registry.Add("Other", "generic", false);
        string? raised = null;
        var fileUpdatedAtEventTime = false;
        registry.Revoked += id =>
        {
            raised = id;
            fileUpdatedAtEventTime = !File.ReadAllText(PathOf).Contains(device.Id, StringComparison.Ordinal);
        };

        Assert.True(registry.Revoke(device.Id));
        Assert.Equal(device.Id, raised);
        Assert.True(fileUpdatedAtEventTime, "the file must already be updated when the event fires");
        Assert.Null(registry.Authenticate(token));
        Assert.Equal(other, registry.Authenticate(otherToken));
        Assert.Null(DeviceRegistry.Load(PathOf).Authenticate(token));
        Assert.DoesNotContain(device.Id, File.ReadAllText(PathOf));
        Assert.False(registry.Revoke(device.Id));
    }

    // Why: a rename must survive restart and must not touch authentication.
    [Fact]
    public void Rename_persists()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (device, token) = registry.Add("Old", "generic", false);
        Assert.True(registry.Rename(device.Id, "New name"));
        Assert.False(registry.Rename("d-00000000", "x"));
        var reloaded = DeviceRegistry.Load(PathOf);
        Assert.Equal("New name", reloaded.Devices.Single().Name);
        Assert.Equal("New name", reloaded.Authenticate(token)!.Name);
        Assert.Throws<ArgumentException>(() => registry.Rename(device.Id, "bad\nname"));
    }

    // Why: a malformed token must be a plain rejection, never an exception that could drop the connection handler.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\n")]
    public void Malformed_tokens_return_null(string? token)
    {
        var registry = DeviceRegistry.Load(PathOf);
        registry.Add("Phone", "generic", false);
        Assert.Null(registry.Authenticate(token));
    }

    // Why: a well-formed token of a never-paired phone must be rejected.
    [Fact]
    public void Well_formed_unknown_token_returns_null()
    {
        var registry = DeviceRegistry.Load(PathOf);
        registry.Add("Phone", "generic", false);
        Assert.Null(registry.Authenticate(Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))));
    }

    // Why: a corrupt file must stop the app loudly; silently starting empty would then overwrite and lose all pairings.
    [Theory]
    [InlineData("{ not json")]
    [InlineData("{\"schemaVersion\":2,\"devices\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":[],\"extra\":1}")]
    [InlineData("{\"schemaVersion\":1,\"devices\":[{\"id\":\"d-1\",\"name\":\"x\",\"templateId\":\"g\",\"tokenHash\":\"zz\",\"pairedAt\":\"2026-01-01T00:00:00+00:00\",\"screenMismatch\":false}]}")]
    public void Invalid_file_throws_and_is_left_untouched(string content)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(PathOf, content);
        Assert.Throws<DeviceRegistryException>(() => DeviceRegistry.Load(PathOf));
        Assert.Equal(content, File.ReadAllText(PathOf));
    }

    // Why: a first start has no file; that is an empty registry, not an error.
    [Fact]
    public void Missing_file_is_empty()
    {
        Assert.Empty(DeviceRegistry.Load(PathOf).Devices);
        Assert.False(File.Exists(PathOf));
    }

    // Why: the schema caps the device list at 64; the 65th pairing must be refused, not stored.
    [Fact]
    public void Add_throws_when_full()
    {
        var registry = DeviceRegistry.Load(PathOf);
        for (var i = 0; i < DeviceRegistry.MaxDevices; i++)
        {
            registry.Add("P" + i, "generic", false);
        }

        Assert.Throws<DeviceRegistryFullException>(() => registry.Add("one more", "generic", false));
        Assert.Equal(DeviceRegistry.MaxDevices, DeviceRegistry.Load(PathOf).Devices.Count);
    }

    // Why: names and template ids come from the network; the registry must not store what the contract forbids.
    [Fact]
    public void Add_rejects_invalid_name_and_template()
    {
        var registry = DeviceRegistry.Load(PathOf);
        Assert.Throws<ArgumentException>(() => registry.Add("", "generic", false));
        Assert.Throws<ArgumentException>(() => registry.Add("ok", "Bad Id", false));
        Assert.Empty(registry.Devices);
    }

    // Why: regenerating the CA un-pairs every phone at once. One write means a crash can never leave "half the phones revoked",
    // and the listeners (which close live sockets with 4401) must only hear about it after the file is final.
    [Fact]
    public void RevokeAll_empties_the_registry_in_one_write_and_then_raises_Revoked_for_every_device()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (first, firstToken) = registry.Add("One", "generic", false);
        var (second, secondToken) = registry.Add("Two", "generic", false);
        var raised = new List<string>();
        var fileEmptyAtFirstEvent = new List<bool>();
        registry.Revoked += id =>
        {
            raised.Add(id);
            fileEmptyAtFirstEvent.Add(DeviceRegistry.Load(PathOf).Devices.Count == 0);
        };

        Assert.Equal(2, registry.RevokeAll());

        Assert.Equal(new[] { first.Id, second.Id }.Order(), raised.Order());
        Assert.All(fileEmptyAtFirstEvent, Assert.True);
        Assert.Empty(registry.Devices);
        Assert.Null(registry.Authenticate(firstToken));
        Assert.Null(registry.Authenticate(secondToken));
        Assert.Empty(DeviceRegistry.Load(PathOf).Devices);
    }

    // Why: startup recovery runs RevokeAll again after a crash; the second run must be harmless and write nothing.
    [Fact]
    public void RevokeAll_on_an_empty_registry_writes_nothing_and_raises_nothing()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var raised = 0;
        registry.Revoked += _ => raised++;

        Assert.Equal(0, registry.RevokeAll());

        Assert.Equal(0, raised);
        Assert.False(File.Exists(PathOf));
    }

    // Why: all or nothing. A failed write must leave every device paired, or the regeneration could not be rolled back cleanly.
    [Fact]
    public void RevokeAll_that_cannot_write_keeps_every_device_and_raises_nothing()
    {
        var registry = DeviceRegistry.Load(PathOf);
        var (device, token) = registry.Add("One", "generic", false);
        var raised = 0;
        registry.Revoked += _ => raised++;
        File.Delete(PathOf);
        Directory.CreateDirectory(PathOf); // a directory in the file's place: the atomic swap cannot succeed

        Assert.ThrowsAny<Exception>(() => registry.RevokeAll());

        Assert.Equal(0, raised);
        Assert.Equal(device, registry.Authenticate(token));
        Assert.Single(registry.Devices);
    }
}
