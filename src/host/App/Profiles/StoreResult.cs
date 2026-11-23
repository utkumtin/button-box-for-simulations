namespace ButtonBox.App.Profiles;

/// <summary>How a profile store call ended. The editor API maps them to 200, 404, 409, 422 and 500.</summary>
public enum StoreStatus
{
    Ok,

    /// <summary>No such profile (an id that is not valid is also not found).</summary>
    NotFound,

    /// <summary>The call is not allowed in the current state: the active profile, a damaged file, a taken name.</summary>
    Conflict,

    /// <summary>The input did not pass the gate; <see cref="StoreResult{T}.Problems"/> says why.</summary>
    Invalid,

    /// <summary>The file could not be written or removed. Nothing changed on disk and nothing was published.</summary>
    WriteFailed,
}

/// <summary>A typed result. <see cref="Value"/> is set only for <see cref="StoreStatus.Ok"/>; <see cref="Problems"/> only otherwise. Each problem is one fixed sentence.</summary>
public sealed record StoreResult<T>(StoreStatus Status, T? Value, IReadOnlyList<string> Problems)
{
    public bool Ok => Status == StoreStatus.Ok;

    public static StoreResult<T> Success(T value) => new(StoreStatus.Ok, value, []);

    public static StoreResult<T> NotFound() => new(StoreStatus.NotFound, default, ["The profile does not exist."]);

    public static StoreResult<T> Conflict(params string[] problems) => new(StoreStatus.Conflict, default, problems);

    public static StoreResult<T> Invalid(IReadOnlyList<string> problems) => new(StoreStatus.Invalid, default, problems);

    public static StoreResult<T> WriteFailed() => new(StoreStatus.WriteFailed, default, ["The profile file could not be written."]);
}
