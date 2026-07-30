namespace WotBTreader.Application.Results;

/// <summary>
/// Describes an application-level error with a stable machine-readable
/// code and an optional retry hint.
/// </summary>
/// <param name="Code">Stable machine-readable error code (e.g. "storage.read-failed").</param>
/// <param name="Message">Human-readable error description.</param>
/// <param name="Retryable">True if the caller may retry the operation.</param>
public sealed record ApplicationError(
    string Code,
    string Message,
    bool Retryable = false);

/// <summary>
/// Typed result that conveys success/failure, an optional value, an optional
/// application error, and a list of warnings. Callers should inspect
/// <see cref="IsSuccess"/> before accessing <see cref="Value"/>.
/// </summary>
/// <typeparam name="T">The success value type.</typeparam>
public sealed class OperationResult<T>
{
    internal OperationResult(
        bool isSuccess,
        T? value,
        ApplicationError? error,
        IReadOnlyList<string> warnings)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
        Warnings = warnings;
    }

    /// <summary>True when the operation completed successfully.</summary>
    public bool IsSuccess { get; }

    /// <summary>The operation result value. Only valid when <see cref="IsSuccess"/> is true.</summary>
    public T? Value { get; }

    /// <summary>Application error when <see cref="IsSuccess"/> is false. Null on success.</summary>
    public ApplicationError? Error { get; }

    /// <summary>Warning messages produced during the operation. Empty on success.</summary>
    public IReadOnlyList<string> Warnings { get; }
}

public static class OperationResult
{
    public static OperationResult<T> Success<T>(T value, params string[] warnings) =>
        new(true, value, null, warnings);

    public static OperationResult<T> Failure<T>(ApplicationError error, params string[] warnings) =>
        new(false, default, error, warnings);
}
