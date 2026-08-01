using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Bootstrap.DependencyInjection;
using WotBTreader.Core;

return await ReplayInspector.RunAsync(args).ConfigureAwait(false);

internal static class ReplayInspector
{
    private const int SuccessExitCode = 0;
    private const int InvalidArgumentsExitCode = 2;
    private const int UnsupportedExitCode = 3;
    private const int InvalidInputExitCode = 4;
    private const int InternalFailureExitCode = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
    };

    public static async Task<int> RunAsync(string[] arguments)
    {
        bool includeSensitive = arguments.Contains(
            "--include-sensitive",
            StringComparer.Ordinal);
        string[] paths = arguments
            .Where(argument => !string.Equals(
                argument,
                "--include-sensitive",
                StringComparison.Ordinal))
            .ToArray();
        if (paths.Length != 1)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.invalid_arguments",
                    "Usage: WotBTreader.ReplayInspector <replay.wotbreplay> [--include-sensitive]"));
            return InvalidArgumentsExitCode;
        }

        string replayPath;
        try
        {
            replayPath = Path.GetFullPath(paths[0]);
        }
        catch (Exception exception) when (
            exception is ArgumentException or
            NotSupportedException or
            PathTooLongException)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.invalid_path",
                    "The replay path is invalid."));
            return InvalidArgumentsExitCode;
        }

        if (!File.Exists(replayPath))
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.not_found",
                    "The replay file does not exist."));
            return InvalidInputExitCode;
        }

        using ServiceProvider provider = BuildServiceProvider();

        try
        {
            SourceArtifact artifact = await CreateArtifactAsync(replayPath).ConfigureAwait(false);
            ReplayInput input = new(
                artifact,
                cancellationToken => ValueTask.FromResult<Stream>(
                    new FileStream(
                        replayPath,
                        new FileStreamOptions
                        {
                            Access = FileAccess.Read,
                            Mode = FileMode.Open,
                            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                            Share = FileShare.Read,
                        })));
            DecoderLimits limits = DecoderLimits.Default;

            IReplayProbe probe = provider.GetRequiredService<IReplayProbe>();
            OperationResult<ReplayProbeResult> probeResult = await probe.ProbeAsync(
                input,
                limits,
                CancellationToken.None).ConfigureAwait(false);
            if (!probeResult.IsSuccess || probeResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: null,
                    probeResult.Warnings,
                    probeResult.Error);
                return InvalidInputExitCode;
            }

            ReplayDecoderRegistry registry = provider.GetRequiredService<ReplayDecoderRegistry>();
            OperationResult<IReplayDecoder> decoderResult = registry.Select(probeResult.Value);
            if (!decoderResult.IsSuccess || decoderResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: new
                    {
                        probeResult.Value.GameVersion,
                        probeResult.Value.FormatVersion,
                    },
                    probeResult.Warnings,
                    decoderResult.Error);
                return UnsupportedExitCode;
            }

            IReplayDecoder decoder = decoderResult.Value;
            ReplayDecodeRequest request = new(
                input,
                DecodeRunId.New(),
                probeResult.Value,
                limits);
            OperationResult<ReplayDecodeProjection> decodeResult = await decoder.DecodeAsync(
                request,
                CancellationToken.None).ConfigureAwait(false);
            if (!decodeResult.IsSuccess || decodeResult.Value is null)
            {
                WriteEnvelope(
                    success: false,
                    data: null,
                    decodeResult.Warnings,
                    decodeResult.Error);
                return InvalidInputExitCode;
            }

            ReplayDecodeProjection projection = decodeResult.Value;
            object data = new
            {
                decoder = new
                {
                    projection.DecodeRun.DecoderId,
                    projection.DecodeRun.DecoderVersion,
                    projection.DecodeRun.SchemaVersion,
                    projection.DecodeRun.Capabilities,
                },
                session = projection.Session is null
                    ? null
                    : new
                    {
                        projection.Session.GameVersion,
                        projection.Session.MapId,
                        projection.Session.MapName,
                        projection.Session.BattleTimeUtc,
                        durationSeconds = projection.Session.Duration?.TotalSeconds,
                        hasViewpoint = projection.Session.ViewpointParticipantId is not null,
                    },
                participants = projection.Participants.Select(
                    (participant, index) => new
                    {
                        ordinal = index + 1,
                        accountId = includeSensitive ? participant.AccountId : null,
                        // Player names are public Wargaming statistics and are
                        // never gated; account IDs and clan tags stay opt-in.
                        playerName = participant.PlayerName,
                        clanTag = includeSensitive ? participant.ClanTag : null,
                        participant.EntityId,
                        participant.TeamNumber,
                        participant.VehicleCompactDescriptor,
                        participant.TankId,
                        participant.TankName,
                        participant.TankClass,
                        participant.BotStatus,
                    }),
                counts = new
                {
                    participants = projection.Participants.Count,
                    positions = projection.Positions.Count,
                    events = projection.Events.Count,
                    rawRecords = projection.RawRecords.Count,
                },
                sensitiveFieldsIncluded = includeSensitive,
            };
            WriteEnvelope(
                success: true,
                data,
                decodeResult.Warnings,
                error: null);
            return SuccessExitCode;
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            CryptographicException)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.read_failed",
                    "The replay could not be read."));
            return InvalidInputExitCode;
        }
        catch (Exception)
        {
            WriteEnvelope(
                success: false,
                data: null,
                warnings: [],
                new ApplicationError(
                    "inspector.internal_failure",
                    "The inspector failed without exposing sensitive internals."));
            return InternalFailureExitCode;
        }
    }

    private static ServiceProvider BuildServiceProvider()
    {
        ServiceCollection services = new();
        services.AddWotBTreaderReplayTooling();
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static async ValueTask<SourceArtifact> CreateArtifactAsync(string path)
    {
        FileInfo info = new(path);
        await using FileStream stream = new(
            path,
            new FileStreamOptions
            {
                Access = FileAccess.Read,
                Mode = FileMode.Open,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
                Share = FileShare.Read,
            });
        byte[] hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return new SourceArtifact(
            SourceArtifactId.New(),
            new ContentHash(Convert.ToHexString(hash).ToLowerInvariant()),
            info.Length,
            "application/vnd.wargaming.wotb-replay",
            ".wotbreplay",
            DateTimeOffset.UtcNow,
            "1");
    }

    private static void WriteEnvelope(
        bool success,
        object? data,
        IReadOnlyList<string> warnings,
        ApplicationError? error)
    {
        Console.WriteLine(JsonSerializer.Serialize(
            new
            {
                schemaVersion = "1",
                success,
                correlationId = Guid.CreateVersion7(),
                data,
                warnings,
                error,
            },
            JsonOptions));
    }
}
