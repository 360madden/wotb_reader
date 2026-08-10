using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using WotBTreader.Application.Replay;
using WotBTreader.Core;

namespace WotBTreader.Replays.Tests;

/// <summary>
/// SCRATCH PROBE (not part of the committed suite contract): re-scans a REAL
/// 11.19 replay's decrypted event stream and dumps the 25-byte tail of every
/// type-10 position packet (the decoder persists only x/y/z, so the rotation
/// candidate bytes are only visible at scan time). The dump goes to a JSON
/// file for offline analysis. Skipped (Inconclusive) when the artifact path
/// env var is absent, so the suite stays green without local replay data.
/// </summary>
[TestClass]
public sealed class PositionPacketTailProbeTests
{
    [TestMethod]
    public async Task DumpPositionPacketTails_ForOfflineAnalysis()
    {
        string? artifactPath = Environment.GetEnvironmentVariable("WOTB_PROBE_ARTIFACT");
        string? outputPath = Environment.GetEnvironmentVariable("WOTB_PROBE_OUTPUT");
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            Assert.Inconclusive("WOTB_PROBE_ARTIFACT not set; scratch probe skipped.");
        }

        string repoRoot = FindRepoRoot();
        artifactPath = Path.GetFullPath(
            Path.IsPathRooted(artifactPath) ? artifactPath : Path.Combine(repoRoot, artifactPath));
        outputPath = Path.GetFullPath(
            string.IsNullOrWhiteSpace(outputPath)
                ? Path.Combine(repoRoot, ".data", "position-packet-tails.json")
                : (Path.IsPathRooted(outputPath) ? outputPath : Path.Combine(repoRoot, outputPath)));

        ReplayInput input = CreateInput(artifactPath);
        DecoderLimits limits = DecoderLimits.Default;
        CancellationToken token = TestContext.CancellationToken;

        ValidatedReplayArchive archive =
            await ReplayArchiveReader.ReadAsync(input, limits, token).ConfigureAwait(false);
        WotbReplayMetadata metadata =
            WotbReplayMetadata.Parse(archive[ReplayFormatConstants.MetadataEntry], limits);
        EventStreamScan eventStream = EventStreamReader.Scan(
            archive[ReplayFormatConstants.EventStreamEntry],
            limits,
            metadata.Duration,
            token);

        using MemoryStream stream = new();
        int type10 = 0;
        using (Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("gameVersion", metadata.Version);
            writer.WriteNumber("packets", eventStream.Packets.Count);
            writer.WritePropertyName("positionPackets");
            writer.WriteStartArray();
            foreach (EventPacket packet in eventStream.Packets)
            {
                if (packet.Type != 10 || packet.Payload.Length != 49)
                {
                    continue;
                }

                type10++;
                ReadOnlySpan<byte> payload = packet.Payload.Span;
                writer.WriteStartObject();
                writer.WriteNumber("ordinal", packet.Ordinal);
                writer.WriteNumber("clockSeconds", packet.ClockSeconds);
                writer.WriteNumber("entityId", BinaryPrimitives.ReadInt32LittleEndian(payload));
                writer.WriteNumber("spaceId", BinaryPrimitives.ReadInt32LittleEndian(payload[4..]));
                writer.WriteNumber("vehicleId", BinaryPrimitives.ReadInt32LittleEndian(payload[8..]));
                writer.WriteNumber("x", BinaryPrimitives.ReadSingleLittleEndian(payload[12..]));
                writer.WriteNumber("y", BinaryPrimitives.ReadSingleLittleEndian(payload[16..]));
                writer.WriteNumber("z", BinaryPrimitives.ReadSingleLittleEndian(payload[20..]));
                writer.WriteString("tailHex", Convert.ToHexString(payload[24..]));
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();

            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, stream.ToArray(), token).ConfigureAwait(false);
        TestContext.WriteLine($"Wrote {type10} type-10 packets to {outputPath}");
    }

    private static ReplayInput CreateInput(string artifactPath)
    {
        FileInfo info = new(artifactPath);
        string sha256 = Convert.ToHexString(
            SHA256.HashData(File.ReadAllBytes(artifactPath))).ToLowerInvariant();
        SourceArtifact artifact = new(
            SourceArtifactId.New(),
            new ContentHash(sha256),
            info.Length,
            "application/octet-stream",
            ".wotbreplay",
            DateTimeOffset.UtcNow,
            "1");
        return new ReplayInput(
            artifact,
            token => new ValueTask<Stream>(File.OpenRead(artifactPath)));
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null &&
               !File.Exists(Path.Combine(directory.FullName, "WotBTreader.sln")) &&
               !File.Exists(Path.Combine(directory.FullName, "validate.ps1")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }

    public TestContext TestContext { get; set; } = null!;
}
