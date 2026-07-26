namespace WotBTreader.Application.Results;

public sealed record ApplicationError(
    string Code,
    string Message,
    bool Retryable = false);

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

    public bool IsSuccess { get; }

    public T? Value { get; }

    public ApplicationError? Error { get; }

    public IReadOnlyList<string> Warnings { get; }
}

public static class OperationResult
{
    public static OperationResult<T> Success<T>(T value, params string[] warnings) =>
        new(true, value, null, warnings);

    public static OperationResult<T> Failure<T>(ApplicationError error, params string[] warnings) =>
        new(false, default, error, warnings);
}
