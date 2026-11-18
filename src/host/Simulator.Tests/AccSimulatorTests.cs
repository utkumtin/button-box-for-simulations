using ButtonBox.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace ButtonBox.Simulator.Tests;

public class AccSimulatorTests
{
    private static readonly Dictionary<int, string> Map = new()
    {
        [1] = "IgnitionSequenceOn",
        [2] = "Starter",
        [3] = "CycleCarLightStages",
        [4] = "CycleWiper",
        [5] = "EnableFlashingLights",
        [6] = "PitLimiter",
    };

    private readonly FakeTimeProvider _time = new();
    private readonly AccSimulator _sim;

    public AccSimulatorTests() => _sim = new AccSimulator(Map, _time);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task Press(int button) => _sim.PressAsync(new VJoyButton(button), Ct);

    private int Field(string name) => _sim.Current.Fields[name];

    // Why: ACC's starter is a held input; the host's hold takeover only works if holding >= 0.47 s starts the engine.
    [Fact]
    public async Task Starter_held_long_enough_starts_engine_and_it_keeps_running_after_release()
    {
        await Press(1); // Ignition on: ACC ignores the starter without it.
        await _sim.HoldAsync(new VJoyButton(2), Ct);
        Assert.Equal(1, Field("starterEngineOn"));

        _time.Advance(TimeSpan.FromSeconds(0.46));
        Assert.Equal(0, Field("isEngineRunning"));
        _time.Advance(TimeSpan.FromSeconds(0.01));
        Assert.Equal(1, Field("isEngineRunning"));

        await _sim.ReleaseAsync(new VJoyButton(2), Ct);
        Assert.Equal(0, Field("starterEngineOn"));
        Assert.Equal(1, Field("isEngineRunning"));
    }

    // Why: this is the bug the hold takeover exists to avoid: a short tap must not start the engine.
    [Fact]
    public async Task Starter_released_before_the_delay_does_not_start_the_engine()
    {
        await Press(1); // Ignition on: ACC ignores the starter without it.
        await _sim.HoldAsync(new VJoyButton(2), Ct);
        _time.Advance(TimeSpan.FromSeconds(0.3));
        await _sim.ReleaseAsync(new VJoyButton(2), Ct);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("isEngineRunning"));
    }

    // Why: a 50 ms vJoy press of the starter is the exact short tap the real game ignores.
    [Fact]
    public async Task Momentary_starter_press_does_not_start_the_engine()
    {
        await Press(1); // Ignition on: ACC ignores the starter without it.
        await Press(2);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("isEngineRunning"));
    }

    // Why: ACC ignores the starter while the ignition is off; the host's engine preset must switch the ignition
    // on before it takes over the starter, and the Simulator must not hide a missing ignition press.
    [Fact]
    public async Task Starter_is_ignored_while_the_ignition_is_off()
    {
        await _sim.HoldAsync(new VJoyButton(2), Ct);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("starterEngineOn"));
        Assert.Equal(0, Field("isEngineRunning"));
    }

    // Why: the amber timeout must be testable for ACC too.
    [Fact]
    public async Task Never_starting_engine_stays_off_while_starter_is_held()
    {
        _sim.EngineNeverStarts = true;

        await Press(1);
        await _sim.HoldAsync(new VJoyButton(2), Ct);
        _time.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(0, Field("isEngineRunning"));
    }

    // Why: the light cycle has three stages; the host relies on the wrap to know the position after N presses.
    [Fact]
    public async Task Light_stages_cycle_0_1_2_and_wrap_to_0()
    {
        var seen = new List<int>();
        for (var i = 0; i < 4; i++)
        {
            await Press(3);
            seen.Add(Field("lightsStage"));
        }

        Assert.Equal([1, 2, 0, 1], seen);
    }

    // Why: the wiper cycle has four stages; same reasoning as the light cycle.
    [Fact]
    public async Task Wiper_levels_cycle_0_1_2_3_and_wrap_to_0()
    {
        var seen = new List<int>();
        for (var i = 0; i < 5; i++)
        {
            await Press(4);
            seen.Add(Field("wiperLV"));
        }

        Assert.Equal([1, 2, 3, 0, 1], seen);
    }

    // Why: the game switches flashing lights off itself; the host must see the telemetry drop without sending anything.
    [Fact]
    public async Task Flashing_lights_turn_themselves_off_after_about_2_6_seconds()
    {
        await Press(5);
        Assert.Equal(1, Field("flashingLights"));

        _time.Advance(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, Field("flashingLights"));
        _time.Advance(TimeSpan.FromSeconds(0.1));
        Assert.Equal(0, Field("flashingLights"));
    }

    // Why: ignition is a toggle in ACC, and switching it off must not leave a pending start behind.
    [Fact]
    public async Task Ignition_toggles_and_turning_it_off_stops_engine_and_cancels_pending_start()
    {
        await Press(1);
        Assert.Equal(1, Field("ignitionOn"));
        await _sim.HoldAsync(new VJoyButton(2), Ct);
        await Press(1);
        _time.Advance(TimeSpan.FromSeconds(5));

        Assert.Equal(0, Field("ignitionOn"));
        Assert.Equal(0, Field("isEngineRunning"));
        Assert.Equal(0, Field("starterEngineOn"));
    }

    // Why: pause/replay/off means the game ignores input; the host must not believe it landed.
    [Fact]
    public async Task Game_not_listening_drops_outputs_but_still_records_them()
    {
        _sim.GameListening = false;

        await Press(6);

        Assert.Equal(0, Field("pitLimiterOn"));
        Assert.Single(_sim.Received);
    }

    // Why: the catalog may map buttons the model does not know; they must be ignored and recorded, not crash.
    [Fact]
    public async Task Unmapped_buttons_and_scs_inputs_are_ignored_but_recorded()
    {
        await Press(31);
        await _sim.PressAsync(new ScsInput("lighton"), Ct);

        Assert.Equal(2, _sim.Received.Count);
        Assert.All(_sim.Current.Fields.Values, v => Assert.Equal(0, v));
    }

    // Why: the host updates LEDs from Changed, so every real change (including the timed ones) must raise it.
    [Fact]
    public async Task Changed_is_raised_for_presses_and_for_timer_driven_changes()
    {
        var count = 0;
        _sim.Changed += (_, _) => count++;

        await Press(5); // Flash on.
        _time.Advance(TimeSpan.FromSeconds(3)); // Flash off.

        Assert.Equal(2, count);
    }

    // Why: the Simulator must tell the host there is no telemetry, like a game that is closed.
    [Fact]
    public void No_telemetry_gives_unavailable_snapshot_without_fields()
    {
        _sim.TelemetryAvailable = false;

        Assert.False(_sim.Current.Available);
        Assert.Empty(_sim.Current.Fields);
    }
}
