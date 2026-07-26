using System.Text;
using WotBTreader.Application.Results;
using WotBTreader.GameIntegration.Dvpl;

namespace WotBTreader.GameIntegration.Tests;

[TestClass]
public sealed class DvplReaderTests
{
    [TestMethod]
    public async Task ReadAsync_ValidRawPayload_ReturnsValidatedBytesAndHashes()
    {
        using TemporaryDirectory temporary = new();
        byte[] expected = Encoding.UTF8.GetBytes("bounded resource");
        string path = temporary.GetPath("raw.dvpl");
        DvplTestData.Write(path, expected);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess);
        CollectionAssert.AreEqual(expected, result.Value!.Data.ToArray());
        Assert.AreEqual(DvplCompressionMode.None, result.Value.Footer.CompressionMode);
        Assert.AreEqual(expected.Length, result.Value.Footer.OriginalSize);
        Assert.AreEqual(expected.Length, result.Value.Footer.StoredSize);
        Assert.AreEqual(64, result.Value.SourceHash.Value.Length);
        Assert.AreEqual(64, result.Value.PayloadHash.Value.Length);
    }

    [TestMethod]
    [DataRow(DvplCompressionMode.Lz4)]
    [DataRow(DvplCompressionMode.Lz4HighCompression)]
    public async Task ReadAsync_ValidLz4Modes_ReturnsOriginalPayload(DvplCompressionMode mode)
    {
        using TemporaryDirectory temporary = new();
        byte[] expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("vehicle-data-", 500)));
        string path = temporary.GetPath($"{mode}.dvpl");
        DvplTestData.Write(path, expected, mode);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        CollectionAssert.AreEqual(expected, result.Value!.Data.ToArray());
        Assert.AreEqual(mode, result.Value.Footer.CompressionMode);
    }

    [TestMethod]
    public async Task ReadAsync_CrcMismatch_IsRejectedBeforeDecode()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("crc.dvpl");
        DvplTestData.Write(
            path,
            Encoding.UTF8.GetBytes("checksum"),
            DvplCompressionMode.Lz4,
            crcOverride: 0);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.crc_mismatch", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_TruncatedFooter_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("truncated.dvpl");
        await File.WriteAllBytesAsync(path, new byte[19]);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.truncated", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_InvalidFooterMagic_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("magic.dvpl");
        DvplTestData.Write(
            path,
            "content"u8,
            magic: "NOPE"u8);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.invalid_magic", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_StoredSizeMismatch_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("stored-size.dvpl");
        DvplTestData.Write(path, "content"u8, storedSizeOverride: 100);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.size_mismatch", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_RawOriginalSizeMismatch_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("original-size.dvpl");
        DvplTestData.Write(path, "content"u8, originalSizeOverride: 100);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.size_mismatch", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_UnknownCompressionMode_IsRejected()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("mode.dvpl");
        DvplTestData.Write(path, "content"u8, modeOverride: 99);

        OperationResult<DvplPayload> result =
            await CreateReader().ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.unsupported_compression", result.Error!.Code);
    }

    [TestMethod]
    public async Task ReadAsync_DeclaredOutputAboveLimit_IsRejectedWithoutAllocation()
    {
        using TemporaryDirectory temporary = new();
        string path = temporary.GetPath("limit.dvpl");
        DvplTestData.Write(
            path,
            "content"u8,
            DvplCompressionMode.Lz4,
            originalSizeOverride: 1025);

        OperationResult<DvplPayload> result =
            await CreateReader(maxOutputBytes: 1024).ReadAsync(path, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("game.dvpl.output_limit", result.Error!.Code);
    }

    private static DvplReader CreateReader(int maxOutputBytes = 1024 * 1024) =>
        new(
            new GameIntegrationOptions
            {
                UseDefaultDiscoveryRoots = false,
                MaxDvplStoredBytes = maxOutputBytes,
                MaxDvplOutputBytes = maxOutputBytes,
            });
}
