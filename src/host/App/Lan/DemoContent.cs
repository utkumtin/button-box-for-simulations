using System.Reflection;
using System.Text.Json;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;

namespace ButtonBox.App.Lan;

/// <summary>The Phase 3 demo as the source of the first-run seed (decisions §6): the profile and its three PNG images.</summary>
public sealed record DemoSeed(Profile Profile, IReadOnlyList<byte[]> Images);

/// <summary>
/// The Phase 3 demo (decisions §3, §7): a profile and its images, embedded in the exe, and the ETS2 catalog from the shipped
/// <c>catalog</c> folder. A damaged bundled file is a bug, so loading throws; the app never runs with an empty profile.
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

    /// <summary>The demo with the catalog next to the executable (<c>catalog\ets2.json</c>), as the tests and the E2E host run it.</summary>
    /// <exception cref="InvalidOperationException">A bundled file or the shipped catalog is missing or not usable.</exception>
    public static DemoContent Load() => Load(Path.Combine(AppContext.BaseDirectory, "catalog"));

    /// <exception cref="InvalidOperationException">A bundled file or the shipped catalog is missing or not usable.</exception>
    public static DemoContent Load(string shippedCatalogsDir)
    {
        var seed = ReadSeed();
        var catalogs = new CatalogStore(shippedCatalogsDir);
        if (!catalogs.TryGet(Game.Ets2, out var catalog))
        {
            throw new InvalidOperationException("The shipped ETS2 catalog is missing or not usable.");
        }

        var engine = new ProfileEngine(seed.Profile, catalog); // throws InvalidOperationException when the pair does not check out
        return new DemoContent(seed.Profile, catalog, engine, new AssetStore(seed.Images));
    }

    /// <exception cref="InvalidOperationException">A bundled file is missing or not usable.</exception>
    public static DemoSeed ReadSeed()
    {
        var assembly = typeof(DemoContent).Assembly;
        var profile = ReadJson<Profile>(assembly, "demo/demo.profile.json");
        return new DemoSeed(profile, [.. ImageResources.Select(name => ReadBytes(assembly, name))]);
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
