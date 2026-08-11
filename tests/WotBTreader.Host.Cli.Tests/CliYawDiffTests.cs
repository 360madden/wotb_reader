using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// End-to-end proof of the <c>yaw-diff</c> command: the snapshots file (the
/// pre-staged dump contract) + the REAL decoded yaw ground truth from a
/// seeded treader database (migration 5 <c>position_samples.yaw</c>) →
/// value-match lag correlation → the hardened verdict. Covers the
/// OD-RECOVERY-089 path: the per-dump bounded BIDIRECTIONAL lag search
/// (<c>--memory-lead-seconds --per-dump-lag</c>) that finds +0x30 when the
/// G2 label skew makes the memory LEAD the label (Dead Rail sign) — the
/// direction the one-directional shared path structurally cannot see.
/// </summary>
[TestClass]
public sealed class CliYawDiffTests
{
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid SessionId = Guid.Parse("019fdff8-8dcf-7426-8547-9fb8cc3eb07b");
    private const long Target = 7001;
    private const int LiveYawOffset = 0x30;

    [TestMethod]
    public async Task YawDiff_PerDumpLeadLag_FindsYawWhenMemoryLeadsLabel()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        // The packet yaw is 0 rad until t=10s then 1.2 rad; the memory at
        // label-time t carries the packet from t + 2 s (memory LEADS the
        // label — the Dead Rail sign). Every non-yaw 4-byte offset carries
        // the constant 0.7 (a value the packet timeline never contains), so
        // no zero-filled decoy can degenerate-match.
        string snapshotsPath = await WriteSnapshotsAsync(
            root,
            (6.0, PacketYawAt(8.0)),
            (8.0, PacketYawAt(10.0)),
            (10.0, PacketYawAt(12.0)),
            (12.0, PacketYawAt(14.0)),
            (14.0, PacketYawAt(16.0)),
            (16.0, PacketYawAt(18.0)));

        CliRun run = await RunAsync(root, "yaw-diff", snapshotsPath,
            "--session", SessionId.ToString("D"),
            "--victim", Target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--max-lag-seconds", "4",
            "--memory-lead-seconds", "4",
            "--per-dump-lag");

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        JsonElement data = run.Data;
        Assert.IsTrue(data.GetProperty("verdict").GetProperty("hit").GetBoolean(), run.Diagnostic);
        Assert.AreEqual(
            LiveYawOffset,
            data.GetProperty("topCandidate").GetProperty("offset").GetInt32());
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("score").GetDouble(), 1e-9);
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("flatness").GetDouble(), 1e-9);
        Assert.AreEqual(
            6,
            data.GetProperty("topCandidate").GetProperty("matchedWindows").GetInt32());
        // The memory-leading alignment is a NEGATIVE median lag; the
        // per-dump spread is reported (visible skew structure).
        Assert.IsLessThan(
            0.0,
            data.GetProperty("topCandidate").GetProperty("bestLagSeconds").GetDouble(),
            run.Diagnostic);
        Assert.IsTrue(
            data.GetProperty("topCandidate").TryGetProperty("lagSpreadSeconds", out _),
            run.Diagnostic);
    }

    [TestMethod]
    public async Task YawDiff_RejectsNegativeMemoryLead()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        string snapshotsPath = await WriteSnapshotsAsync(root, (6.0, 0.0), (8.0, 0.0));

        CliRun run = await RunAsync(root, "yaw-diff", snapshotsPath,
            "--session", SessionId.ToString("D"),
            "--victim", Target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory-lead-seconds", "-1");

        Assert.AreEqual((int)CliExitCode.InvalidArguments, run.ExitCode, run.Diagnostic);
    }

    [TestMethod]
    public async Task YawDiff_RejectsNonNumericMemoryLead()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        string snapshotsPath = await WriteSnapshotsAsync(root, (6.0, 0.0), (8.0, 0.0));

        CliRun run = await RunAsync(root, "yaw-diff", snapshotsPath,
            "--session", SessionId.ToString("D"),
            "--victim", Target.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--memory-lead-seconds", "soon");

        Assert.AreEqual((int)CliExitCode.InvalidArguments, run.ExitCode, run.Diagnostic);
    }

    /// <summary>Bootstrap the database (runs migrations), then seed the
    /// battle session + decoded yaw timeline directly via SQL — the same
    /// shape the repository commits (position_samples, migration 5).</summary>
    private async Task SeedDatabaseAsync(TemporaryDataRoot root)
    {
        CliRun bootstrap = await RunAsync(root, "sessions", "--limit", "1");
        Assert.AreEqual(0, bootstrap.ExitCode, bootstrap.Diagnostic);

        string databasePath = Path.Combine(root.Path, "treader.db");
        Assert.IsTrue(File.Exists(databasePath), $"database not created at {databasePath}");

        await using SqliteConnection connection = new($"Data Source={databasePath}");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText =
                """
                INSERT INTO source_artifacts
                    (id, sha256, byte_length, media_type, stored_extension, imported_at_utc, schema_version)
                VALUES
                    ($artifact, $sha, 3, 'application/octet-stream', '.bin', '2026-08-10T00:00:00Z', '1');
                INSERT INTO decode_runs
                    (id, source_artifact_id, decoder_id, decoder_version, schema_version, status,
                     capabilities, started_at_utc, completed_at_utc)
                VALUES
                    ($run, $artifact, 'wotb-11.19-strict', '0.1.0', '1', 2, 703,
                     '2026-08-10T00:00:00Z', '2026-08-10T00:00:01Z');
                INSERT INTO battle_sessions
                    (id, decode_run_id, game_version, map_name, duration_ticks, schema_version)
                VALUES
                    ($session, $run, '11.19.0', 'Dead Rail', 200000000, '1');
                """;
            command.Parameters.AddWithValue("$artifact", "019fdff8-aaaa-7426-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$run", "019fdff8-bbbb-7426-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$session", SessionId.ToString("D"));
            command.Parameters.AddWithValue("$sha", Sha256);
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        // Yaw ground truth: 0 rad until t=10s, then 1.2 rad (the step that
        // makes the memory-lead case unambiguous), sampled every second.
        await using (SqliteConnection yawConnection = new($"Data Source={databasePath}"))
        {
            await yawConnection.OpenAsync(TestContext.CancellationToken);
            await using SqliteCommand command = yawConnection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO position_samples
                    (id, battle_session_id, entity_id, sequence, replay_time_ticks,
                     raw_x, raw_y, raw_z, raw_coordinate_space,
                     yaw, pitch, roll,
                     evidence_source_artifact_id, evidence_offset, evidence_length, evidence_sha256)
                VALUES
                    ($id, $session, $entity, $seq, $ticks,
                     0.0, 0.0, 0.0, 0,
                     $yaw, 0.0, 0.0,
                     $artifact, 0, 10, $sha);
                """;
            for (int second = 0; second <= 16; second++)
            {
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", $"019fdff8-c000-0000-8547-{second:D12}");
                command.Parameters.AddWithValue("$session", SessionId.ToString("D"));
                command.Parameters.AddWithValue("$entity", Target);
                command.Parameters.AddWithValue("$seq", second + 1);
                command.Parameters.AddWithValue("$ticks", second * 10_000_000L);
                command.Parameters.AddWithValue("$yaw", PacketYawAt(second));
                command.Parameters.AddWithValue("$artifact", "019fdff8-aaaa-7426-8547-9fb8cc3eb07b");
                command.Parameters.AddWithValue("$sha", Sha256);
                await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
            }
        }
    }

    /// <summary>Packet yaw fixture: 0 rad before t=10s, 1.2 rad after.</summary>
    private static double PacketYawAt(double seconds) => seconds < 10.0 ? 0.0 : 1.2;

    private async Task<string> WriteSnapshotsAsync(
        TemporaryDataRoot root,
        params (double Seconds, double MemoryYaw)[] dumps)
    {
        string path = Path.Combine(root.Path, "yaw-snapshots.json");
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "wotbtreader.od.hp-diff.snapshots.v1");
            writer.WriteNumber("regionLength", 0x100);
            writer.WritePropertyName("snapshots");
            writer.WriteStartArray();
            foreach ((double seconds, double memoryYaw) in dumps)
            {
                byte[] bytes = new byte[0x100];
                for (int offset = 0; offset <= bytes.Length - 4; offset += 4)
                {
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(offset), 0.7f);
                }

                BinaryPrimitives.WriteSingleLittleEndian(
                    bytes.AsSpan(LiveYawOffset), (float)memoryYaw);
                writer.WriteStartObject();
                writer.WriteNumber("replayTimeSeconds", seconds);
                writer.WriteString("bytesBase64", Convert.ToBase64String(bytes));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        await File.WriteAllBytesAsync(path, stream.ToArray(), TestContext.CancellationToken);
        return path;
    }

    private async Task<CliRun> RunAsync(TemporaryDataRoot root, params string[] arguments)
    {
        StringWriter output = new();
        StringWriter error = new();
        string[] full = [.. arguments, "--json", "--data-root", root.Path];

        int exitCode = await CliEntryPoint.RunAsync(
            full,
            output,
            error,
            TestContext.CancellationToken);

        return new CliRun(exitCode, output.ToString(), error.ToString());
    }

    public TestContext TestContext { get; set; } = null!;

    private sealed record CliRun(int ExitCode, string StandardOutput, string StandardError)
    {
        public JsonElement Data { get; } =
            JsonDocument.Parse(StandardOutput).RootElement.TryGetProperty("data", out JsonElement data)
                ? data
                : default;

        public string Diagnostic =>
            $"exit={ExitCode}\nstdout={StandardOutput}\nstderr={StandardError}";
    }
}
