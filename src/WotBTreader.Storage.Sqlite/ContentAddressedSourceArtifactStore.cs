using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using WotBTreader.Application.Diagnostics;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;

namespace WotBTreader.Storage.Sqlite;

internal sealed class ContentAddressedSourceArtifactStore : ISourceArtifactStore
{
    private const int CopyBufferBytes = 128 * 1024;
    private const int MaximumMediaTypeLength = 128;
    private const int MaximumExtensionLength = 17;
    private readonly SqliteStorageContext _context;
    private readonly ILogger<ContentAddressedSourceArtifactStore> _logger;

    public ContentAddressedSourceArtifactStore(
        SqliteStorageContext context,
        ILogger<ContentAddressedSourceArtifactStore>? logger = null)
    {
        _context = context;
        _logger = logger ?? NullLogger<ContentAddressedSourceArtifactStore>.Instance;
    }

    public async ValueTask<OperationResult<SourceImportOutcome>> ImportAsync(
        SourceImportRequest request,
        CancellationToken cancellationToken)
    {
        ApplicationError? validationError = ValidateRequest(request);
        if (validationError is not null)
        {
            return OperationResult.Failure<SourceImportOutcome>(validationError);
        }

        using Activity? activity = TreaderDiagnostics.ActivitySource.StartActivity("storage.import");
        StorageLog.ImportStarted(_logger, request.MediaType);

        string? temporaryPath = null;
        try
        {
            _context.EnsureDirectories();
            temporaryPath = Path.Combine(
                _context.Paths.StagingRoot,
                $"{Guid.NewGuid():N}.import");
            (ContentHash hash, long byteLength) = await CopyAndHashAsync(
                request.CandidatePath,
                temporaryPath,
                request.MaximumBytes,
                cancellationToken).ConfigureAwait(false);

            string objectDirectory = Path.Combine(_context.Paths.ContentRoot, hash.Value[..2]);
            Directory.CreateDirectory(objectDirectory);
            string objectPath = Path.Combine(objectDirectory, hash.Value);
            await InstallManagedObjectAsync(
                temporaryPath,
                objectPath,
                hash,
                byteLength,
                cancellationToken).ConfigureAwait(false);
            temporaryPath = null;

            DateTimeOffset importedAtUtc = DateTimeOffset.UtcNow;
            SourceArtifact candidate = new(
                SourceArtifactId.New(),
                hash,
                byteLength,
                request.MediaType.Trim(),
                NormalizeExtension(request.StoredExtension),
                importedAtUtc,
                "1");

            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteTransaction transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);
            try
            {
                await using SqliteCommand insert = connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO source_artifacts(
                        id, sha256, byte_length, media_type, stored_extension,
                        imported_at_utc, schema_version)
                    VALUES (
                        $id, $sha256, $byteLength, $mediaType, $storedExtension,
                        $importedAtUtc, $schemaVersion)
                    ON CONFLICT(sha256) DO NOTHING;
                    """;
                insert.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(candidate.Id.Value));
                insert.Parameters.AddWithValue("$sha256", candidate.Sha256.Value);
                insert.Parameters.AddWithValue("$byteLength", candidate.ByteLength);
                insert.Parameters.AddWithValue("$mediaType", candidate.MediaType);
                insert.Parameters.AddWithValue("$storedExtension", candidate.StoredExtension);
                insert.Parameters.AddWithValue(
                    "$importedAtUtc",
                    SqliteValueConversions.Utc(candidate.ImportedAtUtc));
                insert.Parameters.AddWithValue("$schemaVersion", candidate.SchemaVersion);
                bool inserted =
                    await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;

                SourceArtifact stored = await ReadByHashAsync(
                    connection,
                    transaction,
                    hash,
                    cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("The imported artifact was not retained.");
                if (stored.ByteLength != byteLength)
                {
                    throw new InvalidDataException("A managed object hash collision was detected.");
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                StorageLog.ImportCompleted(_logger, stored.Id, !inserted);
                return OperationResult.Success(new SourceImportOutcome(stored, !inserted));
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            StorageLog.ImportInvalid(_logger);
            return OperationResult.Failure<SourceImportOutcome>(
                new ApplicationError("storage.invalid_source", exception.Message));
        }
        catch (UnauthorizedAccessException exception)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            StorageLog.ImportAccessFailed(_logger, exception.HResult);
            return OperationResult.Failure<SourceImportOutcome>(StorageErrors.Unavailable());
        }
        catch (IOException exception)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            StorageLog.ImportIoFailed(_logger, exception.HResult);
            return OperationResult.Failure<SourceImportOutcome>(StorageErrors.Unavailable());
        }
        catch (SqliteException exception)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            StorageLog.ImportSqliteFailed(_logger, exception.SqliteErrorCode);
            return OperationResult.Failure<SourceImportOutcome>(StorageErrors.From(exception));
        }
        catch (Exception exception)
        {
            TreaderDiagnostics.ImportFailures.Add(1);
            StorageLog.ImportUnexpected(_logger, exception.HResult);
            return OperationResult.Failure<SourceImportOutcome>(StorageErrors.Internal());
        }
        finally
        {
            if (temporaryPath is not null && File.Exists(temporaryPath))
            {
                TryDeleteStagingFile(temporaryPath);
            }
        }
    }

    public async ValueTask<OperationResult<Stream>> OpenReadAsync(
        SourceArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        OperationResult<SourceArtifact> artifactResult =
            await GetAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (!artifactResult.IsSuccess || artifactResult.Value is null)
        {
            return OperationResult.Failure<Stream>(
                artifactResult.Error ?? StorageErrors.NotFound("Source artifact"));
        }

        string objectPath = GetObjectPath(artifactResult.Value.Sha256);
        try
        {
            FileInfo info = new(objectPath);
            if (!info.Exists || info.Length != artifactResult.Value.ByteLength)
            {
                return OperationResult.Failure<Stream>(
                    new ApplicationError(
                        "storage.content_missing",
                        "Managed source content is missing or inconsistent."));
            }

            Stream stream = new FileStream(
                objectPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferBytes,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return OperationResult.Success(stream);
        }
        catch (IOException)
        {
            return OperationResult.Failure<Stream>(StorageErrors.Unavailable());
        }
        catch (UnauthorizedAccessException)
        {
            return OperationResult.Failure<Stream>(StorageErrors.Unavailable());
        }
    }

    public async ValueTask<OperationResult<SourceArtifact>> GetAsync(
        SourceArtifactId artifactId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, sha256, byte_length, media_type, stored_extension,
                       imported_at_utc, schema_version
                FROM source_artifacts
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", SqliteValueConversions.Guid(artifactId.Value));
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return OperationResult.Failure<SourceArtifact>(
                    StorageErrors.NotFound("Source artifact"));
            }

            return OperationResult.Success(ReadArtifact(reader));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException exception)
        {
            return OperationResult.Failure<SourceArtifact>(StorageErrors.From(exception));
        }
    }

    private static ApplicationError? ValidateRequest(SourceImportRequest request)
    {
        if (request is null)
        {
            return StorageErrors.Invalid("An import request is required.");
        }

        if (string.IsNullOrWhiteSpace(request.CandidatePath))
        {
            return StorageErrors.Invalid("A source path is required.");
        }

        if (request.MaximumBytes <= 0)
        {
            return StorageErrors.Invalid("The source byte limit must be positive.");
        }

        if (string.IsNullOrWhiteSpace(request.MediaType) ||
            request.MediaType.Length > MaximumMediaTypeLength)
        {
            return StorageErrors.Invalid("The media type is invalid.");
        }

        try
        {
            _ = NormalizeExtension(request.StoredExtension);
        }
        catch (ArgumentException exception)
        {
            return StorageErrors.Invalid(exception.Message);
        }

        return null;
    }

    private static string NormalizeExtension(string extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            throw new ArgumentException("A stored extension is required.", nameof(extension));
        }

        string normalized = extension.StartsWith('.')
            ? extension
            : $".{extension}";
        if (normalized.Length > MaximumExtensionLength ||
            normalized.Length < 2 ||
            normalized.Skip(1).Any(character => !char.IsAsciiLetterOrDigit(character)))
        {
            throw new ArgumentException(
                "The stored extension must contain only ASCII letters and digits.",
                nameof(extension));
        }

        return normalized.ToLowerInvariant();
    }

    private static async ValueTask<(ContentHash Hash, long ByteLength)> CopyAndHashAsync(
        string sourcePath,
        string temporaryPath,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream source = new(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length > maximumBytes)
        {
            throw new InvalidDataException("The source exceeds the configured byte limit.");
        }

        await using FileStream destination = new(
            temporaryPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using IncrementalHash hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(CopyBufferBytes);
        long total = 0;
        try
        {
            while (true)
            {
                int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total = checked(total + read);
                if (total > maximumBytes)
                {
                    throw new InvalidDataException("The source exceeds the configured byte limit.");
                }

                hasher.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                    .ConfigureAwait(false);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            destination.Flush(flushToDisk: true);
            return (new ContentHash(Convert.ToHexString(hasher.GetHashAndReset())), total);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    private static async ValueTask InstallManagedObjectAsync(
        string temporaryPath,
        string objectPath,
        ContentHash expectedHash,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        try
        {
            File.Move(temporaryPath, objectPath);
            return;
        }
        catch (IOException) when (File.Exists(objectPath))
        {
            // A concurrent importer may have won the atomic rename. Verify the
            // existing immutable object before discarding this staging copy.
        }

        FileInfo existing = new(objectPath);
        if (existing.Length != expectedLength)
        {
            throw new InvalidDataException("A managed object hash collision was detected.");
        }

        await using FileStream stream = new(
            objectPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            CopyBufferBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] existingHash = await SHA256.HashDataAsync(stream, cancellationToken)
            .ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(
                existingHash,
                Convert.FromHexString(expectedHash.Value)))
        {
            throw new InvalidDataException("A managed object hash collision was detected.");
        }

        File.Delete(temporaryPath);
    }

    public async ValueTask<IReadOnlyList<string>> ListUnreferencedContentHashesAsync(
        CancellationToken cancellationToken)
    {
        // Collect all content-addressed object hashes on disk.
        HashSet<string> diskHashes = new(StringComparer.OrdinalIgnoreCase);
        string contentRoot = _context.Paths.ContentRoot;
        if (!Directory.Exists(contentRoot))
        {
            return [];
        }

        foreach (string subDir in Directory.GetDirectories(contentRoot))
        {
            string dirName = Path.GetFileName(subDir);
            if (dirName.Length != 2)
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(subDir))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Length == 64) // SHA-256 hex
                {
                    diskHashes.Add(fileName);
                }
            }
        }

        if (diskHashes.Count == 0)
        {
            return [];
        }

        // Query all referenced hashes from the database.
        try
        {
            await using SqliteConnection connection =
                await _context.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT sha256 FROM source_artifacts
                WHERE sha256 IS NOT NULL;
                """;
            await using SqliteDataReader reader =
                await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                diskHashes.Remove(reader.GetString(0));
            }
        }
        catch (SqliteException)
        {
            // If the database is unavailable, return nothing rather than
            // incorrectly marking everything as unreferenced.
            return [];
        }

        return [.. diskHashes];
    }

    private string GetObjectPath(ContentHash hash) =>
        Path.Combine(_context.Paths.ContentRoot, hash.Value[..2], hash.Value);

    private void TryDeleteStagingFile(string temporaryPath)
    {
        try
        {
            File.Delete(temporaryPath);
        }
        catch (IOException exception)
        {
            StorageLog.StagingCleanupFailed(_logger, exception.HResult);
        }
        catch (UnauthorizedAccessException exception)
        {
            StorageLog.StagingCleanupFailed(_logger, exception.HResult);
        }
    }

    private static async ValueTask<SourceArtifact?> ReadByHashAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ContentHash hash,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, sha256, byte_length, media_type, stored_extension,
                   imported_at_utc, schema_version
            FROM source_artifacts
            WHERE sha256 = $sha256;
            """;
        command.Parameters.AddWithValue("$sha256", hash.Value);
        await using SqliteDataReader reader =
            await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? ReadArtifact(reader)
            : null;
    }

    private static SourceArtifact ReadArtifact(SqliteDataReader reader) =>
        new(
            new SourceArtifactId(Guid.Parse(reader.GetString(0))),
            new ContentHash(reader.GetString(1)),
            reader.GetInt64(2),
            reader.GetString(3),
            reader.GetString(4),
            SqliteValueConversions.ReadUtc(reader, 5),
            reader.GetString(6));
}
