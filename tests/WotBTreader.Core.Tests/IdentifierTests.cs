namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class IdentifierTests
{
    [TestMethod]
    public void NewIdentifiers_UseGuidVersionSeven()
    {
        SourceArtifactId id = SourceArtifactId.New();

        Assert.AreEqual(7, id.Value.Version);
        Assert.AreNotEqual(Guid.Empty, id.Value);
    }

    [TestMethod]
    public void ContentHash_NormalizesHexToLowercase()
    {
        ContentHash hash = new(new string('A', ContentHash.Sha256HexLength));

        Assert.AreEqual(new string('a', ContentHash.Sha256HexLength), hash.Value);
    }

    [TestMethod]
    public void ContentHash_RejectsInvalidLength()
    {
        Assert.ThrowsExactly<ArgumentException>(() => new ContentHash("abcd"));
    }
}
