using System.Xml.Linq;

namespace WotBTreader.Architecture.Tests;

[TestClass]
public sealed class TargetFrameworkTests
{
    private static readonly HashSet<string> WindowsOnlyProjectNames =
    [
        "WotBTreader.Overlay",
        "WotBTreader.Overlay.Tests",
        "WotBTreader.GameHarness",
        "WotBTreader.GameHarness.Tests",
    ];

    [TestMethod]
    public void RepositoryProjects_HaveExactPortableTargetFrameworks()
    {
        ProjectFile[] projects = ProjectCatalog.Discover();

        Assert.IsGreaterThan(0, projects.Length, "The architecture test must discover repository projects.");

        string[] violations = AnalyzeProjects(projects);

        Assert.HasCount(
            0,
            violations,
            $"Portable-TFM policy violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void AnalyzeProjects_PortableProjectChangedToWindows_IsReported()
    {
        ProjectFile[] projects =
        [
            new(
                "src/WotBTreader.Host.Web/WotBTreader.Host.Web.csproj",
                "<Project><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework></PropertyGroup></Project>"),
        ];

        string[] violations = AnalyzeProjects(projects);

        CollectionAssert.Contains(
            violations,
            "WotBTreader.Host.Web must target exactly net10.0, but targets net10.0-windows.");
    }

    private static string[] AnalyzeProjects(IEnumerable<ProjectFile> projects)
    {
        List<string> violations = [];
        HashSet<string> projectNames = new(StringComparer.Ordinal);

        foreach (ProjectFile project in projects.OrderBy(static project => project.RelativePath, StringComparer.Ordinal))
        {
            if (!projectNames.Add(project.Name))
            {
                violations.Add($"Project name '{project.Name}' is not unique.");
            }

            XDocument document = XDocument.Parse(project.Contents, LoadOptions.None);
            XElement[] pluralTargetFrameworks = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "TargetFrameworks")
                .ToArray();
            if (pluralTargetFrameworks.Length > 0)
            {
                violations.Add($"{project.Name} must not use TargetFrameworks.");
            }

            string[] targetFrameworks = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "TargetFramework")
                .Select(static element => element.Value.Trim())
                .ToArray();
            if (targetFrameworks.Length != 1)
            {
                violations.Add($"{project.Name} must declare exactly one TargetFramework, but declares {targetFrameworks.Length}.");
                continue;
            }

            string expectedTargetFramework = WindowsOnlyProjectNames.Contains(project.Name)
                ? "net10.0-windows"
                : "net10.0";
            if (!string.Equals(targetFrameworks[0], expectedTargetFramework, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{project.Name} must target exactly {expectedTargetFramework}, but targets {targetFrameworks[0]}.");
            }
        }

        return [.. violations];
    }
}
