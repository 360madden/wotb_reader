using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class PenetrationAssessmentTests
{
    [TestMethod]
    public void NotReady_DeduplicatesReasonsAndForbidsBadge()
    {
        PenetrationAssessment assessment = PenetrationAssessment.NotReady(
            PenetrationReadinessReason.OwnTeamUnknown,
            [
                PenetrationReadinessReason.TargetTeamUnknown,
                PenetrationReadinessReason.OwnTeamUnknown,
                PenetrationReadinessReason.None,
            ]);

        Assert.AreEqual(PenetrationAssessmentStatus.NotReady, assessment.Status);
        Assert.AreEqual(PenetrationReadinessReason.OwnTeamUnknown, assessment.PrimaryReason);
        CollectionAssert.AreEqual(
            new[] { PenetrationReadinessReason.OwnTeamUnknown, PenetrationReadinessReason.TargetTeamUnknown },
            assessment.Reasons.ToArray());
        Assert.IsNull(assessment.Badge);
    }

    [TestMethod]
    public void NotReady_NoneReason_Throws()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => PenetrationAssessment.NotReady(PenetrationReadinessReason.None));
    }

    [TestMethod]
    public void Ready_DeterminateBadge_HasNoBlockingReasons()
    {
        var badge = new PenetrationBadge(
            42,
            StruckFace.Front,
            new PenetrationVerdict(
                PenetrationBand.Pen,
                HitDistance: 10,
                IncidenceRadians: 0,
                EffectiveArmorMm: 100,
                PenetrationMmAtRange: 120,
                Ricochet: false));

        PenetrationAssessment assessment = PenetrationAssessment.Ready(badge);

        Assert.AreEqual(PenetrationAssessmentStatus.Ready, assessment.Status);
        Assert.AreEqual(PenetrationReadinessReason.None, assessment.PrimaryReason);
        Assert.IsEmpty(assessment.Reasons);
        Assert.AreEqual(badge, assessment.Badge);
    }

    [TestMethod]
    public void Ready_UnknownBadge_Throws()
    {
        var badge = new PenetrationBadge(
            42,
            StruckFace.Unknown,
            new PenetrationVerdict(
                PenetrationBand.Unknown,
                HitDistance: null,
                IncidenceRadians: null,
                EffectiveArmorMm: null,
                PenetrationMmAtRange: null,
                Ricochet: false));

        Assert.ThrowsExactly<ArgumentException>(() => PenetrationAssessment.Ready(badge));
    }
}
