using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class PenValidationTests
{
    // The nominal Churchill-like armor: front only (side/rear unknown → 0).
    private static readonly TankArmor Armor = new(FrontMm: 93.4, SideMm: 0, RearMm: 0);

    [TestMethod]
    public void Score_EmptyInput_ZeroReport()
    {
        PenValidationReport report = PenValidation.Score([]);

        Assert.AreEqual(0, report.TotalShots);
        Assert.AreEqual(0, report.PredictedRicochet);
        Assert.AreEqual(0, report.ClassifiedShots);
        Assert.AreEqual(0.0, report.RicochetPrecision, 1e-9);
        Assert.AreEqual(0.0, report.BandAccuracy, 1e-9);
        Assert.IsEmpty(report.Rows);
    }

    [TestMethod]
    public void Score_HeadOnPenetrating_Agrees()
    {
        // Head-on front hit with a shell that comfortably pens: no ricochet,
        // Pen band, and the decoded outcome is penetrating — full agreement.
        ScoredShot shot = new(
            new AimRay(0, 0, 100, 0, 0, -1),
            Tank(1, 0, 0, yaw: 0),
            Parts(FrontPlateMesh()),
            Armor,
            new ShellSpec(200, 100),
            Penetrated: true);

        PenValidationReport report = PenValidation.Score([shot]);

        Assert.AreEqual(1, report.TotalShots);
        Assert.AreEqual(0, report.PredictedRicochet);
        Assert.AreEqual(0.0, report.RicochetPrecision, 1e-9);
        Assert.AreEqual(1, report.ClassifiedShots);
        Assert.AreEqual(1, report.BandAgreements);
        Assert.AreEqual(1.0, report.BandAccuracy, 1e-9);
        PenValidationShotRow row = report.Rows.Single();
        Assert.IsFalse(row.PredictedRicochet);
        Assert.AreEqual(PenetrationBand.Pen, row.Band);
        Assert.IsTrue(row.Penetrated);
    }

    [TestMethod]
    public void Score_RicochetNonPenetrating_Agrees()
    {
        // A ~70 deg sloped front plate (no overmatch) ricochets, and the
        // decode says the shot did NOT penetrate — ricochet + band agree.
        ScoredShot shot = new(
            new AimRay(0, 0, 100, 0, 0, -1),
            Tank(1, 0, 0, yaw: 0),
            Parts(RicochetPlateMesh()),
            Armor,
            new ShellSpec(1000, 100),
            Penetrated: false);

        PenValidationReport report = PenValidation.Score([shot]);

        Assert.AreEqual(1, report.TotalShots);
        Assert.AreEqual(1, report.PredictedRicochet);
        Assert.AreEqual(1, report.RicochetAgreements);
        Assert.AreEqual(1.0, report.RicochetPrecision, 1e-9);
        Assert.AreEqual(1, report.ClassifiedShots);
        Assert.AreEqual(1, report.BandAgreements);
        Assert.AreEqual(1.0, report.BandAccuracy, 1e-9);
        PenValidationShotRow row = report.Rows.Single();
        Assert.IsTrue(row.PredictedRicochet);
        Assert.AreEqual(PenetrationBand.NoPen, row.Band);
        Assert.IsFalse(row.Penetrated);
    }

    [TestMethod]
    public void Score_RicochetButPenetrating_Disagrees()
    {
        // The model predicts a ricochet (NoPen) but the decode says the shot
        // PENETRATED — a genuine disagreement that drops both agreement rates.
        ScoredShot shot = new(
            new AimRay(0, 0, 100, 0, 0, -1),
            Tank(1, 0, 0, yaw: 0),
            Parts(RicochetPlateMesh()),
            Armor,
            new ShellSpec(1000, 100),
            Penetrated: true);

        PenValidationReport report = PenValidation.Score([shot]);

        Assert.AreEqual(1, report.PredictedRicochet);
        Assert.AreEqual(0, report.RicochetAgreements);
        Assert.AreEqual(0.0, report.RicochetPrecision, 1e-9);
        Assert.AreEqual(1, report.ClassifiedShots);
        Assert.AreEqual(0, report.BandAgreements);
        Assert.AreEqual(0.0, report.BandAccuracy, 1e-9);
    }

    [TestMethod]
    public void Score_MixedShots_ComputesRates()
    {
        ScoredShot[] shots =
        [
            // Ricochet, non-penetrating (agreement).
            new(new AimRay(0, 0, 100, 0, 0, -1), Tank(1, 0, 0, yaw: 0),
                Parts(RicochetPlateMesh()), Armor, new ShellSpec(1000, 100), Penetrated: false),
            // Ricochet, penetrating (disagreement).
            new(new AimRay(0, 0, 100, 0, 0, -1), Tank(1, 0, 0, yaw: 0),
                Parts(RicochetPlateMesh()), Armor, new ShellSpec(1000, 100), Penetrated: true),
            // Head-on pen (agreement).
            new(new AimRay(0, 0, 100, 0, 0, -1), Tank(1, 0, 0, yaw: 0),
                Parts(FrontPlateMesh()), Armor, new ShellSpec(200, 100), Penetrated: true),
        ];

        PenValidationReport report = PenValidation.Score(shots);

        Assert.AreEqual(3, report.TotalShots);
        Assert.AreEqual(2, report.PredictedRicochet);
        Assert.AreEqual(1, report.RicochetAgreements);
        Assert.AreEqual(0.5, report.RicochetPrecision, 1e-9);
        Assert.AreEqual(3, report.ClassifiedShots);
        Assert.AreEqual(2, report.BandAgreements);
        Assert.AreEqual(2.0 / 3.0, report.BandAccuracy, 1e-9);
        Assert.HasCount(3, report.Rows);
    }

    [TestMethod]
    public void Score_NoHit_UnknownRow_Unclassified()
    {
        // An aim that points AWAY from the tank misses every collision part
        // and resolves to Unknown: it must not count as a ricochet or a band
        // prediction.
        ScoredShot shot = new(
            new AimRay(0, 0, 100, 0, 0, 1),
            Tank(1, 0, 0, yaw: 0),
            Parts(FrontPlateMesh()),
            Armor,
            new ShellSpec(200, 100),
            Penetrated: false);

        PenValidationReport report = PenValidation.Score([shot]);

        Assert.AreEqual(1, report.TotalShots);
        Assert.AreEqual(0, report.PredictedRicochet);
        Assert.AreEqual(0, report.ClassifiedShots);
        Assert.AreEqual(PenetrationBand.Unknown, report.Rows.Single().Band);
    }

    // ---- Fixtures (mirror PenetrationAimTests' mesh-space conventions) ----

    private static OverlayTankState Tank(long id, double x, double z, double? yaw) =>
        new(id, x, 0, z, yaw, HpFraction: 1.0, Alive: true, TeamNumber: null,
            PlayerName: null, ClanTag: null, TankName: null, TankClass: null,
            DistanceMeters: 0);

    private static CollisionMeshPart[] Parts(CollisionMesh mesh) =>
        [new CollisionMeshPart(1, mesh)];

    private static CollisionMesh Mesh(params CollisionVertex[] vertices) =>
        new(vertices, [0, 1, 2]);

    // A vertical FRONT plate in mesh Z-up space (normal +Y): a head-on -Y ray
    // hits at 0 deg incidence (best penetration).
    private static CollisionMesh FrontPlateMesh() => Mesh(
        new CollisionVertex(-1, 1, -1, 0, 1, 0),
        new CollisionVertex(1, 1, -1, 0, 1, 0),
        new CollisionVertex(0, 1, 1, 0, 1, 0));

    // A steeply sloped FRONT plate whose geometric normal is ~70 deg from the
    // forward +Y axis: a head-on -Y ray hits at ~70 deg incidence and, without
    // an overmatch, ricochets (NoPen).
    private static CollisionMesh RicochetPlateMesh() => Mesh(
        new CollisionVertex(-1, 1, -0.18, 0, 0.34, 0.94),
        new CollisionVertex(1, 1, -0.18, 0, 0.34, 0.94),
        new CollisionVertex(0, 0, 0.18, 0, 0.34, 0.94));
}
