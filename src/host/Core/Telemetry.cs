namespace ButtonBox.Core;

/// <summary>
/// A point-in-time view of the game's state. Field names are the game's own telemetry names
/// (e.g. <c>truck.engine.enabled</c>, <c>lightsStage</c>); values are integers because the button box
/// shows only lit/unlit (0/1) or a position.
/// </summary>
/// <param name="Available">False when there is no telemetry (game closed, in a menu, no data).</param>
/// <param name="GameListening">False when the game does not accept input (paused, in the background, not LIVE).</param>
/// <param name="Fields">Current field values. Empty when <paramref name="Available"/> is false.</param>
public sealed record TelemetrySnapshot(bool Available, bool GameListening, IReadOnlyDictionary<string, int> Fields)
{
    public static TelemetrySnapshot None { get; } =
        new(false, false, new Dictionary<string, int>());
}

/// <summary>Provides game state (AC shared memory reader, SCS plugin bridge, Simulator).</summary>
public interface IStateProvider
{
    /// <summary>The latest snapshot.</summary>
    TelemetrySnapshot Current { get; }

    /// <summary>Raised after <see cref="Current"/> changes.</summary>
    event EventHandler<TelemetrySnapshot>? Changed;
}
