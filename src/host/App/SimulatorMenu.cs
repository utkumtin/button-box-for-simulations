using ButtonBox.Core;
using ButtonBox.Simulator;

namespace ButtonBox.App;

/// <summary>
/// The tray's "Simulator" submenu (decisions §3): checkboxes for the scenarios and the last few outputs the Simulator received.
/// Exists in Release builds too, because device tests run the Release exe. UI thread only: the clicks and the menu opening
/// arrive there, and the Simulator's properties are plain locked reads and writes.
/// </summary>
public sealed class SimulatorMenu
{
    public const int LastOutputCount = 5;

    private const int FixedItems = 4; // three checkboxes and a separator; the received outputs follow

    private readonly ScsSimulator _simulator;
    private readonly ToolStripMenuItem _telemetry = new("Telemetry") { CheckOnClick = true };
    private readonly ToolStripMenuItem _listening = new("Game listening") { CheckOnClick = true };
    private readonly ToolStripMenuItem _neverStarts = new("Engine never starts") { CheckOnClick = true };

    public SimulatorMenu(ScsSimulator simulator)
    {
        ArgumentNullException.ThrowIfNull(simulator);
        _simulator = simulator;
        _telemetry.Click += (_, _) => _simulator.TelemetryAvailable = _telemetry.Checked;
        _listening.Click += (_, _) => _simulator.GameListening = _listening.Checked;
        _neverStarts.Click += (_, _) => _simulator.EngineNeverStarts = _neverStarts.Checked;

        Item = new ToolStripMenuItem("Simulator");
        Item.DropDownItems.AddRange([_telemetry, _listening, _neverStarts, new ToolStripSeparator()]);
        Item.DropDownOpening += (_, _) => Refresh();
        Refresh();
    }

    public ToolStripMenuItem Item { get; }

    /// <summary>Re-reads the scenario flags and the last outputs. Called when the menu opens.</summary>
    public void Refresh()
    {
        _telemetry.Checked = _simulator.TelemetryAvailable;
        _listening.Checked = _simulator.GameListening;
        _neverStarts.Checked = _simulator.EngineNeverStarts;

        var items = Item.DropDownItems;
        while (items.Count > FixedItems)
        {
            var old = items[FixedItems];
            items.RemoveAt(FixedItems);
            old.Dispose();
        }

        var received = _simulator.Received;
        if (received.Count == 0)
        {
            items.Add(new ToolStripMenuItem("No outputs yet") { Enabled = false });
            return;
        }

        foreach (var output in received.Skip(Math.Max(0, received.Count - LastOutputCount)))
        {
            items.Add(new ToolStripMenuItem(Describe(output)) { Enabled = false });
        }
    }

    /// <summary>For example <c>Press lighton</c>.</summary>
    public static string Describe(ReceivedOutput received)
    {
        ArgumentNullException.ThrowIfNull(received);
        var what = received.Output switch
        {
            ScsInput scs => scs.Name,
            VJoyButton vjoy => "button " + vjoy.Number,
            var other => other.GetType().Name,
        };
        return received.Kind + " " + what;
    }
}
