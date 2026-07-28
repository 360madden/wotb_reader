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
}
