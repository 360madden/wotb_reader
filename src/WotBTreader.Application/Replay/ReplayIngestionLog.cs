using Microsoft.Extensions.Logging;

namespace WotBTreader.Application.Replay;

internal static partial class ReplayIngestionLog
{
    [LoggerMessage(
        EventId = 2000,
        Level = LogLevel.Information,
        Message = "Replay import started.")]
    public static partial void ImportStarted(ILogger logger);

    [LoggerMessage(
        EventId = 2001,
        Level = LogLevel.Information,
        Message = "Replay import completed for artifact {ArtifactId}.")]
    public static partial void ImportCompleted(ILogger logger, Guid artifactId);

    [LoggerMessage(
        EventId = 2002,
        Level = LogLevel.Error,
        Message = "Managed source artifact could not be opened for probing.")]
    public static partial void ProbeOpenFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3000,
        Level = LogLevel.Information,
        Message = "Decode run {DecodeRunId} started with decoder {DecoderId}.")]
    public static partial void DecodeStarted(ILogger logger, Guid decodeRunId, string decoderId);

    [LoggerMessage(
        EventId = 3001,
        Level = LogLevel.Information,
        Message = "Decode run {DecodeRunId} committed with {ParticipantCount} participants and {PositionCount} positions.")]
    public static partial void DecodeCompleted(
        ILogger logger,
        Guid decodeRunId,
        int participantCount,
        int positionCount);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "Managed source artifact could not be reopened for decoding.")]
    public static partial void DecodeOpenFailed(ILogger logger, Exception exception);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Error,
        Message = "Unexpected decode failure in run {DecodeRunId}.")]
    public static partial void UnexpectedDecodeFailure(
        ILogger logger,
        Guid decodeRunId,
        Exception exception);

    [LoggerMessage(
        EventId = 3002,
        Level = LogLevel.Warning,
        Message = "Decode run {DecodeRunId} ended with code {ErrorCode}.")]
    public static partial void DecodeRunFailed(ILogger logger, Guid decodeRunId, string errorCode);
}
