using System.Text.Json;
using ButtonBox.Core.Contracts;
using ButtonBox.Core.Engine;
using Xunit;
using static ButtonBox.Core.Tests.Engine.EngineFixture;

namespace ButtonBox.Core.Tests.Engine;

// A bad profile must be refused when it is loaded, so the gesture path never has to guess at runtime
// what a dangling or unsupported setting meant.
public class ProfileCheckTests
{
    private static void AssertOneProblem(Profile profile, Catalog catalog, string contains)
    {
        var problems = ProfileCheck.Problems(profile, catalog);
        Assert.Contains(problems, p => p.Contains(contains, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => new ProfileEngine(profile, catalog));
    }

    [Fact]
    public void Valid_profile_has_no_problems()
    {
        Assert.Empty(ProfileCheck.Problems(Profile(), Catalog()));
    }

    [Fact]
    public void Catalog_for_another_game_is_refused()
    {
        // A profile belongs to one game; mixing catalogs would send the wrong game's outputs.
        AssertOneProblem(Profile(Game.Ets2), Catalog(Game.Ats), "Catalog is for game");
    }

    [Theory]
    [InlineData(Game.Ets2)]
    [InlineData(Game.Ats)]
    public void Scs_games_refuse_vjoy_outputs(Game game)
    {
        var catalog = Catalog(game);
        ((List<CatalogAction>)catalog.Actions)[0].Output = new VjoyOutput { Button = 1 };
        AssertOneProblem(Profile(game), catalog, "must use an 'scs' output");
    }

    [Theory]
    [InlineData(Game.Ac1)]
    [InlineData(Game.Acc)]
    [InlineData(Game.Acevo)]
    public void Vjoy_games_refuse_scs_outputs(Game game)
    {
        var catalog = Catalog(game);
        ((List<CatalogAction>)catalog.Actions)[0].Output = new ScsOutput { Input = "x" };
        AssertOneProblem(Profile(game), catalog, "must use a 'vjoy' output");
    }

    [Fact]
    public void Duplicate_catalog_action_ids_are_refused()
    {
        AssertOneProblem(Profile(), Catalog(Game.Ets2, "horn", "horn", "wiper-down", "wiper-up", "lights-off", "lights-park", "lights-low", "ign", "starter"),
            "defined more than once");
    }

    [Fact]
    public void Duplicate_template_ids_are_refused()
    {
        // ForTemplate must be unambiguous: two layouts for one template would make the choice arbitrary.
        AssertOneProblem(Profile(Game.Ets2, Layout(), Layout()), Catalog(), "more than one layout");
    }

    [Fact]
    public void Duplicate_widget_ids_in_a_layout_are_refused()
    {
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, Toggle("a"), Toggle("a"))), Catalog(), "used more than once");
    }

    [Fact]
    public void Same_widget_id_in_two_layouts_is_fine()
    {
        var profile = Profile(Game.Ets2, Layout("t1", 4, 1, Toggle("a")), Layout("t2", 4, 1, Toggle("a")));
        Assert.Empty(ProfileCheck.Problems(profile, Catalog()));
    }

    [Fact]
    public void Missing_catalog_actions_are_refused_for_every_widget_type()
    {
        foreach (var widget in new Widget[]
        {
            Toggle(action: "nope"),
            new EncoderWidget { Id = "e", Cell = Cell(), Minus = "nope", Plus = "horn" },
            new EncoderWidget { Id = "e", Cell = Cell(), Minus = "horn", Plus = "nope" },
            Switch("s", "horn", "nope"),
            Dual(tap: "nope", hold: null),
            Dual(tap: null, hold: "nope"),
        })
        {
            AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, widget)), Catalog(), "is not in the catalog");
        }
    }

    [Theory]
    [InlineData(3, 0, 2, 1)] // col + colSpan = 5 > 4
    [InlineData(0, 0, 1, 2)] // row + rowSpan = 2 > 1
    public void Cell_outside_the_grid_is_refused(int col, int row, int colSpan, int rowSpan)
    {
        var widget = Toggle();
        widget.Cell = new GridCell { Col = col, Row = row, ColSpan = colSpan, RowSpan = rowSpan };
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, widget)), Catalog(), "outside the 4x1 grid");
    }

    [Fact]
    public void Cell_touching_the_grid_edge_is_fine()
    {
        var widget = Toggle();
        widget.Cell = new GridCell { Col = 2, Row = 0, ColSpan = 2, RowSpan = 1 };
        Assert.Empty(ProfileCheck.Problems(Profile(Game.Ets2, Layout(Template, 4, 1, widget)), Catalog()));
    }

    [Fact]
    public void Switch_position_without_action_is_refused()
    {
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, Switch("s", "lights-off", null))), Catalog(), "has no action");
    }

    [Fact]
    public void Switch_with_cycle_is_refused_until_press_count_switching_exists()
    {
        var sw = Switch();
        sw.Cycle = "lights-off";
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, sw)), Catalog(), "'cycle'");
    }

    [Fact]
    public void Dual_action_with_neither_tap_nor_hold_is_refused()
    {
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, Dual(tap: null, hold: null))), Catalog(), "neither tap nor hold");
    }

    [Fact]
    public void Starter_takeover_is_refused_until_the_state_machine_exists()
    {
        var widget = Dual(mode: DualActionWidgetHoldMode.StarterTakeover);
        AssertOneProblem(Profile(Game.Ets2, Layout(Template, 4, 1, widget)), Catalog(), "starterTakeover");
    }

    [Fact]
    public void Demo_fixture_loads_without_problems()
    {
        // The demo profile is the first-run seed (decisions §6); it must always be accepted by the engine with the shipped catalog.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        var demo = Path.Combine(dir.FullName, "src", "host", "App", "Demo");

        var profile = JsonSerializer.Deserialize<Profile>(File.ReadAllText(Path.Combine(demo, "demo.profile.json")), ContractJson.Options);
        var catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(Path.Combine(dir.FullName, "catalog", "ets2.json")), ContractJson.Options);
        Assert.NotNull(profile);
        Assert.NotNull(catalog);

        Assert.Empty(ProfileCheck.Problems(profile, catalog));
        var engine = new ProfileEngine(profile, catalog);
        Assert.NotNull(engine.ForTemplate(profile.Layouts[0].TemplateId));
    }
}
