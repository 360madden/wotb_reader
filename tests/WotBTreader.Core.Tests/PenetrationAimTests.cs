using WotBTreader.Core.Overlay;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class PenetrationAimTests
{
    private static OverlayCamera Camera(double yaw = 0, double pitch = 0, double x = 0, double z = -100) =>
        new(x, 0, z, yaw, pitch, null);

    private static OverlayTankState Tank(long id, double x, double z, double? yaw = 0) =>
        new(id, x, 0, z, yaw, HpFraction: 1.0, Alive: true, TeamNumber: 1,
            PlayerName: null, ClanTag: null, TankName: null, TankClass: null,
            DistanceMeters: 0);

    private static readonly TankArmor Armor = new(FrontMm: 93.4, SideMm: 53.4, RearMm: 40);

    [TestMethod]
    public void BuildAimRay_Yaw0FacesPositiveZ()
    {
        AimRay? ray = PenetrationAim.BuildAimRay(Camera(yaw: 0, pitch: 0));

        Assert.IsNotNull(ray);
        Assert.AreEqual(0.0, ray.Value.DirectionX, 1e-9);
        Assert.AreEqual(0.0, ray.Value.DirectionY, 1e-9);
        Assert.AreEqual(1.0, ray.Value.DirectionZ, 1e-9);
    }

    [TestMethod]
    public void BuildAimRay_YawQuarterFacesPositiveX()
    {
        AimRay? ray = PenetrationAim.BuildAimRay(Camera(yaw: Math.PI / 2, pitch: 0));

        Assert.IsNotNull(ray);
        Assert.AreEqual(1.0, ray.Value.DirectionX, 1e-9);
        Assert.AreEqual(0.0, ray.Value.DirectionZ, 1e-9);
    }

    [TestMethod]
    public void BuildAimRay_NoRotation_Null()
    {
        OverlayCamera camera = new(0, 0, -100, null, null, null);
        Assert.IsNull(PenetrationAim.BuildAimRay(camera));
    }

    [TestMethod]
    public void AimedTankId_PicksNearest()
    {
        AimRay ray = PenetrationAim.BuildAimRay(Camera())!.Value;
        OverlayTankState[] tanks = [Tank(1, 0, 0), Tank(2, 0, 50)];

        Assert.AreEqual(1L, PenetrationAim.AimedTankId(ray, tanks));
    }

    [TestMethod]
    public void AimedTankId_OffAxis_NoHit()
    {
        AimRay ray = PenetrationAim.BuildAimRay(Camera())!.Value;
        OverlayTankState[] tanks = [Tank(1, 10, 0)];

        Assert.IsNull(PenetrationAim.AimedTankId(ray, tanks));
    }

    [TestMethod]
    public void AimedTankId_VerticalRay_Null()
    {
        AimRay vertical = new(0, -100, 0, 0, 1, 0);
        OverlayTankState[] tanks = [Tank(1, 0, 0)];

        Assert.IsNull(PenetrationAim.AimedTankId(vertical, tanks));
    }

    [TestMethod]
    public void EvaluateAgainst_FrontHeadOn_UsesFrontArmor()
    {
        // Tank faces +Z; ray arrives from +Z (in front) toward -Z.
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(0.0, verdict.IncidenceRadians!.Value, 1e-9);
        Assert.AreEqual(93.4, verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainst_SideShot_UsesSideArmor()
    {
        // Ray arrives from +X toward -X (perpendicular to the +Z facing).
        AimRay ray = new(100, 0, 0, -1, 0, 0);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(53.4, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainst_RearShot_UsesRearArmor()
    {
        // Ray arrives from -Z (behind) toward +Z.
        AimRay ray = new(0, 0, -100, 0, 0, 1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: 0), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(40.0, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainst_NoHullYaw_Unknown()
    {
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            ray, Tank(1, 0, 0, yaw: null), Armor, new ShellSpec(200, 100));

        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainst_UnknownSideArmor_UnknownBand()
    {
        // Side armor 0 = unknown face (not zero protection); a side shot must
        // fail closed to Unknown, never fabricate a will-penetrate verdict.
        AimRay sideShot = new(100, 0, 0, -1, 0, 0);
        TankArmor frontOnly = new(FrontMm: 93.4, SideMm: 0, RearMm: 0);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainst(
            sideShot, Tank(1, 0, 0, yaw: 0), frontOnly, new ShellSpec(200, 100));

        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void SelectStruckFace_FrontRearSide_AreDerivedFromFacing()
    {
        Assert.AreEqual(
            StruckFace.Front,
            PenetrationAim.SelectStruckFace(new AimRay(0, 0, 100, 0, 0, -1), Tank(1, 0, 0, yaw: 0)));
        Assert.AreEqual(
            StruckFace.Back,
            PenetrationAim.SelectStruckFace(new AimRay(0, 0, -100, 0, 0, 1), Tank(1, 0, 0, yaw: 0)));
        Assert.AreEqual(
            StruckFace.Side,
            PenetrationAim.SelectStruckFace(new AimRay(100, 0, 0, -1, 0, 0), Tank(1, 0, 0, yaw: 0)));
    }

    [TestMethod]
    public void SelectStruckFace_NoYaw_Unknown()
    {
        Assert.AreEqual(
            StruckFace.Unknown,
            PenetrationAim.SelectStruckFace(new AimRay(0, 0, 100, 0, 0, -1), Tank(1, 0, 0, yaw: null)));
    }

    [TestMethod]
    public void ResolveBadge_AimedFrontTank_ReturnsFrontBadge()
    {
        // Camera at z=+100 facing -Z (yaw pi); tank at origin facing +Z, so
        // the ray arrives from in front and strikes the front plate.
        OverlayCamera camera = Camera(yaw: Math.PI, pitch: 0, x: 0, z: 100);
        OverlayTankState[] tanks = [Tank(1, 0, 0, yaw: 0)];
        Dictionary<long, TankArmor> armor = new() { [1] = Armor };

        PenetrationBadge? badge = PenetrationAim.ResolveBadge(
            camera, tanks, armor, new ShellSpec(200, 100));

        Assert.IsNotNull(badge);
        Assert.AreEqual(1L, badge.Value.AimedEntityId);
        Assert.AreEqual(StruckFace.Front, badge.Value.Face);
        Assert.AreEqual(93.4, badge.Value.Verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(PenetrationBand.Pen, badge.Value.Verdict.Band);
    }

    [TestMethod]
    public void ResolveBadge_NoCameraRotation_Null()
    {
        OverlayCamera camera = new(0, 0, -100, null, null, null);
        OverlayTankState[] tanks = [Tank(1, 0, 0)];
        Dictionary<long, TankArmor> armor = new() { [1] = Armor };

        Assert.IsNull(PenetrationAim.ResolveBadge(camera, tanks, armor, new ShellSpec(200, 100)));
    }

    [TestMethod]
    public void ResolveBadge_NoAimedTank_Null()
    {
        OverlayCamera camera = Camera();
        OverlayTankState[] tanks = [Tank(1, 10, 0)];
        Dictionary<long, TankArmor> armor = new() { [1] = Armor };

        Assert.IsNull(PenetrationAim.ResolveBadge(camera, tanks, armor, new ShellSpec(200, 100)));
    }

    [TestMethod]
    public void ResolveBadge_AimedTankMissingArmor_UnknownBand()
    {
        OverlayCamera camera = Camera();
        OverlayTankState[] tanks = [Tank(1, 0, 0)];
        Dictionary<long, TankArmor> armor = new();

        PenetrationBadge? badge = PenetrationAim.ResolveBadge(
            camera, tanks, armor, new ShellSpec(200, 100));

        Assert.IsNotNull(badge);
        Assert.AreEqual(PenetrationBand.Unknown, badge.Value.Verdict.Band);
    }

    // A single-triangle collision mesh (indices 0,1,2 into the vertices).
    private static CollisionMesh Mesh(params CollisionVertex[] vertices)
    {
        int[] indices = new int[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            indices[i] = i;
        }

        return new CollisionMesh(vertices, indices);
    }

    // Wraps one mesh as the HULL part (#id 1).
    private static CollisionMeshPart[] Parts(CollisionMesh mesh, long partId = 1) =>
        [new CollisionMeshPart(partId, mesh)];

    // The tank's local FRONT plate (mesh Z-up space: +Y forward): a triangle
    // at local y=1 facing +Y (outward).
    private static CollisionMesh FrontPlateMesh() => Mesh(
        new CollisionVertex(-1, 1, -1, 0, 1, 0),
        new CollisionVertex(1, 1, -1, 0, 1, 0),
        new CollisionVertex(0, 1, 1, 0, 1, 0));

    // The tank's local RIGHT-side plate: a triangle at local x=1 facing +X.
    private static CollisionMesh SidePlateMesh() => Mesh(
        new CollisionVertex(1, -1, -1, 1, 0, 0),
        new CollisionVertex(1, -1, 1, 1, 0, 0),
        new CollisionVertex(1, 1, 0, 1, 0, 0));

    // A mesh with no triangle on the ray's path (a plate far off the axis).
    private static CollisionMesh OffAxisMesh() => Mesh(
        new CollisionVertex(10, -1, -1, 1, 0, 0),
        new CollisionVertex(10, -1, 1, 1, 0, 0),
        new CollisionVertex(10, 1, 0, 1, 0, 0));

    // The tank's local TOP-DECK plate: a horizontal triangle at local z=1
    // facing +Z (up), so a shot from directly above strikes a vertical normal.
    private static CollisionMesh DeckPlateMesh() => Mesh(
        new CollisionVertex(-1, -1, 1, 0, 0, 1),
        new CollisionVertex(1, -1, 1, 0, 0, 1),
        new CollisionVertex(0, 1, 1, 0, 0, 1));

    // The tank's local FRONT GLACIS: a shallow front plate whose normal is
    // more vertical than forward (ny=0.38, nz=0.93), matching the Churchill I's
    // real glacis. It must still classify as FRONT, not a deck.
    private static CollisionMesh GlacisPlateMesh() => Mesh(
        new CollisionVertex(-1, 1, -0.2054, 0, 0.380, 0.925),
        new CollisionVertex(1, 1, -0.2054, 0, 0.380, 0.925),
        new CollisionVertex(0, -0.5, 0.4108, 0, 0.380, 0.925));

    [TestMethod]
    public void EvaluateAgainstMesh_FrontHeadOn_UsesFrontArmor()
    {
        // Tank faces +Z; ray arrives from +Z (in front) toward -Z.
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(FrontPlateMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Front, face);
        Assert.AreEqual(0.0, verdict.IncidenceRadians!.Value, 1e-9);
        Assert.AreEqual(93.4, verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(PenetrationBand.Pen, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_SideShot_UsesSideArmor()
    {
        // Ray arrives from +X toward -X, striking the local +X side plate.
        AimRay ray = new(100, 0, 0, -1, 0, 0);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(SidePlateMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Side, face);
        Assert.AreEqual(53.4, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_NoHit_Unknown()
    {
        // Ray through the tank center misses the off-axis plate entirely.
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(OffAxisMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Unknown, face);
        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_NoHullYaw_Unknown()
    {
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: null), Parts(FrontPlateMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Unknown, face);
        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_DeckHit_VerticalNormal_Unknown()
    {
        // A top-deck hit (normal +Z in mesh space) is not a front/side/rear
        // face — it must fail closed to Unknown, not borrow the frontal armor.
        AimRay ray = new(0, 100, 0, 0, -1, 0); // straight down from above

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(DeckPlateMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Unknown, face);
        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_SteepGlacis_ClassifiesFrontNotDeck()
    {
        // The Churchill I's glacis normal (0, 0.38, 0.93) is more vertical than
        // forward, but it is the FRONT plate: a head-on shot must use the front
        // armor and thicken it by the slope, not fail closed as a deck hit.
        AimRay ray = new(0, 0, 100, 0, 0, -1);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(GlacisPlateMesh()), Armor, new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Front, face);
        Assert.AreEqual(PenetrationBand.NoPen, verdict.Band);
        // Sloped: effective armor = thickness / cos(incidence) > nominal 93.4.
        Assert.IsGreaterThan(93.4, verdict.EffectiveArmorMm!.Value);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_TurretPart_UsesTurretFrontArmor()
    {
        // A head-on shot at the TURRET part (#id 3) uses the turret's frontal
        // (primary) armor (102 mm), not the hull's (186.7 mm).
        AimRay ray = new(0, 0, 100, 0, 0, -1);
        TankArmor armor = new(FrontMm: 186.7, SideMm: 53.4, RearMm: 53.4, TurretFrontMm: 102.0);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray, Tank(1, 0, 0, yaw: 0), Parts(FrontPlateMesh(), partId: 3), armor,
            new ShellSpec(200, 100), out StruckFace face);

        Assert.AreEqual(StruckFace.Front, face);
        Assert.AreEqual(102.0, verdict.EffectiveArmorMm!.Value, 1e-9);
    }

    [TestMethod]
    public void EvaluateAgainstMesh_UnknownPartId_FailsClosed()
    {
        // Collision groups beyond the proven hull/turret/gun ids must not
        // inherit hull armor. The face can still be localized, but the
        // penetration verdict remains Unknown until that part is identified.
        AimRay ray = new(0, 0, 100, 0, 0, -1);
        TankArmor armor = new(FrontMm: 186.7, SideMm: 53.4, RearMm: 40, TurretFrontMm: 102);

        PenetrationVerdict verdict = PenetrationAim.EvaluateAgainstMesh(
            ray,
            Tank(1, 0, 0, yaw: 0),
            Parts(FrontPlateMesh(), partId: 7),
            armor,
            new ShellSpec(200, 100),
            out StruckFace face);

        Assert.AreEqual(StruckFace.Front, face);
        Assert.AreEqual(PenetrationBand.Unknown, verdict.Band);
        Assert.IsNull(verdict.EffectiveArmorMm);
    }

    [TestMethod]
    public void ResolveBadge_WithMeshMiss_ReportsUnknownFace()
    {
        // When a mesh is present but the ray misses it, the box fallback must
        // not leak its FRONT label into an Unknown mesh verdict.
        OverlayCamera camera = Camera(yaw: Math.PI, pitch: 0, x: 0, z: 100);
        OverlayTankState[] tanks = [Tank(1, 0, 0, yaw: 0)];
        Dictionary<long, TankArmor> armor = new() { [1] = Armor };
        Dictionary<long, IReadOnlyList<CollisionMeshPart>> meshes =
            new() { [1] = Parts(OffAxisMesh()) };

        PenetrationBadge? badge = PenetrationAim.ResolveBadge(
            camera, tanks, armor, new ShellSpec(200, 100), meshesByEntity: meshes);

        Assert.IsNotNull(badge);
        Assert.AreEqual(StruckFace.Unknown, badge.Value.Face);
        Assert.AreEqual(PenetrationBand.Unknown, badge.Value.Verdict.Band);
    }

    [TestMethod]
    public void ResolveBadge_WithMesh_UsesMeshFace()
    {
        OverlayCamera camera = Camera(yaw: Math.PI, pitch: 0, x: 0, z: 100);
        OverlayTankState[] tanks = [Tank(1, 0, 0, yaw: 0)];
        Dictionary<long, TankArmor> armor = new() { [1] = Armor };
        Dictionary<long, IReadOnlyList<CollisionMeshPart>> meshes = new() { [1] = Parts(FrontPlateMesh()) };

        PenetrationBadge? badge = PenetrationAim.ResolveBadge(
            camera, tanks, armor, new ShellSpec(200, 100), meshesByEntity: meshes);

        Assert.IsNotNull(badge);
        Assert.AreEqual(StruckFace.Front, badge.Value.Face);
        Assert.AreEqual(93.4, badge.Value.Verdict.EffectiveArmorMm!.Value, 1e-9);
        Assert.AreEqual(PenetrationBand.Pen, badge.Value.Verdict.Band);
    }
}
