using System.Text.Json;
using Microsoft.Data.Sqlite;
using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

/// <summary>
/// End-to-end proof of the <c>overlay-frame</c> command: a seeded treader
/// database (session + roster + position samples with packet yaw) renders
/// one frame — viewpoint camera with rotation, roster tanks with projected
/// screen pixels, and honest fail-closed behavior for tanks without
/// position evidence at the frame time.
/// </summary>
[TestClass]
public sealed class CliOverlayFrameTests
{
    private const string Sha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly Guid SessionId = Guid.Parse("019fe001-1111-7426-8547-9fb8cc3eb07b");

    [TestMethod]
    public async Task OverlayFrame_RendersCameraAndTanksWithScreenPixels()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        CliRun run = await RunAsync(root, "overlay-frame", "5",
            "--session", SessionId.ToString("D"));

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        JsonElement data = run.Data;
        JsonElement camera = data.GetProperty("camera");
        Assert.AreEqual(0.5, camera.GetProperty("yawRadians").GetDouble(), 1e-9);
        Assert.AreEqual(-0.1, camera.GetProperty("pitchRadians").GetDouble(), 1e-9);

        JsonElement tanks = data.GetProperty("tanks");
        // 3 roster tanks; the late tank has no sample at/before 5s and is
        // omitted even though it is a participant.
        Assert.AreEqual(2, tanks.GetArrayLength());
        // Viewpoint tank (entity 1, at the camera) sorts first; its screen
        // projection is null because it IS the camera (depth ~0).
        Assert.AreEqual(1, tanks[0].GetProperty("entityId").GetInt64());
        Assert.IsFalse(tanks[0].TryGetProperty("screen", out _));
        // The enemy's nearest sample at 5s is the t=0 (0, 0, 100) one:
        // distance from the camera at (1, 2, 3) is sqrt(1 + 4 + 97^2).
        JsonElement enemy = tanks[1];
        Assert.AreEqual(2, enemy.GetProperty("entityId").GetInt64());
        Assert.AreEqual(2, enemy.GetProperty("teamNumber").GetInt32());
        Assert.AreEqual("EnemyTank", enemy.GetProperty("tankName").GetString());
        Assert.AreEqual(Math.Sqrt(1 + 4 + 97 * 97), enemy.GetProperty("distanceMeters").GetDouble(), 1e-6);
        Assert.IsTrue(enemy.GetProperty("screen").GetProperty("inViewport").GetBoolean());
    }

    [TestMethod]
    public async Task OverlayFrame_TankWithoutPositionEvidenceAtFrameTime_IsOmitted()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);
        // Frame time 60s: the late tank's only sample is at 100s, so it is
        // omitted (fail-closed) while the viewpoint and enemy stay.
        CliRun run = await RunAsync(root, "overlay-frame", "60",
            "--session", SessionId.ToString("D"));

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        JsonElement tanks = run.Data.GetProperty("tanks");
        Assert.AreEqual(2, tanks.GetArrayLength());
        Assert.IsFalse(JsonDocument.Parse(tanks.ToString()).RootElement
            .EnumerateArray().Any(tank => tank.GetProperty("entityId").GetInt64() == 3));
    }

    [TestMethod]
    public async Task OverlayFrame_RejectsMissingSessionAndBadTime()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        CliRun noSession = await RunAsync(root, "overlay-frame", "5");
        Assert.AreEqual((int)CliExitCode.InvalidArguments, noSession.ExitCode, noSession.Diagnostic);

        CliRun badTime = await RunAsync(root, "overlay-frame", "abc",
            "--session", SessionId.ToString("D"));
        Assert.AreEqual((int)CliExitCode.InvalidArguments, badTime.ExitCode, badTime.Diagnostic);
    }

    [TestMethod]
    public async Task OverlayFrame_WithPng_WritesValidPngFile()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        string pngPath = Path.Combine(root.Path, "frame.png");
        CliRun run = await RunAsync(root, "overlay-frame", "5",
            "--session", SessionId.ToString("D"),
            "--png", pngPath);

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        Assert.IsTrue(File.Exists(pngPath), "--png should write the preview file.");
        byte[] png = await File.ReadAllBytesAsync(pngPath, TestContext.CancellationToken);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png.Take(8).ToArray());
        Assert.IsGreaterThan(40, png.Length, "PNG should contain more than the signature.");
        Assert.AreEqual(Path.GetFullPath(pngPath),
            run.Data.GetProperty("pngPath").GetString());
    }

    [TestMethod]
    public async Task OverlayStrip_WritesContactSheetPng()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        string pngPath = Path.Combine(root.Path, "strip.png");
        CliRun run = await RunAsync(root, "overlay-strip", "0", "100", "4",
            "--session", SessionId.ToString("D"),
            "--png", pngPath);

        Assert.AreEqual(0, run.ExitCode, run.Diagnostic);
        Assert.IsTrue(File.Exists(pngPath), "--png should write the contact sheet.");
        byte[] png = await File.ReadAllBytesAsync(pngPath, TestContext.CancellationToken);
        CollectionAssert.AreEqual(
            new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A },
            png.Take(8).ToArray());
        // 2x2 grid of 640x360 cells + margins/gutters.
        Assert.AreEqual(1320u, ReadBe(png, 8 + 8));
        Assert.AreEqual(760u, ReadBe(png, 8 + 12));
        Assert.AreEqual(4, run.Data.GetProperty("count").GetInt32());
        Assert.AreEqual(2, run.Data.GetProperty("columns").GetInt32());
    }

    [TestMethod]
    public async Task OverlayStrip_RejectsBadArguments()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        CliRun noPng = await RunAsync(root, "overlay-strip", "0", "100", "4",
            "--session", SessionId.ToString("D"));
        Assert.AreEqual((int)CliExitCode.InvalidArguments, noPng.ExitCode, noPng.Diagnostic);

        CliRun badCount = await RunAsync(root, "overlay-strip", "0", "100", "0",
            "--session", SessionId.ToString("D"), "--png", "x.png");
        Assert.AreEqual((int)CliExitCode.InvalidArguments, badCount.ExitCode, badCount.Diagnostic);
    }

    private static uint ReadBe(byte[] buffer, int offset) =>
        ((uint)buffer[offset] << 24)
        | ((uint)buffer[offset + 1] << 16)
        | ((uint)buffer[offset + 2] << 8)
        | buffer[offset + 3];

    [TestMethod]
    public async Task OverlayFrame_RejectsEmptyPngPath()
    {
        using TemporaryDataRoot root = new();
        await SeedDatabaseAsync(root);

        CliRun run = await RunAsync(root, "overlay-frame", "5",
            "--session", SessionId.ToString("D"),
            "--png");
        Assert.AreEqual((int)CliExitCode.InvalidArguments, run.ExitCode, run.Diagnostic);
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
                ($session, $run, '11.19.0', 'savanna', 1000000000, '1');
            INSERT INTO participants
                (id, battle_session_id, entity_id, team_number, player_name, tank_name,
                 tank_class, bot_status, bot_status_confidence,
                 evidence_source_artifact_id, evidence_offset, evidence_length, evidence_sha256)
            VALUES
                ($vp, $session, 1, 1, 'ViewpointPlayer', 'ViewpointTank', 3, 0, 2, $artifact, 0, 10, $sha),
                ($enemy, $session, 2, 2, 'EnemyPlayer', 'EnemyTank', 3, 0, 2, $artifact, 0, 10, $sha),
                ($late, $session, 3, 2, 'LatePlayer', 'LateTank', 3, 0, 2, $artifact, 0, 10, $sha);
            UPDATE battle_sessions SET viewpoint_participant_id = $vp WHERE id = $session;
            INSERT INTO position_samples
                (id, battle_session_id, participant_id, entity_id, sequence, replay_time_ticks,
                 raw_x, raw_y, raw_z, raw_coordinate_space, yaw, pitch, roll,
                 evidence_source_artifact_id, evidence_offset, evidence_length, evidence_sha256)
            VALUES
                ($p1, $session, $vp, 1, 1, 0,          1, 2, 3, 1, 0.5, -0.1, 0.0, $artifact, 0, 10, $sha),
                ($p2, $session, $vp, 1, 2, 50000000,   1, 2, 3, 1, 0.5, -0.1, 0.0, $artifact, 0, 10, $sha),
                ($p3, $session, $enemy, 2, 3, 0,          0, 0, 100, 1, 0.0, 0.0, 0.0, $artifact, 0, 10, $sha),
                ($p4, $session, $enemy, 2, 4, 1000000000, 0, 0, 100, 1, 0.0, 0.0, 0.0, $artifact, 0, 10, $sha),
                ($p5, $session, $late, 3, 5, 1000000000, 0, 0, 200, 1, 0.0, 0.0, 0.0, $artifact, 0, 10, $sha);
            """;
        command.Parameters.AddWithValue("$artifact", "019fe001-aaaa-7426-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$run", "019fe001-bbbb-7426-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$session", SessionId.ToString("D"));
        command.Parameters.AddWithValue("$sha", Sha256);
        command.Parameters.AddWithValue("$vp", "019fe001-cccc-0001-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$enemy", "019fe001-cccc-0002-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$late", "019fe001-cccc-0003-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p1", "019fe001-dddd-0001-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p2", "019fe001-dddd-0002-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p3", "019fe001-dddd-0003-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p4", "019fe001-dddd-0004-8547-9fb8cc3eb07b");
        command.Parameters.AddWithValue("$p5", "019fe001-dddd-0005-8547-9fb8cc3eb07b");
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
