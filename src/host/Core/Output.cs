namespace ButtonBox.Core;

/// <summary>One output a game accepts: a vJoy button or an SCS semantical input.</summary>
public abstract record GameOutput;

/// <summary>A button on our vJoy device (1–32). Used by AC1, ACC and AC EVO.</summary>
public sealed record VJoyButton(int Number) : GameOutput;

/// <summary>An SCS semantical input name such as <c>lighton</c> or <c>wipers2</c>. Used by ETS2 and ATS.</summary>
public sealed record ScsInput(string Name) : GameOutput;

/// <summary>
/// Sends outputs to a game (vJoy feeder, SCS plugin bridge, Simulator). Implementations own the game's
/// timing rules, e.g. vJoy presses of 50 ms with 50 ms gaps, SCS presses held for at least 3 polled frames.
/// </summary>
public interface IOutputBackend
{
    /// <summary>A single momentary press (press, then release after the backend's press duration).</summary>
    Task PressAsync(GameOutput output, CancellationToken cancellationToken = default);

    /// <summary>Starts holding an output until <see cref="ReleaseAsync"/> (ACC starter takeover).</summary>
    Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default);

    /// <summary>Releases an output started with <see cref="HoldAsync"/>.</summary>
    Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default);
}
