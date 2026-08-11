using WotBTreader.Application.Replay;
using WotBTreader.Application.Results;
using WotBTreader.Application.Storage;
using WotBTreader.Core;
using WotBTreader.Core.Overlay;

namespace WotBTreader.Application.Tests;

[TestClass]
public sealed class ReplayFrameSourceTests
{
    private static readonly BattleSessionId SessionId = BattleSessionId.New();
    private static readonly DecodeRunId RunId = DecodeRunId.New();
    private static readonly EvidenceReference Evidence = new(
        SourceArtifactId.New(),
        "data.wotreplay",
        Offset: 0,
        Length: 1,
        new ContentHash(new string('a', ContentHash.Sha256HexLength)));

    [TestMethod]
    public async Task Frame_UsesViewpointAsCamera_AndRendersTanksWithNearestSamples()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "EnemyTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 1, seconds: 10, x: 5, y: 0, z: 0, yaw: 0.5),
                Sample(entityId: 2, seconds: 0, x: 100, y: 0, z: 0, yaw: null),
                Sample(entityId: 2, seconds: 10, x: 11, y: 8, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        // Camera = the viewpoint entity's sample at 10s.
        Assert.AreEqual(5, frame.Camera.X);
        Assert.AreEqual(0.5, frame.Camera.YawRadians!.Value, 1e-9);
        // Two tanks, nearest sample at/before 10s (the 10s sample).
        Assert.HasCount(2, frame.Tanks);
        OverlayTankState self = frame.Tanks.Single(tank => tank.EntityId == 1);
        Assert.AreEqual(5, self.X);
        Assert.AreEqual("ViewpointTank", self.TankName);
        Assert.AreEqual(1, self.TeamNumber);
        // Sorted by distance: self (0m) before enemy (~8m).
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
        OverlayTankState enemy = frame.Tanks[1];
        Assert.AreEqual(2, enemy.EntityId);
        Assert.AreEqual(2, enemy.TeamNumber);
        Assert.AreEqual(10, enemy.DistanceMeters, 1e-6);
    }

    [TestMethod]
    public void Frame_OmittedWhenNoSampleAtOrBeforeFrameTime()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "LateTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                // Tank 2's first sample is AFTER the frame time.
                Sample(entityId: 2, seconds: 20, x: 1, y: 0, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        // Fail-closed: tank 2 has no evidence at/before 10s and is omitted.
        Assert.HasCount(1, frame.Tanks);
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
    }

    [TestMethod]
    public void Frame_HpFractionReflectsDamageReceived_AndDestroyedClearsAlive()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "VictimTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 2, seconds: 0, x: 10, y: 0, z: 0, yaw: null),
            },
            events: new[]
            {
                DamageEvent(entityId: 2, seconds: 2, damage: 60),
                DamageEvent(entityId: 2, seconds: 4, damage: 40),
            });

        // At 3s: 60 of the victim's 100 observed damage has landed -> 0.4 hp.
        OverlayFrame mid = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(3));
        Assert.AreEqual(0.4, mid.Tanks.Single(tank => tank.EntityId == 2).HpFraction, 1e-9);
        Assert.IsTrue(mid.Tanks.Single(tank => tank.EntityId == 2).Alive);

        // At 5s: all 100 landed, then destroyed -> alive false, hp 0.
        OverlayFrame after = ReplayFrameSource.BuildFrame(
            projection with
            {
                Events = [.. projection.Events,
                    DestroyedEvent(entityId: 2, seconds: 4.5)],
            },
            TimeSpan.FromSeconds(5));
        OverlayTankState victim = after.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(0.0, victim.HpFraction, 1e-9);
        Assert.IsFalse(victim.Alive);
    }

    [TestMethod]
    public void Frame_HpFractionUsesExactMaxHealth_AndExposesCurrentHealth()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "SurvivorTank", team: 2),
                Participant(ParticipantId.New(), entityId: 3, "DeadTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 2, seconds: 0, x: 10, y: 0, z: 0, yaw: null),
                Sample(entityId: 3, seconds: 0, x: 20, y: 0, z: 0, yaw: null),
            },
            events: new[]
            {
                MaxHealthEvent(entityId: 2, maxHealth: 700),
                MaxHealthEvent(entityId: 3, maxHealth: 500),
                DamageEvent(entityId: 2, seconds: 2, damage: 100),
                DamageEvent(entityId: 3, seconds: 2, damage: 400),
                // Destroy credit: the killer is credited with the victim's
                // remaining 100 HP, so taken reaches max and the fraction 0.
                DamageEvent(entityId: 3, seconds: 4, damage: 100),
                DestroyedEvent(entityId: 3, seconds: 4.5),
            });

        // Survivor at t=3: 100 of 700 taken -> exact 600/700, current 600.
        OverlayFrame mid = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(3));
        OverlayTankState survivor = mid.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(600.0 / 700.0, survivor.HpFraction, 1e-9);
        Assert.AreEqual(600, survivor.CurrentHealth);
        Assert.AreEqual(700, survivor.MaxHealth);
        Assert.IsTrue(survivor.Alive);

        // Dead tank at t=5: all 500 taken (incl. destroy credit) -> 0 hp.
        OverlayFrame end = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(5));
        OverlayTankState dead = end.Tanks.Single(tank => tank.EntityId == 3);
        Assert.AreEqual(0.0, dead.HpFraction, 1e-9);
        Assert.AreEqual(0, dead.CurrentHealth);
        Assert.AreEqual(500, dead.MaxHealth);
        Assert.IsFalse(dead.Alive);

        // The survivor at the end is still at 600/700 — the old arc logic
        // would have shown 0.0 here (its only damage event is the 100 taken).
        OverlayTankState survivorEnd = end.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(600.0 / 700.0, survivorEnd.HpFraction, 1e-9);
        Assert.AreEqual(600, survivorEnd.CurrentHealth);
    }

    [TestMethod]
    public void Frame_DamageDealtAndKills_CumulativeAtFrameTime()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "VictimTank", team: 2),
                Participant(ParticipantId.New(), entityId: 3, "AttackerTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 2, seconds: 0, x: 10, y: 0, z: 0, yaw: null),
                Sample(entityId: 3, seconds: 0, x: 20, y: 0, z: 0, yaw: null),
            },
            events: new[]
            {
                DamageWithAttackerEvent(victimEntityId: 2, attackerEntityId: 3, seconds: 2, damage: 60),
                DamageWithAttackerEvent(victimEntityId: 2, attackerEntityId: 3, seconds: 4, damage: 40),
                DamageWithAttackerEvent(victimEntityId: 1, attackerEntityId: 3, seconds: 5, damage: 100),
                DestroyedEvent(entityId: 2, seconds: 4.5),
            });

        // Before any damage: zero totals.
        OverlayFrame early = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(1));
        OverlayTankState earlyAttacker = early.Tanks.Single(tank => tank.EntityId == 3);
        Assert.AreEqual(0, earlyAttacker.DamageDealt);
        Assert.AreEqual(0, earlyAttacker.Kills);
        Assert.AreEqual(0, earlyAttacker.DamageTaken);

        // Mid-battle: only the damage landed so far counts, no kill yet.
        OverlayFrame mid = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(3));
        OverlayTankState midAttacker = mid.Tanks.Single(tank => tank.EntityId == 3);
        Assert.AreEqual(60, midAttacker.DamageDealt);
        Assert.AreEqual(0, midAttacker.Kills);
        // The victim has received 60 of its 100 observed damage by 3s.
        Assert.AreEqual(60, mid.Tanks.Single(tank => tank.EntityId == 2).DamageTaken);
        // The viewpoint tank (1) takes the 100 hit that lands at 5s — not yet
        // by 3s; the attacker has taken nothing.
        Assert.AreEqual(0, midAttacker.DamageTaken);
        Assert.AreEqual(0, mid.Tanks.Single(tank => tank.EntityId == 1).DamageTaken);

        // After the destroy: all three damage hits + the attributed kill.
        OverlayFrame after = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(6));
        OverlayTankState attacker = after.Tanks.Single(tank => tank.EntityId == 3);
        Assert.AreEqual(200, attacker.DamageDealt);
        Assert.AreEqual(1, attacker.Kills);
        Assert.AreEqual(0, attacker.DamageTaken);
        Assert.AreEqual(100, after.Tanks.Single(tank => tank.EntityId == 1).DamageTaken);
        Assert.IsFalse(after.Tanks.Single(tank => tank.EntityId == 2).Alive);
        // The kill attribution flows into the kill feed too.
        OverlayKill kill = after.Kills.Single();
        Assert.AreEqual(2, kill.VictimEntityId);
        Assert.AreEqual(3, kill.KillerEntityId);
    }

    [TestMethod]
    public void Frame_BuildsEventPipsFromRecentWindow()
    {
        // Damage and destroyed events inside the trailing 2 s window become
        // pips; older events are not "live" and are dropped.
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                // 4.5 s before the 10 s frame: outside the 2 s window.
                DamageEvent(entityId: 1, seconds: 5.5, damage: 30),
                // 1 s before: inside.
                DamageEvent(entityId: 1, seconds: 9, damage: 60),
                // At the frame time itself: inside (the current tick).
                DamageEvent(entityId: 1, seconds: 10, damage: 40),
                DestroyedEvent(entityId: 1, seconds: 9.5),
            });

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        Assert.HasCount(3, frame.Pips);
        OverlayEventPip damage = frame.Pips.Single(pip => pip.Kind == CanonicalEventKind.Damage && pip.Damage == 60);
        Assert.AreEqual(1, damage.EntityId);
        OverlayEventPip current = frame.Pips.Single(pip => pip.Damage == 40);
        Assert.AreEqual(TimeSpan.FromSeconds(10), current.ReplayTime);
        OverlayEventPip death = frame.Pips.Single(pip => pip.Kind == CanonicalEventKind.Destroyed);
        Assert.AreEqual(0, death.Damage);
        Assert.IsFalse(frame.Pips.Any(pip => pip.Damage == 30));
    }

    [TestMethod]
    public void Frame_BuildKillsAttributedToLastDamageAttacker()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                // Victim 50 takes two hits; the destroy lands at 20s and the
                // final (killing) hit at 19s -> attacker 70 is the killer.
                DamageWithAttackerEvent(50, 60, seconds: 10, damage: 100),
                DamageWithAttackerEvent(50, 70, seconds: 19, damage: 200),
                DestroyedEvent(50, seconds: 20),
            });

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(25));

        OverlayKill kill = frame.Kills.Single();
        Assert.AreEqual(50, kill.VictimEntityId);
        Assert.AreEqual(70, kill.KillerEntityId);
        Assert.AreEqual(TimeSpan.FromSeconds(20), kill.ReplayTime);
    }

    [TestMethod]
    public void Frame_KillAttributionAllowsPosthumousHitWindow()
    {
        // Real replay: 3760571's kill hit lands ~1.7 s AFTER its destroy
        // marker. The 3 s window must still attribute that attacker.
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                DamageWithAttackerEvent(50, 70, seconds: 18, damage: 100),
                DestroyedEvent(50, seconds: 20),
                DamageWithAttackerEvent(50, 80, seconds: 21.5, damage: 50),
            });

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(25));

        OverlayKill kill = frame.Kills.Single();
        Assert.AreEqual(80, kill.KillerEntityId);
    }

    [TestMethod]
    public void Frame_KillWithoutDamageEvidenceIsEnvironmental()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                DestroyedEvent(50, seconds: 20),
            });

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(25));

        OverlayKill kill = frame.Kills.Single();
        Assert.AreEqual(50, kill.VictimEntityId);
        Assert.IsNull(kill.KillerEntityId);
    }

    [TestMethod]
    public void Frame_KillsOnlyIncludeDestroysAtOrBeforeFrameTime()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                DestroyedEvent(50, seconds: 10),
                DestroyedEvent(51, seconds: 30),
            });

        OverlayFrame at20 = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(20));
        Assert.HasCount(1, at20.Kills);
        Assert.AreEqual(50, at20.Kills[0].VictimEntityId);

        OverlayFrame at40 = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(40));
        Assert.HasCount(2, at40.Kills);
    }

    [TestMethod]
    public void Frame_ZeroDamageEventsAreNotPips()
    {
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
            },
            events: new[]
            {
                DamageEvent(entityId: 1, seconds: 9, damage: 0),
            });

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.FromSeconds(10));

        Assert.IsEmpty(frame.Pips);
    }

    [TestMethod]
    public void Frame_OmitsNonParticipantEntities()
    {
        // The position stream carries non-tank entities (a duplicate "self"
        // stream, projectiles) that must never render as nameplates.
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                // Entity 9999 has full position evidence but no roster entry.
                Sample(entityId: 9999, seconds: 0, x: 50, y: 0, z: 0, yaw: null),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.Zero);

        Assert.HasCount(1, frame.Tanks);
        Assert.AreEqual(1, frame.Tanks[0].EntityId);
    }

    [TestMethod]
    public void Frame_NoViewpointParticipant_ReturnsOriginCamera()
    {
        var projection = Projection(
            viewpointId: null,
            new[]
            {
                Participant(ParticipantId.New(), entityId: 1, "SomeTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 10, y: 0, z: 0, yaw: 0.1),
            },
            events: []);

        OverlayFrame frame = ReplayFrameSource.BuildFrame(projection, TimeSpan.Zero);

        Assert.AreEqual(0, frame.Camera.X);
        Assert.IsNull(frame.Camera.YawRadians);
        Assert.HasCount(1, frame.Tanks);
    }

    [TestMethod]
    public void Frame_CameraOverride_ReplacesViewpointCamera()
    {
        // The CAM-001 seam: a verified memory camera pose replaces the
        // viewpoint-tank approximation; the frame camera is exactly the
        // override and tank distances use the override position.
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
                Participant(ParticipantId.New(), entityId: 2, "EnemyTank", team: 2),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 0, y: 0, z: 0, yaw: 0.1),
                Sample(entityId: 2, seconds: 0, x: 10, y: 0, z: 0, yaw: null),
            },
            events: []);

        // The real camera sits behind/above the viewpoint tank (third-person
        // offset ~1-30 m), e.g. 6 m behind at +2 m elevation.
        var overrideCamera = new OverlayCamera(
            6, 2, 0, YawRadians: 0.5, PitchRadians: -0.2, RollRadians: null);
        OverlayFrame frame = ReplayFrameSource.BuildFrame(
            projection, TimeSpan.FromSeconds(5), overrideCamera);

        Assert.AreEqual(overrideCamera, frame.Camera);
        // Distance is measured from the override camera position, not the
        // viewpoint tank: enemy at (10,0,0) -> sqrt(4^2 + 2^2) = sqrt(20).
        OverlayTankState enemy = frame.Tanks.Single(tank => tank.EntityId == 2);
        Assert.AreEqual(Math.Sqrt(20), enemy.DistanceMeters, 1e-9);
        // The viewpoint tank is now sqrt(40) m from the camera, not 0.
        Assert.AreEqual(
            Math.Sqrt(40),
            frame.Tanks.Single(tank => tank.EntityId == 1).DistanceMeters,
            1e-9);
    }

    [TestMethod]
    public void Frame_NonFiniteCameraOverride_FallsBackToViewpoint()
    {
        // Fail-closed: a non-finite override position is never rendered — the
        // viewpoint camera is used instead (never a fabricated pose).
        ParticipantId viewpointId = ParticipantId.New();
        var projection = Projection(
            viewpointId,
            new[]
            {
                Participant(viewpointId, entityId: 1, "ViewpointTank", team: 1),
            },
            new[]
            {
                Sample(entityId: 1, seconds: 0, x: 3, y: 4, z: 0, yaw: 0.1),
            },
            events: []);

        var invalid = new OverlayCamera(
            double.NaN, 0, 0, YawRadians: 0.5, PitchRadians: null, RollRadians: null);
        OverlayFrame frame = ReplayFrameSource.BuildFrame(
            projection, TimeSpan.Zero, invalid);

        Assert.AreEqual(3, frame.Camera.X);
        Assert.AreEqual(0.1, frame.Camera.YawRadians!.Value, 1e-9);
    }

    [TestMethod]
    public async Task GetFrameAsync_SessionMissing_ReturnsFailure()
    {
        // Projection with a null session record triggers the explicit guard.
        ReplayDecodeProjection projection = Projection(
            viewpointId: null,
            participants: [],
            positions: [],
            events: []) with
        { Session = null };
        StubSessionRepository sessions = new(projection);
        ReplayFrameSource source = new(sessions);

        OperationResult<OverlayFrame> result = await source.GetFrameAsync(
            SessionId, TimeSpan.Zero, CancellationToken.None);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("overlay.session.missing", result.Error?.Code);
    }

    private static ReplayDecodeProjection Projection(
        ParticipantId? viewpointId,
        IReadOnlyList<Participant> participants,
        IReadOnlyList<PositionSample> positions,
        IReadOnlyList<CanonicalEvent> events)
    {
        DecodeRun decodeRun = new(
            RunId,
            Evidence.SourceArtifactId,
            DecoderId: "test",
            DecoderVersion: "1",
            SchemaVersion: "1",
            DecodeRunStatus.Succeeded,
            ReplayCapability.Positions,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            FailureCode: null,
            FailureSummary: null);
        BattleSession session = new(
            SessionId,
            RunId,
            GameVersion: "11.19.0.10",
            ArenaIdentity: null,
            MapId: null,
            MapName: null,
            BattleTimeUtc: null,
            Duration: TimeSpan.FromMinutes(10),
            viewpointId,
            SchemaVersion: "1");
        return new ReplayDecodeProjection(
            decodeRun,
            session,
            participants,
            positions,
            events,
            RawRecords: [],
            Warnings: []);
    }

    private static Participant Participant(
        ParticipantId id,
        long entityId,
        string tankName,
        int team) =>
        new(
            id,
            SessionId,
            AccountId: null,
            EntityId: entityId,
            TeamNumber: team,
            PlayerName: null,
            ClanTag: null,
            VehicleCompactDescriptor: null,
            TankId: null,
            tankName,
            TankClass.Heavy,
            BotStatus.Human,
            EvidenceConfidence.Exact,
            BattleStats: null,
            Evidence);

    private static PositionSample Sample(
        long entityId,
        double seconds,
        double x,
        double y,
        double z,
        double? yaw) =>
        new(
            PositionSampleId.New(),
            SessionId,
            ParticipantId: null,
            EntityId: entityId,
            Sequence: entityId * 100 + (long)(seconds * 10),
            ReplayTime: TimeSpan.FromSeconds(seconds),
            RawX: x,
            RawY: y,
            RawZ: z,
            NormalizedX: null,
            NormalizedY: null,
            RawCoordinateSpace: CoordinateSpace.ReplayRaw,
            NormalizedCoordinateSpace: null,
            Evidence,
            yaw,
            Pitch: null,
            Roll: null);

    private static CanonicalEvent MaxHealthEvent(long entityId, long maxHealth) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 900 + entityId * 10,
            CanonicalEventKind.MaxHealthObserved,
            TimeSpan.FromSeconds(0.1),
            ParticipantId: null,
            EntityId: entityId,
            ValuesJson: $"{{\"maxHealth\":{maxHealth}}}",
            EvidenceConfidence.Exact,
            Evidence);

    private static CanonicalEvent DamageEvent(long entityId, double seconds, int damage) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 1000 + (long)(seconds * 10),
            CanonicalEventKind.Damage,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: entityId,
            ValuesJson: $"{{\"damage\":{damage}}}",
            EvidenceConfidence.Exact,
            Evidence);

    private static CanonicalEvent DamageWithAttackerEvent(
        long victimEntityId,
        long attackerEntityId,
        double seconds,
        int damage) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 1500 + (long)(seconds * 10),
            CanonicalEventKind.Damage,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: victimEntityId,
            ValuesJson:
                $"{{\"attackerEntityId\":{attackerEntityId},\"victimEntityId\":{victimEntityId},\"damage\":{damage}}}",
            EvidenceConfidence.Exact,
            Evidence);

    private static CanonicalEvent DestroyedEvent(long entityId, double seconds) =>
        new(
            CanonicalEventId.New(),
            RunId,
            SessionId,
            Sequence: 2000 + (long)(seconds * 10),
            CanonicalEventKind.Destroyed,
            TimeSpan.FromSeconds(seconds),
            ParticipantId: null,
            EntityId: entityId,
            ValuesJson: "{}",
            EvidenceConfidence.Exact,
            Evidence);

    private sealed class StubSessionRepository : ISessionQueryRepository
    {
        private readonly ReplayDecodeProjection? _projection;

        public StubSessionRepository(ReplayDecodeProjection? projection)
        {
            _projection = projection;
        }

        public ValueTask<IReadOnlyList<DecodeRunSummary>> ListAsync(
            int offset, int limit, CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<DecodeRunSummary>>([]);

        public ValueTask<OperationResult<ReplayDecodeProjection>> GetProjectionAsync(
            BattleSessionId battleSessionId, CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                _projection is null
                    ? OperationResult.Failure<ReplayDecodeProjection>(
                        new ApplicationError("replay.session.notfound", "not found"))
                    : OperationResult.Success(_projection));

        public ValueTask<IReadOnlyList<MapBoundary>> GetMapBoundariesAsync(
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<IReadOnlyList<MapBoundary>>([]);
    }
}
