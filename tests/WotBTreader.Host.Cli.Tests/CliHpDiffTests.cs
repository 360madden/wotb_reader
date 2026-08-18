using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// End-to-end proof of the <c>hp-diff</c> command: the snapshots file (the
/// pre-staged dump contract) + the REAL damage ground truth from a seeded
/// treader database → bucket → correlate → the hardened verdict (Lenient
/// score 1.0 + flatness 1.0 + Strict confirmation). Seeding mirrors the
/// storage compose test: kind-3 damage events with values_json carrying the
/// victim's damage amounts.
/// </summary>
[TestClass]
public sealed class CliHpDiffTests
{
    private const string Sha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly Guid SessionId = Guid.Parse("019fdff7-8dcf-7426-8547-9fb8cc3eb07b");
    private const long Victim = 7001;

    [TestMethod]
    public async Task HpDiff_IdentifiesHpField_WhenDropsMatchTheDamageTimeline()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        // The victim takes 450 at 1s and 120 at 2s (kind-3 events) plus a
        // 3s Destroyed (no damage). HP at +0x48 drops by exactly those
        // amounts and stays flat through the (2, 3] control window.
        string snapshotsPath = await WriteSnapshotsAsync(
            root,
            (0, 1000),
            (1, 550),
            (2, 430),
            (3, 430));

        CliRun run = await RunAsync(root, "hp-diff", snapshotsPath,
            "--session", SessionId.ToString("D"), "--victim", Victim.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        JsonElement data = run.Data;
        Assert.IsTrue(data.GetProperty("verdict").GetProperty("hit").GetBoolean(), run.Diagnostic);
        Assert.AreEqual(
            0x48,
            data.GetProperty("topCandidate").GetProperty("offset").GetInt32());
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("score").GetDouble(), 1e-9);
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("flatness").GetDouble(), 1e-9);
        Assert.AreEqual(
            2,
            data.GetProperty("strictConfirmation").GetProperty("matchedDamageWindows").GetInt32());
    }

    [TestMethod]
    public async Task HpDiff_IncrementDirection_IdentifiesDamageDealtField_WhenRisesMatchTheTimeline()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        // Re-point the seeded events at the target as the ATTACKER (the
        // increment direction keys on attackerEntityId): the target deals 450
        // at 1s and 120 at 2s; the 3s Destroyed carries no damage.
        await using (SqliteConnection connection =
                     new($"Data Source={Path.Combine(root.Path, "treader.db")};Pooling=False"))
        {
            await connection.OpenAsync(TestContext.CancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE canonical_events
                SET values_json = CASE id
                    WHEN $event1 THEN '{"attackerEntityId":7001,"victimEntityId":7002,"damage":450}'
                    WHEN $event2 THEN '{"attackerEntityId":7001,"victimEntityId":7003,"damage":120}'
                    ELSE values_json
                END;
                """;
            command.Parameters.AddWithValue("$event1", "019fdff7-cccc-0001-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$event2", "019fdff7-cccc-0002-8547-9fb8cc3eb07b");
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }

        // The scoreboard damage-dealt counter at +0x48 RISES by exactly those
        // amounts and stays flat through the (2, 3] control window.
        string snapshotsPath = await WriteSnapshotsAsync(
            root,
            (0, 0),
            (1, 450),
            (2, 570),
            (3, 570));

        CliRun run = await RunAsync(root, "hp-diff", snapshotsPath,
            "--session", SessionId.ToString("D"),
            "--victim", Victim.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--direction", "increment");

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        JsonElement data = run.Data;
        Assert.AreEqual("increment", data.GetProperty("direction").GetString());
        Assert.IsTrue(data.GetProperty("verdict").GetProperty("hit").GetBoolean(), run.Diagnostic);
        Assert.AreEqual(
            0x48,
            data.GetProperty("topCandidate").GetProperty("offset").GetInt32());
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("score").GetDouble(), 1e-9);
        Assert.AreEqual(1.0, data.GetProperty("topCandidate").GetProperty("flatness").GetDouble(), 1e-9);
        Assert.AreEqual(
            2,
            data.GetProperty("strictConfirmation").GetProperty("matchedDamageWindows").GetInt32());
    }

    [TestMethod]
    public async Task HpDiff_IncrementDirection_RejectsUnknownDirectionValue()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        string snapshotsPath = await WriteSnapshotsAsync(root, (0, 1000), (1, 1000));

        CliRun run = await RunAsync(root, "hp-diff", snapshotsPath,
            "--session", SessionId.ToString("D"),
            "--victim", Victim.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "--direction", "sideways");

        Assert.AreEqual((int)CliExitCode.InvalidArguments, run.ExitCode, run.Diagnostic);
    }

    [TestMethod]
    public async Task HpDiff_ReportsNoHit_WhenNoFieldDropsMatch()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        // Same damage ground truth, but the region never changes: no candidate
        // can match, and the verdict must be honest-negative.
        string snapshotsPath = await WriteSnapshotsAsync(
            root,
            (0, 1000),
            (1, 1000),
            (2, 1000),
            (3, 1000));

        CliRun run = await RunAsync(root, "hp-diff", snapshotsPath,
            "--session", SessionId.ToString("D"), "--victim", Victim.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        Assert.IsFalse(run.Data.GetProperty("verdict").GetProperty("hit").GetBoolean());
        // With no matching candidate, topCandidate is omitted from the
        // envelope (null values are not written).
        Assert.IsFalse(run.Data.TryGetProperty("topCandidate", out _));
    }

    [TestMethod]
    public async Task HpDiff_RejectsMalformedSnapshotsFile()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        string snapshotsPath = Path.Combine(root.Path, "bad-snapshots.json");
        await File.WriteAllTextAsync(snapshotsPath, "{ not json", TestContext.CancellationToken);

        CliRun run = await RunAsync(root, "hp-diff", snapshotsPath,
            "--session", SessionId.ToString("D"), "--victim", Victim.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // The snapshots file is an INPUT error, not an argument error -
        // MapExitCode routes cli.hp-diff.snapshots.* to InvalidInput.
        Assert.AreEqual(
            (int)CliExitCode.InvalidInput,
            run.ExitCode,
            run.Diagnostic);
    }

    /// <summary>Bootstrap the database (runs migrations), then seed the HP
    /// ground-truth rows directly via SQL — the same shape the compose test
    /// commits through the repository.</summary>
    private async Task SeedDatabaseAsync(TemporaryDataRoot root)
    {
        CliRun bootstrap = await RunAsync(root, "sessions", "--limit", "1");
        Assert.AreEqual(0, bootstrap.ExitCode, bootstrap.Diagnostic);

        string databasePath = Path.Combine(root.Path, "treader.db");
        Assert.IsTrue(File.Exists(databasePath), $"database not created at {databasePath}");

        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
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
                    ($session, $run, '11.19.0', 'savanna', 600000000, '1');
                INSERT INTO canonical_events
                    (id, decode_run_id, battle_session_id, sequence, kind, replay_time_ticks,
                     entity_id, values_json, confidence, evidence_source_artifact_id,
                     evidence_offset, evidence_length, evidence_sha256)
                VALUES
                    ($event1, $run, $session, 1, 3, 10000000, $victim,
                     '{"attackerEntityId":7002,"victimEntityId":7001,"damage":450}', 2,
                     $artifact, 0, 10, $sha),
                    ($event2, $run, $session, 2, 3, 20000000, $victim,
                     '{"attackerEntityId":7003,"victimEntityId":7001,"damage":120}', 2,
                     $artifact, 0, 10, $sha),
                    ($event3, $run, $session, 3, 6, 30000000, $victim,
                     '{"attackerEntityId":7004,"victimEntityId":7001}', 2,
                     $artifact, 0, 10, $sha);
                """;
            command.Parameters.AddWithValue("$artifact", "019fdff7-aaaa-7426-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$run", "019fdff7-bbbb-7426-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$session", SessionId.ToString("D"));
            command.Parameters.AddWithValue("$sha", Sha256);
            command.Parameters.AddWithValue("$victim", Victim);
            command.Parameters.AddWithValue("$event1", "019fdff7-cccc-0001-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$event2", "019fdff7-cccc-0002-8547-9fb8cc3eb07b");
            command.Parameters.AddWithValue("$event3", "019fdff7-cccc-0003-8547-9fb8cc3eb07b");
            await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
        }
    }

    private async Task<string> WriteSnapshotsAsync(
        TemporaryDataRoot root,
        params (double Seconds, int Hp)[] dumps)
    {
        string path = Path.Combine(root.Path, "hp-snapshots.json");
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "wotbtreader.od.hp-diff.snapshots.v1");
            writer.WriteNumber("regionLength", 0x100);
            writer.WritePropertyName("snapshots");
            writer.WriteStartArray();
            foreach ((double seconds, int hp) in dumps)
            {
                byte[] bytes = new byte[0x100];
                BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(0x48), hp);
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
        // Failed commands (or null data) omit the "data" key; expose it as
        // Undefined so assertions can distinguish "no data" from a value.
        public JsonElement Data { get; } =
            JsonDocument.Parse(StandardOutput).RootElement.TryGetProperty("data", out JsonElement data)
                ? data
                : default;

        public string Diagnostic =>
            $"exit={ExitCode}\nstdout={StandardOutput}\nstderr={StandardError}";
    }
}
