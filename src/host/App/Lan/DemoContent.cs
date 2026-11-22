using System.Reflection;
using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;

namespace ButtonBox.App.Lan;

/// <summary>
/// The Phase 3 demo (decisions §3, §7): a bundled, read-only profile, its ETS2 catalog and its images, embedded in the
/// exe. A damaged bundled file is a bug, so loading throws; the app never runs with an empty profile.
/// </summary>
public sealed class DemoContent
{
    private static readonly string[] ImageResources = ["demo/rotary-base.png", "demo/rotary-knob.png", "demo/engine-body.png"];

    private DemoContent(Profile profile, Catalog catalog, ProfileEngine engine, AssetStore assets)
    {
        Profile = profile;
        Catalog = catalog;
        Engine = engine;
        Assets = assets;
    }

    public Profile Profile { get; }

    public Catalog Catalog { get; }

    public ProfileEngine Engine { get; }

    public AssetStore Assets { get; }

    /// <exception cref="InvalidOperationException">A bundled file is missing or not usable.</exception>
    public static DemoContent Load()
    {
        var assembly = typeof(DemoContent).Assembly;
        var profile = ReadJson<Profile>(assembly, "demo/demo.profile.json");
        var catalog = ReadJson<Catalog>(assembly, "demo/ets2-demo.catalog.json");
        var engine = new ProfileEngine(profile, catalog); // throws InvalidOperationException when the pair does not check out
        var assets = new AssetStore(ImageResources.Select(name => ReadBytes(assembly, name)));
        return new DemoContent(profile, catalog, engine, assets);
    }

    private static byte[] ReadBytes(Assembly assembly, string name)
    {
        using var stream = assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Bundled demo file '{name}' is missing.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static T ReadJson<T>(Assembly assembly, string name)
        where T : class
    {
        var bytes = ReadBytes(assembly, name);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, ContractJson.Options)
                ?? throw new InvalidOperationException($"Bundled demo file '{name}' is empty.");
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException($"Bundled demo file '{name}' is not valid.", e);
        }
    }
}
