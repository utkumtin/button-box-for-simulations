using System.Text.Json;
using ButtonBox.App.Lan;
using ButtonBox.App.Profiles;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: the first launch is the only moment the app creates profiles by itself. It must leave a working, editable starter, never
// seed twice (the user may have deleted it on purpose) and never delete anything when it fails half-way.
public sealed class FirstRunTests : IDisposable
{
    private readonly List<ProfileKit> _kits = [];

    public void Dispose()
    {
        foreach (var kit in _kits)
        {
            kit.Dispose();
        }
    }

    private ProfileKit Kit(string? catalogDir = null, bool images = false)
    {
        var kit = new ProfileKit(catalogDir, importDemoImages: images);
        _kits.Add(kit);
        return kit;
    }

    private static IReadOnlyList<string> Run(ProfileKit kit, ProfileStore store) =>
        FirstRun.Run(kit.DataDir, kit.Assets, store, DemoContent.ReadSeed());

    [Fact]
    public void A_fresh_data_folder_gets_one_editable_starter_profile_its_three_images_and_it_is_active()
    {
        var kit = Kit();
        var store = kit.NewStore();

        var problems = Run(kit, store);

        Assert.Empty(problems);
        var summary = Assert.Single(store.List().Profiles);
        Assert.Equal("ETS2 starter", summary.Name);
        Assert.Equal(Game.Ets2, summary.Game);
        Assert.Matches(@"\Aets2-starter-[0-9a-f]{4}\z", summary.Id);
        Assert.Equal(summary.Id, store.ActiveId);
        Assert.Equal(3, kit.Assets.List().Count);
        // A normal profile, not a read-only special: saving it works.
        var profile = store.Get(summary.Id).Value!;
        profile.Name = "My trucks";
        Assert.True(store.Save(summary.Id, profile).Ok);
        // The same layouts as the Phase 3 demo.
        Assert.Equal(
            JsonSerializer.Serialize(DemoContent.ReadSeed().Profile.Layouts, ContractJson.Options),
            JsonSerializer.Serialize(profile.Layouts, ContractJson.Options));
    }

    [Fact]
    public void The_seed_is_published_to_the_phones_when_the_app_publishes_the_active_engine()
    {
        var kit = Kit();
        var store = kit.NewStore();
        Run(kit, store);

        kit.Session.Publish(store.CreateActiveEngine(), keepSwitchPositions: false);

        Assert.NotNull(kit.Session.Engine?.ForTemplate(ProfileKit.Template));
    }

    [Fact]
    public void A_second_start_does_not_seed_again()
    {
        var kit = Kit();
        Run(kit, kit.NewStore());
        var secondStore = kit.NewStore();

        var problems = Run(kit, secondStore);

        Assert.Empty(problems);
        Assert.Single(secondStore.List().Profiles);
        Assert.Equal(3, kit.Assets.List().Count);
    }

    [Fact]
    public void A_profile_the_user_deleted_does_not_come_back_on_the_next_start()
    {
        // The folder exists, so the seed is over: deleting the starter is a choice, not a fault.
        var kit = Kit();
        var store = kit.NewStore();
        Run(kit, store);
        var other = store.Create("Other", Game.Ets2).Value!;
        Assert.True(store.Activate(other.Id).Ok);
        Assert.True(store.Delete(store.List().Profiles.Single(p => p.Name == "ETS2 starter").Id).Ok);

        Run(kit, kit.NewStore());

        Assert.Equal(["Other"], kit.NewStore().List().Profiles.Select(p => p.Name));
    }

    [Fact]
    public void An_existing_empty_profiles_folder_is_not_seeded()
    {
        var kit = Kit();
        Directory.CreateDirectory(kit.ProfilesDir);
        var store = kit.NewStore();

        var problems = Run(kit, store);

        Assert.Empty(problems);
        Assert.Empty(store.List().Profiles);
        Assert.Null(store.ActiveId);
        Assert.Empty(kit.Assets.List());
        Assert.False(File.Exists(kit.ActiveFile));
    }

    [Fact]
    public void A_seed_that_cannot_complete_reports_it_keeps_what_it_wrote_and_is_not_retried()
    {
        // Without the ETS2 catalog the starter cannot be checked: the app starts with no active profile instead of a crash,
        // the images stay in the library, and the next start does not try again.
        var empty = Path.Combine(Path.GetTempPath(), "bb-nocat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var kit = Kit(catalogDir: empty);
            var store = kit.NewStore();

            var problems = Run(kit, store);

            Assert.NotEmpty(problems);
            Assert.Empty(store.List().Profiles);
            Assert.Null(store.ActiveId);
            Assert.Equal(3, kit.Assets.List().Count);
            Assert.True(Directory.Exists(kit.ProfilesDir));
            Assert.Empty(Run(kit, kit.NewStore()));
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }
}
