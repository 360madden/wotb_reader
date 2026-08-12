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

    [TestMethod]
    public void Parse_PerDumpLagIsFlag_MemoryLeadSecondsIsValue()
    {
        // OD-RECOVERY-089: --per-dump-lag switches on the per-dump bounded
        // bidirectional lag search (no value); --memory-lead-seconds takes a
        // number. A flag must NOT consume the following token.
        var result = CliInvocation.Parse(
            ["yaw-diff", "s.json", "--per-dump-lag", "--memory-lead-seconds", "8", "--json"]);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.AreEqual("yaw-diff", result.Value.Command);
        Assert.IsTrue(result.Value.Options.ContainsKey("per-dump-lag"));
        Assert.IsNull(result.Value.Options["per-dump-lag"]);
        Assert.AreEqual("8", result.Value.Options["memory-lead-seconds"]);
        Assert.IsTrue(result.Value.Json);
    }

    [TestMethod]
    public void Parse_MemoryLeadSecondsRequiresAValue()
    {
        // OptionRequiresValue covers memory-lead-seconds: without a value the
        // option is registered with null, and the router rejects it as
        // invalid arguments — the parser must not treat the next option as
        // its value.
        var result = CliInvocation.Parse(
            ["yaw-diff", "s.json", "--memory-lead-seconds", "--json"]);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value.Options.ContainsKey("memory-lead-seconds"));
        Assert.IsNull(result.Value.Options["memory-lead-seconds"]);
        Assert.IsTrue(result.Value.Json);
    }

    [TestMethod]
    public void Parse_LagLeadSecondsRequiresAValue()
    {
        // OptionRequiresValue covers lag-lead-seconds (hp-diff lead-side
        // attribution window, OD-RECOVERY-091): without a value the option
        // is registered with null, and the router rejects it as invalid
        // arguments — the parser must not treat the next option as its
        // value.
        var result = CliInvocation.Parse(
            ["hp-diff", "s.json", "--lag-lead-seconds", "--json"]);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsNotNull(result.Value);
        Assert.IsTrue(result.Value.Options.ContainsKey("lag-lead-seconds"));
        Assert.IsNull(result.Value.Options["lag-lead-seconds"]);
        Assert.IsTrue(result.Value.Json);
    }
}
