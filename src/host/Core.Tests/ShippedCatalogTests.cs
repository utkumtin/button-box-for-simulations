using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;

namespace ButtonBox.Core.Tests;

// Why: saved profiles refer to catalog action ids, so a renamed or removed id silently breaks every user's layout
// (decisions §4: later phases may add actions, never rename or renumber one). The pinned lists below make such a change loud.
public class ShippedCatalogTests
{
    // Verified in spike S2; identical in ETS2 and ATS (decisions §4.1). Append only.
    private static readonly (string Id, string Input)[] ScsActions =
    [
        ("ignition-on", "ignitionon"), ("ignition-off", "ignitionoff"), ("starter", "ignitionstrt"),
        ("light-off", "lightoff"), ("light-park", "lightpark"), ("light-on", "lighton"), ("high-beam", "hblight"),
        ("wipers-off", "wipers0"), ("wipers-auto", "wipers1"), ("wipers-low", "wipers2"), ("wipers-fast", "wipers3"),
        ("beacon", "beacon"), ("hazard", "flasher4way"), ("parking-brake", "parkingbrake"),
        ("retarder-down", "retarderdown"), ("retarder-up", "retarderup"),
        // Phase 5 (decisions §4 "Phase 5 catalog additions").
        ("cruise-control", "cruiectrl"), ("cruise-resume", "cruiectrlres"), ("cruise-up", "cruiectrlinc"), ("cruise-down", "cruiectrldec"),
        ("diff-lock", "diflock"), ("lift-axle", "liftaxle"), ("trailer-lift-axle", "liftaxlet"),
        ("engine-brake", "engbraketog"), ("engine-brake-up", "engbrakeup"), ("engine-brake-down", "engbrakedwn"),
        ("indicator-left", "lblinker"), ("indicator-right", "rblinker"),
    ];

    [Theory]
    [InlineData("ets2", Game.Ets2)]
    [InlineData("ats", Game.Ats)]
    public void Scs_catalog_keeps_every_pinned_action(string file, Game game)
    {
        var catalog = Load(file);
        Assert.Equal(game, catalog.Game);
        var byId = catalog.Actions.ToDictionary(a => a.Id);
        Assert.Equal(catalog.Actions.Count, byId.Count);
        foreach (var (id, input) in ScsActions)
        {
            Assert.True(byId.TryGetValue(id, out var action), $"action '{id}' was removed or renamed");
            Assert.Equal(input, Assert.IsType<ScsOutput>(action.Output).Input);
        }
    }

    [Fact]
    public void Ets2_and_ats_offer_the_same_actions()
    {
        var ets2 = JsonSerializer.Serialize(Load("ets2").Actions, ContractJson.Options);
        var ats = JsonSerializer.Serialize(Load("ats").Actions, ContractJson.Options);
        Assert.Equal(ets2, ats);
    }

    // The first-run seed (decisions §6) is the demo profile; it must stay usable with the shipped catalog.
    [Fact]
    public void Demo_profile_checks_out_against_the_shipped_ets2_catalog()
    {
        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(RepoRoot(), "src", "host", "App", "Demo", "demo.profile.json")), ContractJson.Options)!;
        Assert.Empty(ProfileCheck.Problems(profile, Load("ets2")));
    }

    internal static Catalog Load(string name) =>
        JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(RepoRoot(), "catalog", name + ".json")), ContractJson.Options)!;

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            // The schemas folder marks the repo root; build outputs also carry a catalog folder.
            if (File.Exists(Path.Combine(dir.FullName, "schemas", "catalog.schema.json")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("schemas/catalog.schema.json not found above " + AppContext.BaseDirectory);
    }
}
