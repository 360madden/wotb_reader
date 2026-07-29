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

    private static readonly string[] ProjectDirectories = ["src", "tests", "tools/src", "tools/tests"];

    [TestMethod]
    public void RepositoryProjects_HaveExactPortableTargetFrameworks()
    {
        string repositoryRoot = FindRepositoryRoot(AppContext.BaseDirectory);
        ProjectFile[] projects = DiscoverProjects(repositoryRoot);

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

    private static ProjectFile[] DiscoverProjects(string repositoryRoot) =>
        [.. ProjectDirectories
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory),
                "*.csproj",
                SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(path => new ProjectFile(
                Path.GetRelativePath(repositoryRoot, path),
                File.ReadAllText(path)))];

    private static string[] AnalyzeProjects(IEnumerable<ProjectFile> projects)
    {
        List<string> violations = [];
        HashSet<string> projectNames = new(StringComparer.Ordinal);

        foreach (ProjectFile project in projects.OrderBy(static project => project.RelativePath, StringComparer.Ordinal))
        {
            string projectName = Path.GetFileNameWithoutExtension(project.RelativePath);
            if (!projectNames.Add(projectName))
            {
                violations.Add($"Project name '{projectName}' is not unique.");
            }

            XDocument document = XDocument.Parse(project.Contents, LoadOptions.None);
            XElement[] pluralTargetFrameworks = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "TargetFrameworks")
                .ToArray();
            if (pluralTargetFrameworks.Length > 0)
            {
                violations.Add($"{projectName} must not use TargetFrameworks.");
            }

            string[] targetFrameworks = document
                .Descendants()
                .Where(static element => element.Name.LocalName == "TargetFramework")
                .Select(static element => element.Value.Trim())
                .ToArray();
            if (targetFrameworks.Length != 1)
            {
                violations.Add($"{projectName} must declare exactly one TargetFramework, but declares {targetFrameworks.Length}.");
                continue;
            }

            string expectedTargetFramework = WindowsOnlyProjectNames.Contains(projectName)
                ? "net10.0-windows"
                : "net10.0";
            if (!string.Equals(targetFrameworks[0], expectedTargetFramework, StringComparison.Ordinal))
            {
                violations.Add(
                    $"{projectName} must target exactly {expectedTargetFramework}, but targets {targetFrameworks[0]}.");
            }
        }

        return [.. violations];
    }

    private static string FindRepositoryRoot(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WotBTreader.sln")) &&
                File.Exists(Path.Combine(directory.FullName, "Directory.Build.props")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the repository root from '{startDirectory}'. " +
            "Expected WotBTreader.sln and Directory.Build.props.");
    }

    private sealed record ProjectFile(string RelativePath, string Contents);
}
