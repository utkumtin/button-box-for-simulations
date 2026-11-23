using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.Core.Contracts;

namespace ButtonBox.App.Profiles;

/// <summary>
/// The first-run seed (decisions §6): when the data folder has no <c>profiles</c> folder yet, the Phase 3 demo becomes a normal,
/// editable ETS2 profile and the active one, and its images go into the asset library. Nothing is ever deleted; a seed that fails
/// half-way leaves what it wrote and the app starts with no active profile. The next start does not seed again.
/// </summary>
public static class FirstRun
{
    public const string SeedName = "ETS2 starter";

    /// <summary>Problems are fixed sentences; empty when the seed is not needed or succeeded. Never throws for I/O failures.</summary>
    public static IReadOnlyList<string> Run(string dataDir, AssetLibrary assets, ProfileStore profiles, DemoSeed seed)
    {
        ArgumentException.ThrowIfNullOrEmpty(dataDir);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(seed);

        var dir = Path.Combine(dataDir, "profiles");
        if (Directory.Exists(dir))
        {
            return [];
        }

        try
        {
            Directory.CreateDirectory(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return ["The first-run profile folder could not be created."];
        }

        foreach (var image in seed.Images)
        {
            if (assets.Import(image).Problem is { } problem)
            {
                return ["The first-run images could not be imported: " + problem];
            }
        }

        seed.Profile.Name = SeedName;
        var imported = profiles.Import(JsonSerializer.SerializeToUtf8Bytes(seed.Profile, ContractJson.Options));
        if (imported.Value is not { } profile)
        {
            return [$"The first-run profile '{SeedName}' could not be created.", .. imported.Problems];
        }

        var activated = profiles.Activate(profile.Id);
        return activated.Ok ? [] : [$"The first-run profile '{SeedName}' could not be activated."];
    }
}
