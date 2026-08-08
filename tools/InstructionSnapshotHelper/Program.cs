using System.IO.Pipes;
using System.Text.Json;

namespace WotBTreader.WriteInterceptor;

internal static class Program
{
    private const int MaximumPlanBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                return 2;
            }

            return args[0] switch
            {
                "--execute-object-snapshot" => await RunExecuteObjectSnapshotAsync(args).ConfigureAwait(false),
                "--snapshot-self-test" => RunSnapshotSelfTest(args),
                "--snapshot-counter" => RunSnapshotCounter(args),
                "--verify-coordinator-file" => VerifyCoordinatorFile(args),
                _ => 2,
            };
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"unexpected_error:{exception.GetType().Name}");
            return 5;
        }
    }

    private static int RunSnapshotCounter(string[] args)
    {
        string? stateFile = GetArg(args, "-StateFile");
        return string.IsNullOrWhiteSpace(stateFile) ? 2 : SnapshotCounterMode.Run(stateFile);
    }

    private static int RunSnapshotSelfTest(string[] args)
    {
        string? outPath = GetArg(args, "-Out");
        return string.IsNullOrWhiteSpace(outPath) ? 2 : SnapshotCounterMode.RunSelfTest(outPath);
    }

    private static int VerifyCoordinatorFile(string[] args)
    {
        string? path = GetArg(args, "-Path");
        string? assemblyPath = GetArg(args, "-AssemblyPath");
        string? nonce = GetArg(args, "-Nonce");
        bool verified = !string.IsNullOrWhiteSpace(path)
            && !string.IsNullOrWhiteSpace(assemblyPath)
            && nonce is { Length: 32 }
            && nonce.All(Uri.IsHexDigit)
            && ExecuteSnapshotInterceptor.IsPinnedCoordinatorImage(path, assemblyPath);
        if (!verified)
        {
            return 3;
        }

        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schema = "wotbtreader.instruction-snapshot-helper.verify.v1",
            nonce,
            verified = true,
        }));
        return 0;
    }

    private static async Task<int> RunExecuteObjectSnapshotAsync(string[] args)
    {
        string? planHandle = GetArg(args, "-PlanPipe");
        string? resultHandle = GetArg(args, "-ResultPipe");
        string? cancelHandle = GetArg(args, "-CancelPipe");
        if (!IsPipeHandle(planHandle) || !IsPipeHandle(resultHandle) || !IsPipeHandle(cancelHandle))
        {
            return 2;
        }

        using AnonymousPipeClientStream planPipe = new(PipeDirection.In, planHandle!);
        using AnonymousPipeClientStream resultPipe = new(PipeDirection.Out, resultHandle!);
        using AnonymousPipeClientStream cancelPipe = new(PipeDirection.In, cancelHandle!);
        using CancellationTokenSource cancellation = new();
        Task cancellationMonitor = MonitorCancellationAsync(cancelPipe, cancellation);

        string planJson = await ReadBoundedUtf8Async(
            planPipe,
            MaximumPlanBytes,
            cancellation.Token).ConfigureAwait(false);
        ExecuteSnapshotPlan? plan = JsonSerializer.Deserialize<ExecuteSnapshotPlan>(planJson, JsonOptions);
        if (plan is null || plan.SyntheticOwnedTarget)
        {
            return 2;
        }

        using var interceptor = new ExecuteSnapshotInterceptor(plan, cancellation.Token);
        (int exitCode, ExecuteSnapshotReport report) = interceptor.Run();
        await JsonSerializer.SerializeAsync(resultPipe, report, JsonOptions).ConfigureAwait(false);
        await resultPipe.FlushAsync().ConfigureAwait(false);
        GC.KeepAlive(cancellationMonitor);
        return exitCode;
    }

    private static async Task MonitorCancellationAsync(
        Stream cancelPipe,
        CancellationTokenSource cancellation)
    {
        try
        {
            _ = await cancelPipe.ReadAsync(new byte[1]).ConfigureAwait(false);
        }
        catch
        {
            // A closed coordinator pipe is cancellation, never authorization.
        }

        cancellation.Cancel();
    }

    private static async Task<string> ReadBoundedUtf8Async(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using MemoryStream buffer = new();
        byte[] block = new byte[1024];
        while (true)
        {
            int read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException("input_too_large");
            }

            buffer.Write(block, 0, read);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static bool IsPipeHandle(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 32
        && value.All(static character => char.IsAsciiDigit(character));

    private static string? GetArg(string[] args, string name)
    {
        for (int i = 1; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
