using System.Text;
using System.Text.Json.Nodes;
using ButtonBox.App.Profiles;
using Xunit;

namespace ButtonBox.App.Tests;

// Why: the gate is the only thing between an untrusted file (a download, a hand edit, a bug in the editor) and the phones.
// Every negative case starts from the demo profile, which passes, and changes exactly one thing, so a rejection can only come
// from the step under test.
public sealed class ProfileGateTests : IDisposable
{
    private const string MissingImage = "0000000000000000000000000000000000000000000000000000000000000000";

    private readonly ProfileKit _kit = new();

    public void Dispose() => _kit.Dispose();

    // The whole pipeline as the store runs it for a file: parse, then check.
    private IReadOnlyList<string> Run(byte[] json)
    {
        var parsed = _kit.Gate.Parse(json);
        return parsed.Profile is { } profile ? _kit.Gate.Check(profile).Problems : parsed.Problems;
    }

    private static void Clone(JsonObject profile, string widgetId, string newId, Action<JsonObject>? change = null)
    {
        var copy = ProfileKit.Widget(profile, widgetId).DeepClone().AsObject();
        copy["id"] = newId;
        change?.Invoke(copy);
        ProfileKit.Layout(profile)["widgets"]!.AsArray().Add(copy);
    }

    [Fact]
    public void The_demo_profile_passes_the_whole_gate()
    {
        // Touching widgets share an edge but do not overlap, and every demo widget meets the minimum size on the iPhone 14 Pro.
        Assert.Empty(Run(ProfileKit.Bytes(ProfileKit.Demo())));
    }

    [Fact]
    public void A_file_of_exactly_1_MiB_is_read_and_one_byte_more_is_refused_before_any_parsing()
    {
        var valid = ProfileKit.Bytes(ProfileKit.Demo());
        byte[] Padded(int length) => [.. valid, .. Enumerable.Repeat((byte)' ', length - valid.Length)];

        Assert.Empty(Run(Padded(ProfileGate.MaxBytes)));

        var problems = Run(Padded(ProfileGate.MaxBytes + 1));
        Assert.Contains("1 MiB", Assert.Single(problems));
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public void Nesting_deeper_than_32_levels_is_refused_with_its_own_reason(int levels, bool tooDeep)
    {
        // No profile field nests this deep, so a deep document is hostile; the depth sentence proves the depth step fired and
        // not the type or unknown-field error that a deep document would also cause later.
        var json = Encoding.UTF8.GetBytes(new string('[', levels) + new string(']', levels));

        var problems = Run(json);

        Assert.Equal(tooDeep, problems.Any(p => p.Contains("nested", StringComparison.Ordinal)));
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void Text_that_is_not_json_or_not_a_profile_is_refused()
    {
        Assert.NotEmpty(Run("not json"u8.ToArray()));
        Assert.NotEmpty(Run("null"u8.ToArray()));
        Assert.NotEmpty(Run("{}"u8.ToArray()));
    }

    [Fact]
    public void An_unknown_field_is_refused()
    {
        // Silently ignoring a field would let a typo or a future setting pass as if it were applied.
        var problems = Run(ProfileKit.Mutate(root => root["extra"] = 1));

        Assert.NotEmpty(problems);
    }

    [Fact]
    public void The_bounds_check_runs_first_and_later_steps_do_not_run_on_a_profile_it_refused()
    {
        var result = _kit.Gate.Check(ProfileKit.Parse(ProfileKit.Mutate(root => ProfileKit.Widget(root, "engine")["cell"]!["col"] = -1)));

        Assert.False(result.StructureValid);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public void A_game_without_a_shipped_catalog_is_refused()
    {
        // Phase 4 ships ETS2 and ATS only (decisions §4): a profile for another game cannot be checked against actions.
        var problems = Run(ProfileKit.Mutate(root => root["game"] = "ac1"));

        Assert.Contains(problems, p => p.Contains("catalog", StringComparison.Ordinal));
    }

    [Fact]
    public void An_action_that_is_not_in_the_catalog_is_refused()
    {
        var problems = Run(ProfileKit.Mutate(root => ProfileKit.Widget(root, "beacon")["action"] = "no-such-action"));

        Assert.Contains(problems, p => p.Contains("no-such-action", StringComparison.Ordinal));
    }

    [Fact]
    public void A_layout_for_an_unknown_template_is_refused()
    {
        var problems = Run(ProfileKit.Mutate(root => ProfileKit.Layout(root)["templateId"] = "no-such-phone"));

        Assert.Contains(problems, p => p.Contains("no-such-phone", StringComparison.Ordinal) && p.Contains("template", StringComparison.Ordinal));
    }

    [Fact]
    public void A_widget_below_the_minimum_size_on_the_template_is_refused()
    {
        // The iPhone 14 Pro is 734 pt wide inside its safe area: on 24 columns one cell is 30.6 pt, below the 44 pt a finger needs.
        var problems = Run(ProfileKit.Mutate(root =>
        {
            ProfileKit.Layout(root)["grid"]!["columns"] = 24;
            ProfileKit.Widget(root, "beacon")["cell"]!["colSpan"] = 1;
        }));

        Assert.Contains(problems, p => p.Contains("'beacon'", StringComparison.Ordinal) && p.Contains("minimum", StringComparison.Ordinal));
    }

    [Fact]
    public void Two_widgets_on_the_same_cells_are_refused()
    {
        // A new id, so that the duplicate-id rule of the profile check cannot be what rejects it.
        var problems = Run(ProfileKit.Mutate(root => Clone(root, "beacon", "beacon-twin")));

        Assert.Contains(problems, p => p.Contains("'beacon'", StringComparison.Ordinal) && p.Contains("'beacon-twin'", StringComparison.Ordinal) && p.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void Widgets_that_partly_cover_each_other_are_refused_and_widgets_that_only_touch_are_not()
    {
        // The demo neighbours touch at an edge (columns 0-1 and 2-3). One cell of overlap is already an overlap.
        var partly = Run(ProfileKit.Mutate(root => Clone(root, "beacon", "shifted", w => w["cell"]!["col"] = 1)));

        Assert.Contains(partly, p => p.Contains("overlap", StringComparison.Ordinal));
        Assert.DoesNotContain(Run(ProfileKit.Bytes(ProfileKit.Demo())), p => p.Contains("overlap", StringComparison.Ordinal));
    }

    [Fact]
    public void An_image_that_is_not_in_the_library_is_refused_and_named()
    {
        var problems = Run(ProfileKit.Mutate(root => ProfileKit.Widget(root, "engine")["look"]!["image"] = MissingImage));

        Assert.Contains(problems, p => p.Contains(MissingImage, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_missing_image_is_listed_including_rotary_layers_and_lit_images()
    {
        var lit = new string('1', 64);
        var layer = new string('2', 64);
        var problems = Run(ProfileKit.Mutate(root =>
        {
            ProfileKit.Widget(root, "engine")["look"]!["on"] = lit;
            ProfileKit.Widget(root, "lights")["rotary"]!["knob"]!["asset"] = layer;
        }));

        Assert.Contains(problems, p => p.Contains(lit, StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains(layer, StringComparison.Ordinal));
    }

    [Fact]
    public void Asset_ids_of_a_profile_are_listed_once_and_sorted()
    {
        var ids = ProfileGate.AssetIds(ProfileKit.Demo());

        Assert.Equal(3, ids.Count);
        Assert.Equal(ids.Order(StringComparer.Ordinal), ids);
    }

    [Fact]
    public void A_problem_list_is_capped_at_100_sentences_of_at_most_300_characters()
    {
        // A hostile profile can have hundreds of faults; the user gets a bounded list.
        var problems = Run(ProfileKit.Mutate(root =>
        {
            for (var i = 0; i < 150; i++)
            {
                Clone(root, "beacon", "twin-" + i);
            }
        }));

        Assert.Equal(100, problems.Count);
        Assert.All(problems, p => Assert.True(p.Length <= 300));
    }
}
