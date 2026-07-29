namespace WotBTreader.Architecture.Tests;

[TestClass]
public sealed class ProjectReferenceTests
{
    private const string CoreName = "WotBTreader.Core";
    private const string ApplicationName = "WotBTreader.Application";
    private const string BootstrapName = "WotBTreader.Bootstrap";
    private const string ApiContractsName = "WotBTreader.ApiContracts";
    private const string OverlayName = "WotBTreader.Overlay";

    private static readonly string[] AdapterNames =
    [
        "WotBTreader.CaptureLogs",
        "WotBTreader.GameIntegration",
        "WotBTreader.Replays",
        "WotBTreader.Storage.Sqlite",
    ];

    private static readonly string[] HostNames = ["WotBTreader.Host.Cli", "WotBTreader.Host.Web"];

    private static readonly string[] ToolNames =
    [
        "WotBTreader.GameHarness",
        "WotBTreader.ReplayInspector",
        "WotBTreader.ReplaySanitizer",
    ];

    private static readonly string[] NoReferences = [];
    private static readonly string[] CoreOnly = [CoreName];
    private static readonly string[] AdapterAllowedReferences = [ApplicationName, CoreName];
    private static readonly string[] BootstrapAllowedReferences = [ApplicationName, CoreName, .. AdapterNames];
    private static readonly string[] HostAllowedReferences = [ApiContractsName, ApplicationName, BootstrapName, CoreName];
    private static readonly string[] OverlayAllowedReferences = [ApiContractsName];
    private static readonly string[] ToolAllowedReferences = [ApiContractsName, ApplicationName, BootstrapName, CoreName];

    [TestMethod]
    public void ProductionProjects_FollowTheApprovedReferenceGraph()
    {
        ProjectFile[] projects = [.. ProjectCatalog.Discover().Where(static project => project.IsProduction)];

        Assert.IsGreaterThan(0, projects.Length, "The architecture test must discover production projects.");

        string[] violations = AnalyzeProjects(projects);

        Assert.HasCount(
            0,
            violations,
            $"Project reference violations: {string.Join("; ", violations)}");
    }

    [TestMethod]
    public void AnalyzeProjects_AdapterReferencingAnotherAdapter_IsReported()
    {
        ProjectFile[] projects =
        [
            new(
                "src/WotBTreader.Replays/WotBTreader.Replays.csproj",
                Project("..\\WotBTreader.Storage.Sqlite\\WotBTreader.Storage.Sqlite.csproj")),
        ];

        string[] violations = AnalyzeProjects(projects);

        CollectionAssert.Contains(
            violations,
            "WotBTreader.Replays must not reference WotBTreader.Storage.Sqlite.");
    }

    [TestMethod]
    public void AnalyzeProjects_OverlayReferencingAdapter_IsReported()
    {
        ProjectFile[] projects =
        [
            new(
                "src/WotBTreader.Overlay/WotBTreader.Overlay.csproj",
                Project("..\\WotBTreader.Storage.Sqlite\\WotBTreader.Storage.Sqlite.csproj")),
        ];

        string[] violations = AnalyzeProjects(projects);

        CollectionAssert.Contains(
            violations,
            "WotBTreader.Overlay must not reference WotBTreader.Storage.Sqlite.");
    }

    [TestMethod]
    public void AnalyzeProjects_HostReferencingAdapter_IsReported()
    {
        ProjectFile[] projects =
        [
            new(
                "src/WotBTreader.Host.Web/WotBTreader.Host.Web.csproj",
                Project("..\\WotBTreader.Replays\\WotBTreader.Replays.csproj")),
        ];

        string[] violations = AnalyzeProjects(projects);

        CollectionAssert.Contains(
            violations,
            "WotBTreader.Host.Web must not reference WotBTreader.Replays.");
    }

    [TestMethod]
    public void AnalyzeProjects_UnclassifiedProductionProject_IsReported()
    {
        ProjectFile[] projects =
        [
            new("src/WotBTreader.Invented/WotBTreader.Invented.csproj", Project(null)),
        ];

        string[] violations = AnalyzeProjects(projects);

        CollectionAssert.Contains(
            violations,
            "WotBTreader.Invented is not classified in the approved reference graph.");
    }

    private static string[] AnalyzeProjects(IEnumerable<ProjectFile> projects)
    {
        List<string> violations = [];

        foreach (ProjectFile project in projects.OrderBy(static project => project.RelativePath, StringComparer.Ordinal))
        {
            if (!TryGetAllowedReferences(project.Name, out string[] allowedReferences))
            {
                violations.Add($"{project.Name} is not classified in the approved reference graph.");
                continue;
            }

            foreach (string reference in ProjectCatalog.ProjectReferences(project))
            {
                if (!allowedReferences.Contains(reference, StringComparer.Ordinal))
                {
                    violations.Add($"{project.Name} must not reference {reference}.");
                }
            }
        }

        return [.. violations];
    }

    private static bool TryGetAllowedReferences(string projectName, out string[] allowedReferences)
    {
        if (string.Equals(projectName, CoreName, StringComparison.Ordinal))
        {
            allowedReferences = NoReferences;
            return true;
        }

        if (string.Equals(projectName, ApplicationName, StringComparison.Ordinal))
        {
            allowedReferences = CoreOnly;
            return true;
        }

        if (string.Equals(projectName, ApiContractsName, StringComparison.Ordinal))
        {
            allowedReferences = NoReferences;
            return true;
        }

        if (string.Equals(projectName, BootstrapName, StringComparison.Ordinal))
        {
            allowedReferences = BootstrapAllowedReferences;
            return true;
        }

        if (string.Equals(projectName, OverlayName, StringComparison.Ordinal))
        {
            allowedReferences = OverlayAllowedReferences;
            return true;
        }

        if (AdapterNames.Contains(projectName, StringComparer.Ordinal))
        {
            allowedReferences = AdapterAllowedReferences;
            return true;
        }

        if (HostNames.Contains(projectName, StringComparer.Ordinal))
        {
            allowedReferences = HostAllowedReferences;
            return true;
        }

        if (ToolNames.Contains(projectName, StringComparer.Ordinal))
        {
            allowedReferences = ToolAllowedReferences;
            return true;
        }

        allowedReferences = NoReferences;
        return false;
    }

    private static string Project(string? projectReferenceInclude) =>
        projectReferenceInclude is null
            ? "<Project></Project>"
            : $"""<Project><ItemGroup><ProjectReference Include="{projectReferenceInclude}" /></ItemGroup></Project>""";
}
