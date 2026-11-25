using ButtonBox.Core;
using ButtonBox.Core.Contracts;

namespace ButtonBox.App;

/// <summary>
/// The backend the game session talks to when the SCS bridge is in use (decisions §3 "Backend selection"): while the active
/// profile is an ETS2 or ATS profile, everything goes to the bridge; for every other game, or without a profile, there is no
/// backend: <see cref="Current"/> is <see cref="TelemetrySnapshot.None"/> and presses are dropped at once.
/// Takes no lock of its own: the session reads <see cref="Current"/> under its session lock, so this class must never call back into the session.
/// </summary>
public sealed class GameBackend : IOutputBackend, IStateProvider
{
    private readonly Func<Game?> _activeGame;
    private readonly IOutputBackend _scs;
    private readonly IStateProvider _scsState;

    /// <param name="activeGame">The game of the active profile, or null; a plain read, no lock (the session's <c>Engine</c> is a volatile read).</param>
    /// <param name="scs">The backend for ETS2 and ATS (the bridge).</param>
    /// <param name="scsState">The state provider for ETS2 and ATS (the bridge).</param>
    public GameBackend(Func<Game?> activeGame, IOutputBackend scs, IStateProvider scsState)
    {
        ArgumentNullException.ThrowIfNull(activeGame);
        ArgumentNullException.ThrowIfNull(scs);
        ArgumentNullException.ThrowIfNull(scsState);
        _activeGame = activeGame;
        _scs = scs;
        _scsState = scsState;
        _scsState.Changed += OnScsChanged;
    }

    /// <inheritdoc />
    public event EventHandler<TelemetrySnapshot>? Changed;

    /// <inheritdoc />
    public TelemetrySnapshot Current => IsScs ? _scsState.Current : TelemetrySnapshot.None;

    private bool IsScs => _activeGame() is Game.Ets2 or Game.Ats;

    /// <inheritdoc />
    public Task PressAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        IsScs ? _scs.PressAsync(output, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task HoldAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        IsScs ? _scs.HoldAsync(output, cancellationToken) : Task.CompletedTask;

    /// <inheritdoc />
    public Task ReleaseAsync(GameOutput output, CancellationToken cancellationToken = default) =>
        IsScs ? _scs.ReleaseAsync(output, cancellationToken) : Task.CompletedTask;

    // A profile switch publishes to every phone itself, so nothing is raised for a game that has no backend.
    private void OnScsChanged(object? sender, TelemetrySnapshot snapshot)
    {
        if (IsScs)
        {
            Changed?.Invoke(this, snapshot);
        }
    }
}
