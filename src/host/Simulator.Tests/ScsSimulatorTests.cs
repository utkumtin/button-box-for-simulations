using ButtonBox.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.Simulator.Tests;

public class ScsSimulatorTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ScsSimulator _sim;

    public ScsSimulatorTests() => _sim = new ScsSimulator(_time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task Press(string name) => _sim.PressAsync(new ScsInput(name), Ct);

    private int Field(string name) => _sim.Current.Fields[name];

    // Why: the host's starter sequence (ignitionon, ignitionstrt) and its amber timeout rely on the measured 1.28 s.
    [Fact]
    public async Task Start_sequence_brings_engine_on_at_1_28_seconds_not_before()
    {
        await Press("ignitionon");
        await Press("ignitionstrt");

        Assert.Equal(1, Field("truck.electric.enabled"));
        _time.Advance(TimeSpan.FromSeconds(1.27));
        Assert.Equal(0, Field("truck.engine.enabled"));
        _time.Advance(TimeSpan.FromSeconds(0.01));
        Assert.Equal(1, Field("truck.engine.enabled"));
    }

    // Why: a scenario with an instant start must still notify the host, or its engine LED would never turn green.
    [Fact]
    public async Task Zero_starter_delay_starts_the_engine_at_once_and_raises_Changed()
    {
        _sim.StarterDelay = TimeSpan.Zero;
        await Press("ignitionon");
        TelemetrySnapshot? last = null;
        _sim.Changed += (_, s) => last = s;

        await Press("ignitionstrt");

        Assert.Equal(1, last?.Fields["truck.engine.enabled"]);
    }

    // Why: the amber LED must be testable, so a start that never happens has to be producible.
    [Fact]
    public async Task Never_starting_engine_stays_off_past_the_4_second_amber_timeout()
    {
        _sim.EngineNeverStarts = true;

        await Press("ignitionon");
        await Press("ignitionstrt");
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Field("truck.engine.enabled"));
    }

    // Why: a cancelled start must never fire later, or the host would see an engine the user switched off.
    [Fact]
    public async Task Ignition_off_during_cranking_cancels_the_start()
    {
        await Press("ignitionon");
        await Press("ignitionstrt");
        _time.Advance(TimeSpan.FromSeconds(0.5));
        await Press("ignitionoff");
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("truck.engine.enabled"));
        Assert.Equal(0, Field("truck.electric.enabled"));
    }

    // Why: ignitionoff with the engine running drops electric and engine together (spike).
    [Fact]
    public async Task Ignition_off_drops_electric_and_running_engine_together()
    {
        await Press("ignitionon");
        await Press("ignitionstrt");
        _time.Advance(TimeSpan.FromSeconds(2));
        await Press("ignitionoff");

        Assert.Equal(0, Field("truck.electric.enabled"));
        Assert.Equal(0, Field("truck.engine.enabled"));
    }

    // Why: documents our modelling assumption that the starter needs electric; the host sends ignitionon first.
    [Fact]
    public async Task Starter_with_electric_off_is_ignored()
    {
        await Press("ignitionstrt");
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("truck.engine.enabled"));
    }

    // Why: the lights LED mapping depends on exactly these positions (spike).
    [Theory]
    [InlineData("lightoff", 0, 0)]
    [InlineData("lightpark", 1, 0)]
    [InlineData("lighton", 1, 1)]
    public async Task Light_switch_positions_map_to_parking_and_low_beam(string input, int parking, int low)
    {
        await Press("lighton");
        await Press(input);

        Assert.Equal(parking, Field("truck.light.parking"));
        Assert.Equal(low, Field("truck.light.beam.low"));
    }

    // Why: high beam follows its own switch even with lights off, so lights changes must not reset it.
    [Fact]
    public async Task Hblight_toggles_high_beam_independent_of_the_light_switch()
    {
        await Press("lightoff");
        await Press("hblight");
        Assert.Equal(1, Field("truck.light.beam.high"));

        await Press("lighton");
        await Press("lightoff");
        Assert.Equal(1, Field("truck.light.beam.high"));

        await Press("hblight");
        Assert.Equal(0, Field("truck.light.beam.high"));
    }

    // Why: telemetry reports wipers on/off only, so auto/low/fast must all read 1.
    [Theory]
    [InlineData("wipers0", 0)]
    [InlineData("wipers1", 1)]
    [InlineData("wipers2", 1)]
    [InlineData("wipers3", 1)]
    public async Task Wipers_report_on_off_only(string input, int expected)
    {
        await Press("wipers3");
        await Press(input);

        Assert.Equal(expected, Field("truck.wipers"));
    }

    // Why: beacon, parking brake and hazards are toggles; the host must not assume absolute set.
    [Theory]
    [InlineData("beacon", "truck.light.beacon")]
    [InlineData("parkingbrake", "truck.brake.parking")]
    [InlineData("flasher4way", "truck.hazard.warning")]
    public async Task Toggle_inputs_flip_on_each_press(string input, string field)
    {
        await Press(input);
        Assert.Equal(1, Field(field));
        await Press(input);
        Assert.Equal(0, Field(field));
    }

    // Why: a paused/unfocused game ignores presses; the host must cope, and tests must be able to show it.
    [Fact]
    public async Task Game_not_listening_drops_outputs_but_still_records_them()
    {
        _sim.GameListening = false;

        await Press("lighton");

        Assert.Equal(0, Field("truck.light.beam.low"));
        Assert.False(_sim.Current.GameListening);
        Assert.Single(_sim.Received);
    }

    // Why: with no telemetry the host must show "unknown", so the snapshot has to be empty and unavailable.
    [Fact]
    public void No_telemetry_gives_unavailable_snapshot_without_fields()
    {
        _sim.TelemetryAvailable = false;

        Assert.False(_sim.Current.Available);
        Assert.Empty(_sim.Current.Fields);
    }

    // Why: the host updates LEDs from Changed, so every real change must raise it and no-ops must not.
    [Fact]
    public async Task Changed_is_raised_on_each_state_change_with_the_new_snapshot_and_not_on_no_ops()
    {
        var snapshots = new List<TelemetrySnapshot>();
        _sim.Changed += (_, s) => snapshots.Add(s);

        await Press("hblight");
        await Press("lightoff"); // Already off: no change.
        await Press("unknown");
        await Press("ignitionon");
        await Press("ignitionstrt"); // Starting does not change telemetry yet.
        _time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(3, snapshots.Count);
        Assert.Equal(1, snapshots[0].Fields["truck.light.beam.high"]);
        Assert.Equal(1, snapshots[1].Fields["truck.electric.enabled"]);
        Assert.Equal(1, snapshots[2].Fields["truck.engine.enabled"]);
    }

    // Why: Changed handlers may call back into the simulator, so it must not be raised while holding the lock.
    [Fact]
    public async Task Changed_handler_can_call_back_into_the_simulator()
    {
        TelemetrySnapshot? seen = null;
        _sim.Changed += (_, _) => seen = _sim.Current;

        await Press("lighton");

        Assert.NotNull(seen);
    }

    // Why: tests assert what the host sent; unknown outputs and holds must be recorded in order with their kind.
    [Fact]
    public async Task Received_records_every_output_in_order_with_kind_including_unknown()
    {
        var known = new ScsInput("lighton");
        var unknown = new ScsInput("nonsense");
        var vjoy = new VJoyButton(3);

        await _sim.PressAsync(known, Ct);
        await _sim.HoldAsync(unknown, Ct);
        await _sim.ReleaseAsync(vjoy, Ct);

        Assert.Equal(
            [
                new ReceivedOutput(OutputKind.Press, known),
                new ReceivedOutput(OutputKind.Hold, unknown),
                new ReceivedOutput(OutputKind.Release, vjoy),
            ],
            _sim.Received);
        Assert.Equal(0, Field("truck.engine.enabled"));
    }
}
