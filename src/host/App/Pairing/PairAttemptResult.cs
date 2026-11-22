using ButtonBox.Core.Contracts;

namespace ButtonBox.App.Pairing;

public enum PairOutcome
{
    /// <summary>200, <see cref="PairAttemptResult.Response"/> is set.</summary>
    Paired,

    /// <summary>404: no pairing session is open (or it cannot take attempts).</summary>
    NotOpen,

    /// <summary>403: wrong code, or no code has been shown yet.</summary>
    WrongCode,

    /// <summary>410: the code expired, was burned by 5 wrong attempts, or was already used.</summary>
    Gone,

    /// <summary>409: the code was right but the registry is full.</summary>
    RegistryFull,

    /// <summary>400: the request failed validation.</summary>
    InvalidRequest,

    /// <summary>500: the code was right but saving the device or template failed.</summary>
    InternalError,
}

public sealed record PairAttemptResult(PairOutcome Outcome, PairResponse? Response = null);
