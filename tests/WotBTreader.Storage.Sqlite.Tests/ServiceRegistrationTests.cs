using Microsoft.Extensions.DependencyInjection;
using WotBTreader.Application.Storage;

namespace WotBTreader.Storage.Sqlite.Tests;

[TestClass]
public sealed class ServiceRegistrationTests
{
    [TestMethod]
    public void AddSqliteStorageRegistersAllStoragePortsAsSingletons()
    {
        ServiceCollection services = [];
        SqliteStorageOptions options = new()
        {
            ApplicationDataRoot = Path.Combine(
                Path.GetTempPath(),
                "WotBTreader.Storage.Tests",
                Guid.NewGuid().ToString("N")),
        };

        IServiceCollection returned = services.AddSqliteStorage(options);

        Assert.AreSame(services, returned);
        AssertSingleton<IStorageInitializer>(services);
        AssertSingleton<ISourceArtifactStore>(services);
        AssertSingleton<IDecodeRunRepository>(services);
        AssertSingleton<ISessionQueryRepository>(services);
        AssertSingleton<ITrajectoryGroundTruthProvider>(services);
        AssertSingleton<IHpGroundTruthProvider>(services);
        AssertSingleton<IComparisonRunRepository>(services);
        AssertSingleton<IReplayClockSegmentRepository>(services);
        AssertSingleton<ISqliteStoragePathProvider>(services);
    }

    private static void AssertSingleton<T>(IServiceCollection services)
    {
        ServiceDescriptor? descriptor =
            services.SingleOrDefault(item => item.ServiceType == typeof(T));
        Assert.IsNotNull(descriptor, $"Missing registration for {typeof(T).Name}.");
        Assert.AreEqual(ServiceLifetime.Singleton, descriptor.Lifetime);
    }
}
