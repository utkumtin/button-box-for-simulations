namespace ButtonBox.Core.Engine;

/// <summary>What the engine did with a client message.</summary>
public enum HandleOutcome
{
    /// <summary>Valid and the game is listening. Outputs may still be empty (e.g. a switch already at its end).</summary>
    Accepted,

    /// <summary>Not valid for this layout, widget type or configured actions. Dropped, never closes the connection.</summary>
    Invalid,

    /// <summary>Valid, but the game does not accept input right now. Dropped, never queued.</summary>
    NotListening,
}

/// <param name="Outcome">What happened.</param>
/// <param name="Outputs">Game outputs to send, in order. Empty unless <paramref name="Outcome"/> is Accepted.</param>
/// <param name="StateChanged">True when anything the phone shows may have changed (a switch's commanded position moved, amber cleared, ...).</param>
public sealed record HandleResult(HandleOutcome Outcome, IReadOnlyList<GameOutput> Outputs, bool StateChanged)
{
    internal static HandleResult Invalid { get; } = new(HandleOutcome.Invalid, [], false);
    internal static HandleResult NotListening { get; } = new(HandleOutcome.NotListening, [], false);
}
