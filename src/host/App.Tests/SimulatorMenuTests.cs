using System.Windows.Forms;
using ButtonBox.Core;
using ButtonBox.Simulator;
using Xunit;

namespace ButtonBox.App.Tests;

public sealed class SimulatorMenuTests
{
    [Fact]
    public void The_three_checkboxes_drive_the_simulator_scenarios()
    {
        // The tray is how a tester pulls the rug from under the phone (no telemetry, game paused, engine never starts).
        var sim = new ScsSimulator();
        var menu = new SimulatorMenu(sim);
        var items = menu.Item.DropDownItems.OfType<ToolStripMenuItem>().Take(3).ToList();
        Assert.Equal(["Telemetry", "Game listening", "Engine never starts"], items.Select(i => i.Text));
        Assert.Equal([true, true, false], items.Select(i => i.Checked));

        items[0].PerformClick();
        items[1].PerformClick();
        items[2].PerformClick();

        Assert.False(sim.TelemetryAvailable);
        Assert.False(sim.GameListening);
        Assert.True(sim.EngineNeverStarts);

        // Opening the menu shows the real state again, e.g. after a change from somewhere else.
        sim.GameListening = true;
        menu.Refresh();
        Assert.True(items[1].Checked);
    }

    [Fact]
    public async Task The_last_five_outputs_are_listed_as_disabled_items_when_the_menu_refreshes()
    {
        var sim = new ScsSimulator();
        var menu = new SimulatorMenu(sim);
        Assert.Equal("No outputs yet", menu.Item.DropDownItems[4].Text);

        foreach (var name in new[] { "lightoff", "lightpark", "lighton", "hblight", "beacon", "wipers0", "wipers1" })
        {
            await sim.PressAsync(new ScsInput(name), TestContext.Current.CancellationToken);
        }

        menu.Refresh();

        var listed = menu.Item.DropDownItems.Cast<ToolStripItem>().Skip(4).ToList();
        Assert.Equal(["Press lighton", "Press hblight", "Press beacon", "Press wipers0", "Press wipers1"], listed.Select(i => i.Text));
        Assert.All(listed, i => Assert.False(i.Enabled));
    }

    [Fact]
    public void Outputs_are_described_by_kind_and_name()
    {
        Assert.Equal("Press lighton", SimulatorMenu.Describe(new ReceivedOutput(OutputKind.Press, new ScsInput("lighton"))));
        Assert.Equal("Hold button 3", SimulatorMenu.Describe(new ReceivedOutput(OutputKind.Hold, new VJoyButton(3))));
    }
}
