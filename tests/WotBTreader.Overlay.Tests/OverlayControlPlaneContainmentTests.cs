using System.IO;
using System.Reflection;

namespace WotBTreader.Overlay.Tests;

[TestClass]
public sealed class OverlayControlPlaneContainmentTests
{
    [TestMethod]
    public void App_DoesNotOwnAnEmbeddedHttpListener()
    {
        Type appType = typeof(WotBTreader.Overlay.App);
        const BindingFlags declaredInstanceMembers =
            BindingFlags.Instance |
            BindingFlags.Public |
            BindingFlags.NonPublic |
            BindingFlags.DeclaredOnly;

        Assert.IsFalse(
            appType.GetMethods(declaredInstanceMembers)
                .Any(method => method.Name.Contains(
                    "OverlayApi",
                    StringComparison.OrdinalIgnoreCase)),
            "The overlay application must not start or stop an embedded HTTP API.");
        Assert.IsFalse(
            appType.GetFields(declaredInstanceMembers)
                .Any(field => field.FieldType.FullName?.StartsWith(
                    "Microsoft.AspNetCore",
                    StringComparison.Ordinal) == true),
            "The overlay application must not retain an ASP.NET Core listener.");
    }

    [TestMethod]
    public void Overlay_HasNoDeadEndpointCode()
    {
        string overlaySource = Path.Combine(
            ProjectRoot(),
            "src", "WotBTreader.Overlay");

        string endpointsDir = Path.Combine(overlaySource, "Endpoints");
        string overlayApiEndpoints = Path.Combine(endpointsDir, "OverlayApiEndpoints.cs");
        Assert.IsFalse(
            File.Exists(overlayApiEndpoints),
            "OverlayApiEndpoints.cs must be deleted in M3.");

        string servicesDir = Path.Combine(overlaySource, "Services");
        string overlayApiState = Path.Combine(servicesDir, "OverlayApiState.cs");
        Assert.IsFalse(
            File.Exists(overlayApiState),
            "OverlayApiState.cs must be deleted in M3.");
    }

    [TestMethod]
    public void OverlayCsproj_HasNoAspNetCoreFrameworkReference()
    {
        string csprojPath = Path.Combine(
            ProjectRoot(),
            "src", "WotBTreader.Overlay", "WotBTreader.Overlay.csproj");
        string content = File.ReadAllText(csprojPath);

        Assert.IsFalse(
            content.Contains("Microsoft.AspNetCore.App", StringComparison.Ordinal),
            "Overlay.csproj must not reference Microsoft.AspNetCore.App in M3.");
    }

    private static string ProjectRoot()
    {
        string assemblyDir = Path.GetDirectoryName(
            typeof(OverlayControlPlaneContainmentTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "..", ".."));
    }
}
