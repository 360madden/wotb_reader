using System.Text.Json;

namespace WotBTreader.GameHarness;

internal static class ScannerStateStore
{
    private const int MaximumStateBytes = 64 * 1024;
    private const string StateFileName = "scanner-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static ScannerState? Load(string? directory = null)
    {
        directory ??= GetOffsetsDirectory();
        string path = Path.Combine(directory, StateFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumStateBytes)
        {
            return null;
        }

        byte[] content = new byte[checked((int)stream.Length)];
        stream.ReadExactly(content);
        return JsonSerializer.Deserialize<ScannerState>(content, JsonOptions);
    }

    private static string GetOffsetsDirectory()
    {
        string current = Environment.CurrentDirectory;
        for (int level = 0; level < 6; level++)
        {
            string candidate = Path.Combine(current, "memory-offsets");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }

            current = parent;
        }

        return Path.Combine(Environment.CurrentDirectory, "memory-offsets");
    }
}

internal sealed record ScannerState
{
    public int ProcessId { get; init; }

    public string ExecutableVersion { get; init; } = string.Empty;

    public long BaseAddress { get; init; }

    public int CandidateCount { get; init; }

    public List<long> TopCandidates { get; init; } = [];
}
