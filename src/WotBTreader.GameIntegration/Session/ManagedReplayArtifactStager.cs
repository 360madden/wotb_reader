using System.Security.Cryptography;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.GameIntegration.Session;

internal interface IManagedReplayArtifactStager
{
    ValueTask<OperationResult<ManagedReplayArtifactLease>> StageAsync(
        SourceArtifactId sourceArtifactId,
        CancellationToken cancellationToken);
}

/// <summary>Creates a new, private staging file without replacing an existing file.</summary>
internal interface IReplayLaunchStagingPlatform
{
    /// <summary>
    /// Creates a file using create-new semantics. A null result means the requested name collided.
    /// The returned handle owns deletion of the staging file.
    /// </summary>
    ValueTask<IReplayLaunchStagingFile?> CreateNewAsync(
        string stagingRoot,
        string fileName,
        CancellationToken cancellationToken);
}

/// <summary>Represents a pinned staged file and owns its eventual cleanup.</summary>
internal interface IReplayLaunchStagingFile : IAsyncDisposable
{
    string Path { get; }

    Stream Stream { get; }

    ValueTask<bool> SealAsync(CancellationToken cancellationToken);
}

/// <summary>Generates opaque candidate file names for managed replay staging.</summary>
internal interface IReplayLaunchStageNameGenerator
{
    string Generate();
}

/// <summary>
/// Keeps a successfully staged replay pinned until the managed launch completes. The staging path
/// is internal so callers outside this adapter cannot use it as a source-selection mechanism.
/// </summary>
internal sealed class ManagedReplayArtifactLease : IAsyncDisposable
{
    private IReplayLaunchStagingFile? _stagingFile;

    internal ManagedReplayArtifactLease(
        string stagingPath,
        SourceArtifactId sourceArtifactId,
        ContentHash sha256,
        long byteLength,
        IReplayLaunchStagingFile stagingFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentNullException.ThrowIfNull(sha256);
        ArgumentNullException.ThrowIfNull(stagingFile);
        if (sourceArtifactId.Value == Guid.Empty || byteLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceArtifactId));
        }

        StagingPath = stagingPath;
        SourceArtifactId = sourceArtifactId;
        Sha256 = sha256;
        ByteLength = byteLength;
        _stagingFile = stagingFile;
    }

    internal string StagingPath { get; }

    internal SourceArtifactId SourceArtifactId { get; }

    internal ContentHash Sha256 { get; }

    internal long ByteLength { get; }

    public async ValueTask DisposeAsync()
    {
        IReplayLaunchStagingFile? stagingFile = Interlocked.Exchange(ref _stagingFile, null);
        if (stagingFile is not null)
        {
            await stagingFile.DisposeAsync().ConfigureAwait(false);
        }
    }

    public override string ToString() => nameof(ManagedReplayArtifactLease);
}

/// <summary>
/// Copies one immutable replay artifact into a private launch location while verifying the stored
/// length and SHA-256. It does not choose a replay, expose a path, or start the game process.
/// </summary>
internal sealed class ManagedReplayArtifactStager(
    ISourceArtifactStore artifactStore,
    GameIntegrationOptions options,
    IReplayLaunchStagingPlatform stagingPlatform,
    IReplayLaunchStageNameGenerator nameGenerator)
    : IManagedReplayArtifactStager
{
    private const string ReplayMediaType = "application/vnd.wotblitz.replay";
    private const string ReplayExtension = ".wotbreplay";
    private const int MaximumNameAttempts = 8;
    private const int BufferSize = 64 * 1024;

    private readonly ISourceArtifactStore _artifactStore =
        artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly GameIntegrationOptions _options =
        options ?? throw new ArgumentNullException(nameof(options));
    private readonly IReplayLaunchStagingPlatform _stagingPlatform =
        stagingPlatform ?? throw new ArgumentNullException(nameof(stagingPlatform));
    private readonly IReplayLaunchStageNameGenerator _nameGenerator =
        nameGenerator ?? throw new ArgumentNullException(nameof(nameGenerator));

    public async ValueTask<OperationResult<ManagedReplayArtifactLease>> StageAsync(
        SourceArtifactId sourceArtifactId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (sourceArtifactId.Value == Guid.Empty)
        {
            return Failure("game.launch.artifact_invalid", "The replay artifact identifier is invalid.");
        }

        if (string.IsNullOrWhiteSpace(_options.ReplayLaunchStagingRoot))
        {
            return StagingUnavailable();
        }

        OperationResult<SourceArtifact> artifactResult;
        try
        {
            artifactResult = await _artifactStore.GetAsync(sourceArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return StagingUnavailable();
        }

        if (!artifactResult.IsSuccess)
        {
            return IsNotFound(artifactResult.Error) ? NotFound() : StagingUnavailable();
        }

        if (artifactResult.Value is null)
        {
            return StagingUnavailable();
        }

        SourceArtifact artifact = artifactResult.Value;
        if (artifact.Id != sourceArtifactId)
        {
            return Inconsistent();
        }

        if (!string.Equals(artifact.MediaType, ReplayMediaType, StringComparison.Ordinal)
            || !string.Equals(artifact.StoredExtension, ReplayExtension, StringComparison.Ordinal))
        {
            return Failure("game.launch.artifact_unsupported", "The artifact is not a supported replay.");
        }

        if (artifact.ByteLength <= 0 || artifact.ByteLength > _options.MaxReplayLaunchBytes)
        {
            return Failure("game.launch.artifact_invalid", "The replay artifact length is invalid.");
        }

        OperationResult<Stream> sourceResult;
        try
        {
            sourceResult = await _artifactStore.OpenReadAsync(sourceArtifactId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return StagingUnavailable();
        }

        if (!sourceResult.IsSuccess)
        {
            return IsNotFound(sourceResult.Error) ? NotFound() : StagingUnavailable();
        }

        if (sourceResult.Value is null)
        {
            return StagingUnavailable();
        }

        await using Stream source = sourceResult.Value;
        IReplayLaunchStagingFile? stagingFile = await CreateStagingFileAsync(cancellationToken).ConfigureAwait(false);
        if (stagingFile is null)
        {
            return StagingUnavailable();
        }

        try
        {
            byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
            using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long copied = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
            {
                copied = checked(copied + read);
                if (copied > artifact.ByteLength)
                {
                    return await CleanupAndFailAsync(stagingFile, Inconsistent()).ConfigureAwait(false);
                }

                hasher.AppendData(buffer, 0, read);
                await stagingFile.Stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            if (copied != artifact.ByteLength
                || !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(artifact.Sha256.Value),
                    hasher.GetHashAndReset()))
            {
                return await CleanupAndFailAsync(stagingFile, Inconsistent()).ConfigureAwait(false);
            }

            await stagingFile.Stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await stagingFile.SealAsync(cancellationToken).ConfigureAwait(false))
            {
                return await CleanupAndFailAsync(stagingFile, Inconsistent()).ConfigureAwait(false);
            }

            if (!await VerifySealedFileAsync(
                    stagingFile.Stream,
                    artifact,
                    cancellationToken).ConfigureAwait(false))
            {
                return await CleanupAndFailAsync(stagingFile, Inconsistent()).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            return OperationResult.Success(
                new ManagedReplayArtifactLease(
                    stagingFile.Path,
                    sourceArtifactId,
                    artifact.Sha256,
                    artifact.ByteLength,
                    stagingFile));
        }
        catch (OperationCanceledException)
        {
            await CleanupAsync(stagingFile).ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await CleanupAsync(stagingFile).ConfigureAwait(false);
            return StagingUnavailable();
        }
    }

    private async ValueTask<IReplayLaunchStagingFile?> CreateStagingFileAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; attempt < MaximumNameAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name;
            try
            {
                name = _nameGenerator.Generate();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return null;
            }

            if (!IsValidStagingName(name))
            {
                return null;
            }

            try
            {
                IReplayLaunchStagingFile? file = await _stagingPlatform
                    .CreateNewAsync(_options.ReplayLaunchStagingRoot!, name, cancellationToken)
                    .ConfigureAwait(false);
                if (file is not null)
                {
                    if (string.IsNullOrWhiteSpace(file.Path) || file.Stream is null)
                    {
                        await CleanupAsync(file).ConfigureAwait(false);
                        return null;
                    }

                    return file;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                return null;
            }
        }

        return null;
    }

    internal static bool IsValidStagingName(string? value) =>
        value is { Length: 43 }
        && value.EndsWith(ReplayExtension, StringComparison.Ordinal)
        && value[..32].All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static async ValueTask<bool> VerifySealedFileAsync(
        Stream stream,
        SourceArtifact artifact,
        CancellationToken cancellationToken)
    {
        using IncrementalHash hasher =
            IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(BufferSize);
        long length = 0;
        int read;
        while ((read = await stream.ReadAsync(
                   buffer,
                   cancellationToken).ConfigureAwait(false)) != 0)
        {
            length = checked(length + read);
            if (length > artifact.ByteLength)
            {
                return false;
            }

            hasher.AppendData(buffer, 0, read);
        }

        bool matches = length == artifact.ByteLength &&
            CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(artifact.Sha256.Value),
                hasher.GetHashAndReset());
        if (matches && stream.CanSeek)
        {
            stream.Position = 0;
        }

        return matches;
    }

    private static async ValueTask<OperationResult<ManagedReplayArtifactLease>> CleanupAndFailAsync(
        IReplayLaunchStagingFile stagingFile,
        OperationResult<ManagedReplayArtifactLease> result)
    {
        await CleanupAsync(stagingFile).ConfigureAwait(false);
        return result;
    }

    private static async ValueTask CleanupAsync(IReplayLaunchStagingFile stagingFile)
    {
        try
        {
            await stagingFile.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Cleanup is best-effort; callers receive the original stable failure or cancellation.
        }
    }

    private static bool IsNotFound(ApplicationError? error) =>
        error?.Code is "storage.not_found" or "storage.artifact_not_found";

    private static OperationResult<ManagedReplayArtifactLease> NotFound() =>
        Failure("game.launch.artifact_not_found", "The replay artifact was not found.");

    private static OperationResult<ManagedReplayArtifactLease> Inconsistent() =>
        Failure("game.launch.artifact_inconsistent", "The replay artifact changed or is inconsistent.");

    private static OperationResult<ManagedReplayArtifactLease> StagingUnavailable() =>
        Failure(
            "game.launch.staging_unavailable",
            "Replay launch staging is unavailable.",
            retryable: true);

    private static OperationResult<ManagedReplayArtifactLease> Failure(
        string code,
        string message,
        bool retryable = false) =>
        OperationResult.Failure<ManagedReplayArtifactLease>(new ApplicationError(code, message, retryable));
}
