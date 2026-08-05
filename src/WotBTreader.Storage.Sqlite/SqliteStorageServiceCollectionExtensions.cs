using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Storage;

namespace WotBTreader.Storage.Sqlite;

/// <summary>Registers the SQLite-backed storage boundary and its resolved paths.</summary>
public static class SqliteStorageServiceCollectionExtensions
{
    public static IServiceCollection AddSqliteStorage(
        this IServiceCollection services,
        SqliteStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);

        SqliteStoragePaths paths = new(options);
        services.AddSingleton(options);
        services.AddSingleton<ISqliteStoragePathProvider>(paths);
        services.AddSingleton<SqliteStorageContext>();
        services.AddSingleton<IStorageInitializer, SqliteStorageInitializer>();
        services.AddSingleton<ISourceArtifactStore, ContentAddressedSourceArtifactStore>();
        services.AddSingleton<IDecodeRunRepository, SqliteDecodeRunRepository>();
        services.AddSingleton<ISessionQueryRepository, SqliteSessionQueryRepository>();
        services.AddSingleton<ITrajectoryGroundTruthProvider, SqliteTrajectoryGroundTruthProvider>();
        services.AddSingleton<IComparisonRunRepository, SqliteComparisonRunRepository>();
        services.AddSingleton<IReplayClockSegmentRepository, SqliteReplayClockSegmentRepository>();
        return services;
    }
}
