using System.Xml.Linq;

namespace WotBTreader.Architecture.Tests;

internal sealed record ProjectFile(string RelativePath, string Contents)
{
    public string Name => Path.GetFileNameWithoutExtension(RelativePath);

    public bool IsProduction =>
        RelativePath.StartsWith("src/", StringComparison.Ordinal) ||
        RelativePath.StartsWith("tools/src/", StringComparison.Ordinal);
}

internal static class ProjectCatalog
{
    private static readonly string[] ProjectDirectories = ["src", "tests", "tools/src", "tools/tests"];

    public static ProjectFile[] Discover()
    {
        string repositoryRoot = RepositoryRoot();

        return [.. ProjectDirectories
            .SelectMany(directory => Directory.EnumerateFiles(
                Path.Combine(repositoryRoot, directory),
                "*.csproj",
                SearchOption.AllDirectories))
            .Order(StringComparer.Ordinal)
            .Select(path => new ProjectFile(
                Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
                File.ReadAllText(path)))];
    }

    public static string[] ProjectReferences(ProjectFile project) =>
        [.. XDocument.Parse(project.Contents, LoadOptions.None)
            .Descendants()
            .Where(static element => element.Name.LocalName == "ProjectReference")
            .Select(static element => element.Attribute("Include")?.Value)
            .Where(static include => !string.IsNullOrWhiteSpace(include))
            .Select(static include => Path.GetFileNameWithoutExtension(include!.Replace('\\', '/')))
            .Order(StringComparer.Ordinal)];

    public static string RepositoryRoot() =>
        FindRepositoryRoot(AppContext.BaseDirectory);

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
}
