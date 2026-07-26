using Microsoft.Data.Sqlite;
using WotBTreader.Application.Results;

namespace WotBTreader.Storage.Sqlite;

internal static class StorageErrors
{
    public static ApplicationError Invalid(string message) =>
        new("storage.invalid_input", message);

    public static ApplicationError NotFound(string entity) =>
        new("storage.not_found", $"{entity} was not found.");

    public static ApplicationError Conflict(string message) =>
        new("storage.conflict", message);

    public static ApplicationError Busy() =>
        new("storage.busy", "Storage is busy; retry the operation.", Retryable: true);

    public static ApplicationError Unavailable() =>
        new("storage.unavailable", "Storage is unavailable.", Retryable: true);

    public static ApplicationError Internal() =>
        new("storage.internal", "Storage could not complete the operation.");

    public static ApplicationError From(SqliteException exception) =>
        exception.SqliteErrorCode is 5 or 6 ? Busy() : Internal();
}
