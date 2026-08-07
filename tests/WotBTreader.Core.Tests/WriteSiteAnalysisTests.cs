using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class WriteSiteAnalysisTests
{
    private static readonly WriteSiteModule Main = new("wotblitz.exe", 0x01000000, 0x02000000);

    [TestMethod]
    public void ResolveRip_MapsIntoContainingModule()
    {
        WriteSiteModuleResolve resolved = WriteSiteAnalysis.ResolveRip(0x01005F19, [Main]);

        Assert.AreEqual("wotblitz.exe", resolved.ModuleName);
        Assert.AreEqual(0x01000000ul, resolved.ModuleBase);
        Assert.AreEqual(0x5F19u, resolved.ModuleRva);
        Assert.AreEqual("wotblitz.exe+0x5F19", resolved.RvaLabel);
    }

    [TestMethod]
    public void ResolveRip_OutsideModules_IsJit()
    {
        WriteSiteModuleResolve resolved = WriteSiteAnalysis.ResolveRip(0x7FFE0010, [Main]);

        Assert.IsNull(resolved.ModuleName);
        Assert.IsNull(resolved.ModuleBase);
        Assert.IsNull(resolved.ModuleRva);
        Assert.AreEqual("jit", resolved.RvaLabel);
    }

    [TestMethod]
    public void ObjectBase_FromConsistentRegisterDisplacement()
    {
        // Armed float at base+0x28; ecx holds the object base across hits.
        const ulong objBase = 0x3D525BC0;
        const ulong addr = objBase + 0x28;
        var regs = new Dictionary<string, uint>
        {
            ["ecx"] = (uint)objBase,
            ["eax"] = 0x11111111,
        };

        WriteSiteAnalysisResult result = WriteSiteAnalysis.Analyze(
            [
                new WriteSiteHitInput(addr, 0x01005F19, "wotblitz.exe+0x5F19", "F30F11", regs),
                new WriteSiteHitInput(addr, 0x01005F19, "wotblitz.exe+0x5F19", "F30F11", regs),
                new WriteSiteHitInput(addr, 0x01005F19, "wotblitz.exe+0x5F19", "F30F11", regs),
            ],
            [Main]);

        Assert.HasCount(1, result.WriteSites);
        WriteSiteSummary site = result.WriteSites[0];
        Assert.AreEqual("wotblitz.exe", site.ModuleName);
        Assert.AreEqual(0x5F19u, site.ModuleRva);
        Assert.AreEqual("movss [mem],xmm", site.InstructionHint);
        Assert.IsGreaterThanOrEqualTo(1, site.ObjectBaseCandidates.Count);
        WriteSiteObjectBaseCandidate best = site.ObjectBaseCandidates[0];
        Assert.AreEqual("ecx", best.Register);
        Assert.AreEqual(0x28, best.Displacement);
        Assert.AreEqual(objBase, best.Base);
        Assert.AreEqual("member-displacement", result.ResolverHints.AddressKind);
        Assert.IsNotEmpty(result.ResolverHints.SiblingReadPlan);
        Assert.IsFalse(result.ResolverHints.SiblingReadPlan.Any(e => e.OffsetFromBase == 0x28));
    }

    [TestMethod]
    public void ObjectBase_AmbiguousEqualSupport_DoesNotForceWinner()
    {
        const ulong addr = 0x10010028;
        // Two regs both yield plausible bases with equal support.
        var regs = new Dictionary<string, uint>
        {
            ["ecx"] = 0x10010000, // disp 0x28
            ["edx"] = 0x10010020, // disp 0x08
        };

        WriteSiteAnalysisResult result = WriteSiteAnalysis.Analyze(
            [
                new WriteSiteHitInput(addr, 0x01000010, null, null, regs),
                new WriteSiteHitInput(addr, 0x01000010, null, null, regs),
            ],
            [Main]);

        // Candidates may both qualify; equal top support → kind stays unknown.
        Assert.AreEqual("unknown", result.ResolverHints.AddressKind);
        Assert.IsEmpty(result.ResolverHints.SiblingReadPlan);
    }

    [TestMethod]
    public void NoRegisters_ClassifiesHeapDynamicWhenModuleKnown()
    {
        WriteSiteAnalysisResult result = WriteSiteAnalysis.Analyze(
            [
                new WriteSiteHitInput(0x3D525BE8, 0x01005F19, null, "F30F11", null),
                new WriteSiteHitInput(0x3D525BE8, 0x01005F19, null, "F30F11", null),
            ],
            [Main]);

        Assert.AreEqual("heap-dynamic", result.ResolverHints.AddressKind);
        Assert.AreEqual("wotblitz.exe", result.WriteSites[0].ModuleName);
        Assert.IsEmpty(result.WriteSites[0].ObjectBaseCandidates);
    }

    [TestMethod]
    public void ClassifyInstructionHint_UnknownOnGarbage()
    {
        Assert.AreEqual("unknown", WriteSiteAnalysis.ClassifyInstructionHint(null));
        Assert.AreEqual("unknown", WriteSiteAnalysis.ClassifyInstructionHint("ZZ"));
        Assert.AreEqual("unknown", WriteSiteAnalysis.ClassifyInstructionHint("90"));
    }

    [TestMethod]
    public void SiblingPlan_ExcludesArmedDisplacement()
    {
        IReadOnlyList<WriteSiteSiblingPlanEntry> plan =
            WriteSiteAnalysis.BuildSiblingReadPlan(0x1000, armedDisplacement: 8, radiusBytes: 8);

        Assert.IsTrue(plan.All(e => e.OffsetFromBase != 8));
        Assert.IsTrue(plan.Any(e => e.OffsetFromBase == 0));
        Assert.IsTrue(plan.Any(e => e.OffsetFromBase == 4));
        Assert.IsTrue(plan.All(e => e.HypothesizedAxis == "unknown"));
    }
}
