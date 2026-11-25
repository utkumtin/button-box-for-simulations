using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ButtonBox.App.Lan;
using ButtonBox.App.Pairing;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using ButtonBox.Simulator;
using Xunit;

namespace ButtonBox.App.Tests;

/// <summary>
/// A throwaway data folder with the real shipped catalogs and templates, the library, the gate and a game session. The demo profile
/// is the one valid profile every test starts from: each negative test changes exactly one thing in it.
/// </summary>
internal sealed class ProfileKit : IDisposable
{
    public const string Template = "iphone-14-pro";

    private readonly ScsSimulator _simulator = new();

    public ProfileKit(string? catalogDir = null, bool importDemoImages = true)
    {
        Root = Path.Combine(Path.GetTempPath(), "bb-prof-" + Guid.NewGuid().ToString("N"));
        DataDir = Path.Combine(Root, "data");
        Directory.CreateDirectory(DataDir);
        Catalogs = new CatalogStore(catalogDir ?? Path.Combine(AppContext.BaseDirectory, "catalog"));
        Templates = new TemplateStore(Path.Combine(AppContext.BaseDirectory, "templates"), Path.Combine(Root, "generic"));
        Assets = new AssetLibrary(Path.Combine(DataDir, "assets"));
        Gate = new ProfileGate(Catalogs, Templates, Assets);
        Session = new GameSession(null, _simulator, _simulator);
        if (importDemoImages)
        {
            ImportDemoImages();
        }
    }

    public string Root { get; }

    public string DataDir { get; }

    public string ProfilesDir => Path.Combine(DataDir, "profiles");

    public string ActiveFile => Path.Combine(DataDir, ProfileStore.ActiveFileName);

    public CatalogStore Catalogs { get; }

    public TemplateStore Templates { get; }

    public AssetLibrary Assets { get; }

    public ProfileGate Gate { get; }

    public GameSession Session { get; }

    public ProfileStore NewStore(Action<string, byte[]>? write = null) =>
        new(DataDir, Gate, Catalogs, Session, write ?? ((path, bytes) => ButtonBox.Core.AtomicFile.WriteAllBytes(path, bytes)));

    public string ProfileFile(string id) => Path.Combine(ProfilesDir, id + ".json");

    public void ImportDemoImages()
    {
        foreach (var image in DemoContent.ReadSeed().Images)
        {
            Assert.Null(Assets.Import(image).Problem);
        }
    }

    public static Profile Demo() => DemoContent.ReadSeed().Profile;

    public static byte[] Bytes(Profile profile) => JsonSerializer.SerializeToUtf8Bytes(profile, ContractJson.Options);

    /// <summary>The demo profile as JSON after <paramref name="edit"/>: one change per test, so a rejection cannot come from anything else.</summary>
    public static byte[] Mutate(Action<JsonObject> edit)
    {
        var root = (JsonObject)JsonNode.Parse(Bytes(Demo()))!;
        edit(root);
        return Encoding.UTF8.GetBytes(root.ToJsonString());
    }

    public static JsonObject Layout(JsonObject profile) => profile["layouts"]![0]!.AsObject();

    public static JsonObject Widget(JsonObject profile, string id) =>
        Layout(profile)["widgets"]!.AsArray().Select(n => n!.AsObject()).Single(w => (string)w["id"]! == id);

    /// <summary>The Phase 3 lights knob: three positions and no behaviour, for tests of the generic switch plumbing (commanded positions that stay put).</summary>
    public static void PlainLights(JsonObject profile)
    {
        var lights = Widget(profile, "lights");
        lights.Remove("behavior");
        lights["positions"]!.AsArray().RemoveAt(3);
    }

    /// <summary>The same for a typed profile.</summary>
    public static Profile WithPlainLights(Profile profile)
    {
        var lights = profile.Layouts.SelectMany(l => l.Widgets).OfType<SwitchWidget>().Single(w => w.Id == "lights");
        lights.Behavior = null;
        lights.Positions = [.. lights.Positions.Take(3)];
        return profile;
    }

    public static Profile Parse(byte[] json) => JsonSerializer.Deserialize<Profile>(json, ContractJson.Options)!;

    public static string IdOf(string path) => Path.GetFileNameWithoutExtension(path);

    public void Dispose()
    {
        Session.DisposeAsync().AsTask().GetAwaiter().GetResult();
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
            // A temp folder.
        }
    }
}
