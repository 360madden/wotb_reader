using System.Text.Json;
using Microsoft.Data.Sqlite;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// End-to-end proof of the <c>beacon</c> command family and its projection in
/// <c>overlay-frame</c>: a seeded database accepts persistent beacons (add /
/// list / remove round-trips across CLI invocations) and the frame preview
/// projects visible ones while filtering by the replay-time tag.
/// </summary>
[TestClass]
public sealed class CliBeaconTests
{
    private const string Sha256 = "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
    private static readonly Guid SessionId = Guid.Parse("019fe002-1111-7426-8547-9fb8cc3eb07b");

    [TestMethod]
    public async Task Beacon_AddListRemoveRoundTripsAcrossInvocations()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        string session = SessionId.ToString("D");

        CliRun added = await RunAsync(root, "beacon", "add", "FlagA", "10", "20", "30",
            "--session", session, "--color", "#00FF00");
        Assert.AreEqual(0, added.ExitCode, added.Diagnostic);

        CliRun listed = await RunAsync(root, "beacon", "list", "--session", session);
        Assert.AreEqual(0, listed.ExitCode, listed.Diagnostic);
        JsonElement beacons = listed.Data.GetProperty("beacons");
        Assert.AreEqual(1, beacons.GetArrayLength());
        Assert.AreEqual("FlagA", beacons[0].GetProperty("name").GetString());
        Assert.AreEqual(10.0, beacons[0].GetProperty("x").GetDouble(), 1e-9);
        Assert.AreEqual("#00FF00", beacons[0].GetProperty("color").GetString());

        CliRun replaced = await RunAsync(root, "beacon", "add", "FlagA", "11", "21", "31",
            "--session", session);
        Assert.AreEqual(0, replaced.ExitCode, replaced.Diagnostic);
        CliRun relisted = await RunAsync(root, "beacon", "list", "--session", session);
        Assert.AreEqual(1, relisted.Data.GetProperty("beacons").GetArrayLength());
        Assert.AreEqual(11.0, relisted.Data.GetProperty("beacons")[0].GetProperty("x").GetDouble(), 1e-9);

        CliRun removed = await RunAsync(root, "beacon", "remove", "FlagA", "--session", session);
        Assert.AreEqual(0, removed.ExitCode, removed.Diagnostic);
        CliRun empty = await RunAsync(root, "beacon", "list", "--session", session);
        Assert.AreEqual(0, empty.ExitCode, empty.Diagnostic);
        Assert.AreEqual(0, empty.Data.GetProperty("beacons").GetArrayLength());

        CliRun removeMissing = await RunAsync(root, "beacon", "remove", "FlagA", "--session", session);
        Assert.AreEqual((int)CliExitCode.InvalidArguments, removeMissing.ExitCode, removeMissing.Diagnostic);
    }

    [TestMethod]
    public async Task Beacon_RejectsMissingSessionAndBadCoordinates()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        CliRun noSession = await RunAsync(root, "beacon", "list");
        Assert.AreEqual((int)CliExitCode.InvalidArguments, noSession.ExitCode, noSession.Diagnostic);

        CliRun badCoord = await RunAsync(root, "beacon", "add", "X", "abc", "0", "0",
            "--session", SessionId.ToString("D"));
        Assert.AreEqual((int)CliExitCode.InvalidArguments, badCoord.ExitCode, badCoord.Diagnostic);

        CliRun unknownSub = await RunAsync(root, "beacon", "explode", "--session", SessionId.ToString("D"));
        Assert.AreEqual((int)CliExitCode.InvalidArguments, unknownSub.ExitCode, unknownSub.Diagnostic);
    }

    [TestMethod]
    public async Task OverlayFrame_ProjectsVisibleBeaconsAndFiltersByTimeTag()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        string session = SessionId.ToString("D");

        // Beacon in front of the camera at (0,0,100) — visible at t=5.
        CliRun addFlag = await RunAsync(root, "beacon", "add", "Flag", "0", "0", "100",
            "--session", session, "--color", "#FFD700");
        Assert.AreEqual(0, addFlag.ExitCode, addFlag.Diagnostic);
        // Beacon behind the camera — never drawn.
        CliRun addBack = await RunAsync(root, "beacon", "add", "Rear", "0", "0", "-100",
            "--session", session, "--color", "#FF0000");
        Assert.AreEqual(0, addBack.ExitCode, addBack.Diagnostic);
        // Beacon tagged to a window that excludes t=5.
        CliRun addLate = await RunAsync(root, "beacon", "add", "Late", "0", "0", "100",
            "--session", session, "--from", "60");
        Assert.AreEqual(0, addLate.ExitCode, addLate.Diagnostic);

        CliRun frame = await RunAsync(root, "overlay-frame", "5", "--session", session);
        Assert.AreEqual(0, frame.ExitCode, frame.Diagnostic);
        JsonElement beacons = frame.Data.GetProperty("beacons");
        // Flag (visible) + Rear (behind camera, screen null) — the CLI
        // reports every projection; the view-model filters by viewport.
        // "Late" is excluded by its --from 60 time tag: that is the filter
        // under test.
        Assert.AreEqual(2, beacons.GetArrayLength());
        JsonElement flag = beacons.EnumerateArray().Single(b => b.GetProperty("name").GetString() == "Flag");
        Assert.IsTrue(flag.GetProperty("screen").GetProperty("inViewport").GetBoolean());
        JsonElement rear = beacons.EnumerateArray().Single(b => b.GetProperty("name").GetString() == "Rear");
        Assert.IsFalse(rear.TryGetProperty("screen", out _));
        Assert.IsFalse(beacons.EnumerateArray().Any(b => b.GetProperty("name").GetString() == "Late"));
    }

    /// <summary>Bootstrap the database (runs migrations), then seed the
    /// session, roster, and position samples directly via SQL.</summary>
    private async Task SeedDatabaseAsync(TemporaryDataRoot root)
    {
        CliRun bootstrap = await RunAsync(root, "sessions", "--limit", "1");
        Assert.AreEqual(0, bootstrap.ExitCode, bootstrap.Diagnostic);

        string databasePath = Path.Combine(root.Path, "treader.db");
        await using SqliteConnection connection = new($"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(TestContext.CancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
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
                ($session, $run, '11.19.0', 'Oasis Palms', 1000000000, '1');
            INSERT INTO participants
                (id, battle_session_id, entity_id, team_number, player_name, tank_name,
                 tank_class, bot_status, bot_status_confidence,
                 evidence_source_artifact_id, evidence_offset, evidence_length, evidence_sha256)
            VALUES
                ($vp, $session, 1, 1, 'ViewpointPlayer', 'ViewpointTank', 3, 0, 2, $artifact, 0, 10, $sha);
            UPDATE battle_sessions SET viewpoint_participant_id = $vp WHERE id = $session;
            INSERT INTO position_samples
                (id, battle_session_id, participant_id, entity_id, sequence, replay_time_ticks,
                 raw_x, raw_y, raw_z, raw_coordinate_space, yaw, pitch, roll,
                 evidence_source_artifact_id, evidence_offset, evidence_length, evidence_sha256)
            VALUES
                ($p1, $session, $vp, 1, 1, 0,          1, 2, 3, 1, 0.5, -0.1, 0.0, $artifact, 0, 10, $sha),
                ($p2, $session, $vp, 1, 2, 50000000,   1, 2, 3, 1, 0.5, -0.1, 0.0, $artifact, 0, 10, $sha),
                ($p3, $session, $vp, 1, 3, 1000000000, 1, 2, 3, 1, 0.5, -0.1, 0.0, $artifact, 0, 10, $sha);
            """;
        command.Parameters.AddWithValue("$artifact", "019fe002-aaaa-7426-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$run", "019fe002-bbbb-7426-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$session", SessionId.ToString("D"));
        command.Parameters.AddWithValue("$sha", Sha256);
        command.Parameters.AddWithValue("$vp", "019fe002-cccc-0001-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p1", "019fe002-dddd-0001-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p2", "019fe002-dddd-0002-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p3", "019fe002-dddd-0003-8547-9fb8cc3eb07b");
        await command.ExecuteNonQueryAsync(TestContext.CancellationToken);
    }

    private async Task<CliRun> RunAsync(
        TemporaryDataRoot root,
        params string[] arguments)
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
