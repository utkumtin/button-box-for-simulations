using System.Text.RegularExpressions;
using ButtonBox.Core.Contracts;
using Xunit;

namespace ButtonBox.Core.Tests;

// Why (decisions §4 "Plugin and catalog"): the game lets the plugin register inputs and channels only while it loads, so the
// plugin's compiled lists and the shipped catalog must agree. A catalog action the plugin did not register would be refused by the
// plugin (the phone button does nothing); a state field without a channel would never light. And the game accepts unknown
// semantical names silently (spike S2 #5), so a typo in the catalog would also fail without any error: every input must be one
// of the game's own mix names.
public partial class ScsPluginListsTests
{
    // Fields the widget behaviours read (decisions §5.1-§5.3); the plugin must register them even if no catalog action names them.
    private static readonly string[] BehaviourFields =
    [
        "truck.electric.enabled", "truck.engine.enabled", "truck.light.parking", "truck.light.beam.low", "truck.light.beam.high",
        "truck.wipers",
    ];

    [GeneratedRegex("""^BBS_INPUT\("([^"]*)"\)$""")]
    private static partial Regex InputLine();

    [GeneratedRegex("""^BBS_CHANNEL\("([^"]*)", (\w+)\)$""")]
    private static partial Regex ChannelLine();

    [Fact]
    public void Plugin_registers_exactly_the_catalog_inputs_in_catalog_order()
    {
        var catalogInputs = ShippedCatalogTests.Load("ets2").Actions
            .Select(a => Assert.IsType<ScsOutput>(a.Output).Input)
            .ToList();
        Assert.Equal(catalogInputs, PluginInputs());
    }

    [Fact]
    public void Every_catalog_input_is_a_game_mix_name()
    {
        var mixes = File.ReadAllLines(Path.Combine(ShippedCatalogTests.RepoRoot(), "src", "host", "Core.Tests", "Fixtures", "scs-semantical-inputs.txt"))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(297, mixes.Count);

        foreach (var game in new[] { "ets2", "ats" })
        {
            foreach (var action in ShippedCatalogTests.Load(game).Actions)
            {
                var input = Assert.IsType<ScsOutput>(action.Output).Input;
                Assert.True(mixes.Contains(input), $"{game}: '{action.Id}' uses '{input}', which is not a semantical mix of the game");
            }
        }
    }

    [Fact]
    public void Plugin_registers_every_state_field_the_catalog_and_behaviours_read()
    {
        var channels = PluginChannels();
        Assert.Equal(channels.Count, channels.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
        var names = channels.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        var needed = ShippedCatalogTests.Load("ets2").Actions
            .Where(a => a.StateField is not null)
            .Select(a => a.StateField!)
            .Concat(BehaviourFields);
        foreach (var field in needed)
        {
            Assert.True(names.Contains(field), $"'{field}' is read by the host but not registered by the plugin (channels.inc)");
        }
    }

    // A channel name the SDK does not define would register as "not found" and silently stay dark.
    [Fact]
    public void Every_plugin_channel_is_an_sdk_channel_with_a_known_mapping()
    {
        var header = File.ReadAllText(Path.Combine(ShippedCatalogTests.RepoRoot(), "src", "plugins", "scs", "third_party", "scs_sdk_1_16", "include", "common", "scssdk_telemetry_truck_common_channels.h"));
        foreach (var (name, mapping) in PluginChannels())
        {
            var at = header.IndexOf($"\"{name}\"", StringComparison.Ordinal);
            Assert.True(at >= 0, $"'{name}' is not an SDK channel");

            // The SDK documents each channel's type in the comment right above its #define. A mapping registered with another type
            // is refused by the game and the channel stays dark, like an unknown name.
            var typeAt = header.LastIndexOf("Type: ", at, StringComparison.Ordinal);
            var sdkType = header[(typeAt + "Type: ".Length)..header.IndexOf('\n', typeAt)].Trim();
            var expected = mapping switch
            {
                "bool" => "bool",
                "u32" => "u32",
                "positive" => "float",
                _ => null,
            };
            Assert.True(expected == sdkType, $"'{name}' is '{sdkType}' in the SDK but mapped as '{mapping}'");
        }
    }

    private static List<string> PluginInputs() =>
        PluginLines("inputs.inc")
            .Select(l => InputLine().Match(l))
            .Select(m => Assert.IsType<Match>(m.Success ? m : null).Groups[1].Value)
            .ToList();

    private static List<(string Name, string Mapping)> PluginChannels() =>
        PluginLines("channels.inc")
            .Select(l => ChannelLine().Match(l))
            .Select(m => Assert.IsType<Match>(m.Success ? m : null))
            .Select(m => (m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    // Every non-comment, non-blank line must be an entry: a malformed line fails the match above instead of being skipped.
    private static IEnumerable<string> PluginLines(string file) =>
        File.ReadAllLines(Path.Combine(ShippedCatalogTests.RepoRoot(), "src", "plugins", "scs", "src", file))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal));
}
