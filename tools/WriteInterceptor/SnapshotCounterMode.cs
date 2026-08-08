using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WotBTreader.WriteInterceptor;

internal static class SnapshotCounterMode
{
    private const int ObjectBytes = 0x1000;
    private const int EntityIdOffset = 0x1c;
    private const int PositionOffset = 0x90;
    private const int SyntheticEntityId = 4242;
    private const int WriteIntervalMilliseconds = 20;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint SnapshotStub(nint entityAddress, nint vectorAddress);

    internal static int Run(string stateFile)
    {
        nint objectAddress = CounterNative.VirtualAlloc(
            nint.Zero,
            ObjectBytes,
            0x3000,
            0x04);
        nint codeAddress = CounterNative.VirtualAlloc(
            nint.Zero,
            0x1000,
            0x3000,
            0x40);
        if (objectAddress == nint.Zero || codeAddress == nint.Zero)
        {
            return 3;
        }

        byte[] code =
        [
            0x56,                         // push esi
            0x8B, 0x74, 0x24, 0x08,       // mov esi,[esp+8]
            0x8B, 0x44, 0x24, 0x0C,       // mov eax,[esp+0xc]
            0x57,                         // push edi
            0xF3, 0x0F, 0x7E, 0x00,       // movq xmm0,[eax]
            0x8B, 0x40, 0x08,             // mov eax,[eax+8]
            0x5F,                         // pop edi
            0x5E,                         // pop esi
            0xC3,                         // ret
        ];
        Marshal.Copy(code, 0, codeAddress, code.Length);
        WriteUInt32(objectAddress + EntityIdOffset, SyntheticEntityId);
        WriteVector(objectAddress + PositionOffset, 1f);

        nint targetAddress = codeAddress + 10;
        string partialStateFile = stateFile + ".partial";
        using (FileStream stream = new(partialStateFile, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new
            {
                processId = Environment.ProcessId,
                targetAddress = $"0x{targetAddress.ToInt32():X8}",
            }, JsonOptions);
        }
        File.Move(partialStateFile, stateFile);

        SnapshotStub stub = Marshal.GetDelegateForFunctionPointer<SnapshotStub>(codeAddress);
        float value = 1f;
        while (true)
        {
            WriteVector(objectAddress + PositionOffset, value);
            _ = stub(objectAddress, objectAddress + PositionOffset);
            value += 0.5f;
            Thread.Sleep(WriteIntervalMilliseconds);
        }
    }

    internal static int RunSelfTest(string outPath)
    {
        if (File.Exists(outPath))
        {
            return 2;
        }

        string temporaryRoot = Path.Combine(
            Path.GetTempPath(),
            "wotbtreader-snapshot-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(temporaryRoot);
        string statePath = Path.Combine(temporaryRoot, "state.json");
        Process? child = null;
        string phase = "start";
        try
        {
            phase = "launch-child";
            string executable = Environment.ProcessPath
                ?? throw new InvalidOperationException("process_path_unavailable");
            child = Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = $"--snapshot-counter -StateFile \"{statePath}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (child is null)
            {
                return 3;
            }

            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            phase = "await-state";
            while (!File.Exists(statePath) && DateTimeOffset.UtcNow < deadline)
            {
                if (child.HasExited)
                {
                    return 3;
                }

                Thread.Sleep(20);
            }

            if (!File.Exists(statePath))
            {
                return 3;
            }

            using JsonDocument state = JsonDocument.Parse(File.ReadAllText(statePath));
            phase = "parse-state";
            string addressText = state.RootElement.GetProperty("targetAddress").GetString() ?? string.Empty;
            if (!uint.TryParse(
                addressText.Replace("0x", string.Empty, StringComparison.OrdinalIgnoreCase),
                NumberStyles.HexNumber,
                CultureInfo.InvariantCulture,
                out uint targetAddress))
            {
                return 3;
            }

            ExecuteSnapshotPlan plan = new()
            {
                ProcessId = child.Id,
                DurationMilliseconds = 1_500,
                MaxHits = 4,
                MinimumObjectSampleIntervalMilliseconds = 0,
                ExpectedInstructionHex = "F30F7E00",
                CaptureKind = "type10-entity-position",
                EntityIdDisplacement = EntityIdOffset,
                SyntheticOwnedTarget = true,
                SyntheticTargetAddress = targetAddress,
            };
            phase = "run-probe";
            (int exitCode, ExecuteSnapshotReport report) =
                new ExecuteSnapshotInterceptor(plan, CancellationToken.None).Run();
            if (exitCode != 0 || !report.CleanupProven || !report.Detached)
            {
                return 5;
            }

            phase = "run-timeout-cleanup-probe";
            ExecuteSnapshotPlan timeoutPlan = plan with
            {
                DurationMilliseconds = 1_000,
                MaxHits = 64,
                MinimumObjectSampleIntervalMilliseconds = 750,
            };
            (int timeoutExitCode, ExecuteSnapshotReport timeoutReport) =
                new ExecuteSnapshotInterceptor(timeoutPlan, CancellationToken.None).Run();
            if (timeoutExitCode != 0
                || timeoutReport.Truncated
                || timeoutReport.HitCount < 1
                || !timeoutReport.CleanupProven
                || !timeoutReport.Detached)
            {
                Console.Error.WriteLine(
                    $"snapshot_timeout_probe_failed:exit={timeoutExitCode}:status={timeoutReport.Status}:" +
                    $"hits={timeoutReport.HitCount}:cleanup={timeoutReport.CleanupProven}:" +
                    $"detached={timeoutReport.Detached}:diagnostics={string.Join(',', timeoutReport.Diagnostics)}");
                return 5;
            }

            phase = "write-report";
            using FileStream output = new(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            JsonSerializer.Serialize(output, report, JsonOptions);
            return exitCode;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"snapshot_self_test_failed:{phase}:{ex.GetType().Name}");
            return 5;
        }
        finally
        {
            if (child is { HasExited: false })
            {
                child.Kill(entireProcessTree: true);
                child.WaitForExit(2_000);
            }

            try
            {
                Directory.Delete(temporaryRoot, recursive: true);
            }
            catch
            {
                // Synthetic-only temporary cleanup is best effort.
            }
        }
    }

    private static void WriteVector(nint vectorAddress, float x)
    {
        WriteFloat(vectorAddress, x);
        WriteFloat(vectorAddress + 4, x + 10f);
        WriteFloat(vectorAddress + 8, x - 10f);
    }

    private static void WriteFloat(nint address, float value)
    {
        unsafe
        {
            *(float*)address = value;
        }
    }

    private static void WriteUInt32(nint address, uint value)
    {
        unsafe
        {
            *(uint*)address = value;
        }
    }
}
