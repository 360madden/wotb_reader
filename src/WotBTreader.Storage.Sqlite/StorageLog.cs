using Microsoft.Extensions.Logging;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal static partial class StorageLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Importing a source artifact with media type {MediaType}")]
    public static partial void ImportStarted(ILogger logger, string mediaType);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Imported source artifact {ArtifactId}; duplicate={AlreadyExisted}")]
    public static partial void ImportCompleted(
        ILogger logger,
        SourceArtifactId artifactId,
        bool alreadyExisted);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "Source import was rejected as invalid")]
    public static partial void ImportInvalid(ILogger logger);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Source import could not access durable storage; hresult={HResult}")]
    public static partial void ImportAccessFailed(ILogger logger, int hResult);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Source import failed during durable file access; hresult={HResult}")]
    public static partial void ImportIoFailed(ILogger logger, int hResult);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Source import metadata failed with SQLite code {SqliteCode}")]
    public static partial void ImportSqliteFailed(
        ILogger logger,
        int sqliteCode);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Source import failed unexpectedly; hresult={HResult}")]
    public static partial void ImportUnexpected(ILogger logger, int hResult);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Warning,
        Message = "A private staging artifact could not be removed; hresult={HResult}")]
    public static partial void StagingCleanupFailed(ILogger logger, int hResult);

    [LoggerMessage(
        EventId = 4000,
        Level = LogLevel.Information,
        Message = "Applying storage migration {MigrationVersion} {MigrationName}")]
    public static partial void MigrationApplying(
        ILogger logger,
        int migrationVersion,
        string migrationName);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Storage migration failed with SQLite code {SqliteCode}")]
    public static partial void MigrationSqliteFailed(
        ILogger logger,
        int sqliteCode);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Storage migration failed during durable file access; hresult={HResult}")]
    public static partial void MigrationIoFailed(ILogger logger, int hResult);

    [LoggerMessage(
        EventId = 4001,
        Level = LogLevel.Error,
        Message = "Storage migration failed unexpectedly; hresult={HResult}")]
    public static partial void MigrationUnexpected(ILogger logger, int hResult);
}
