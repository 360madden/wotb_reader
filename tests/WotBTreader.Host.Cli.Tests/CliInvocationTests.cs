using WotBTreader.Host.Cli.Cli;

namespace WotBTreader.Host.Cli.Tests;

[TestClass]
public sealed class CliInvocationTests
{
    [TestMethod]
    public void ParseRecognizesCommandOptionsAndPositionals()
    {
        var result = CliInvocation.Parse(
            ["sessions", "--json", "--limit", "20", "--offset=5"]);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("sessions", result.Value.Command);
        Assert.IsTrue(result.Value.Json);
        Assert.AreEqual("20", result.Value.Options["limit"]);
        Assert.AreEqual("5", result.Value.Options["offset"]);
    }

    [TestMethod]
    public void ParseRejectsDuplicateOptions()
    {
        var result = CliInvocation.Parse(["doctor", "--json", "--json"]);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.option.duplicate", result.Error?.Code);
    }

    [TestMethod]
    public void ParseRejectsMissingCommand()
    {
        var result = CliInvocation.Parse([]);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("cli.command.required", result.Error?.Code);
    }
}
