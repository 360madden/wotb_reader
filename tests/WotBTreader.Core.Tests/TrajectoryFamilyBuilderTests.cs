using WotBTreader.Core.Discovery;

namespace WotBTreader.Core.Tests;

[TestClass]
public sealed class TrajectoryFamilyBuilderTests
{
    private static readonly Guid ParticipantA = Guid.NewGuid();
    private static readonly Guid ParticipantB = Guid.NewGuid();

    private static TrajectoryCorrelationResult Result(
        string address,
        string axis,
        Guid participant,
        long entityId,
        double score = 0.9,
        int sign = 1,
        double shiftMin = 0,
        double shiftMax = 0) =>
        new(
            address,
            new ParticipantId(participant),
            entityId,
            axis,
            sign,
            ShiftSeconds: 0,
            shiftMin,
            shiftMax,
            MatchCount: 10,
            TotalSamples: 10,
            Span: 50,
            score);

    [TestMethod]
    public void CompleteFamilyIsBuiltFromThreeConsecutiveComponents()
    {
        // x/y/z at consecutive 4-byte offsets of one entity: the clean triple.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
            Result("0x1008", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        TrajectoryFamily family = families[0];
        Assert.AreEqual("0x1000", family.BaseAddress);
        Assert.AreEqual(8, family.SpanBytes);
        Assert.IsTrue(family.Complete);
        Assert.IsTrue(family.AxesCovered.SequenceEqual(["x", "y", "z"]));
        Assert.HasCount(3, family.Members);
        Assert.AreEqual(0, family.Members[0].OffsetBytes);
        Assert.AreEqual("x", family.Members[0].Axis);
        Assert.AreEqual(4, family.Members[1].OffsetBytes);
        Assert.AreEqual("y", family.Members[1].Axis);
        Assert.AreEqual(8, family.Members[2].OffsetBytes);
        Assert.AreEqual("z", family.Members[2].Axis);
    }

    [TestMethod]
    public void SurvivorInTheMiddleStillGroupsToTheLowestBase()
    {
        // The provisional survivor may be the MIDDLE component (y at 0x1000);
        // the base must still be the lowest address and offsets must be
        // relative to it.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x0FFC", "x", ParticipantA, 1, score: 1.0),
            Result("0x1000", "y", ParticipantA, 1, score: 1.0),
            Result("0x1004", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        TrajectoryFamily family = families[0];
        Assert.AreEqual("0xFFC", family.BaseAddress);
        Assert.IsTrue(family.Complete);
        // Members are sorted by offset ascending from the base.
        Assert.AreEqual(0, family.Members[0].OffsetBytes);
        Assert.AreEqual("x", family.Members[0].Axis);
        Assert.AreEqual(4, family.Members[1].OffsetBytes);
        Assert.AreEqual("y", family.Members[1].Axis);
        Assert.AreEqual(8, family.Members[2].OffsetBytes);
        Assert.AreEqual("z", family.Members[2].Axis);
    }

    [TestMethod]
    public void TwoMemberPartialFamilyReportsCoveredAxes()
    {
        // Only x and y scored (e.g. the z ground axis was stationary and was
        // excluded by the scorer): the family is reported but incomplete.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        Assert.IsFalse(families[0].Complete);
        Assert.IsTrue(families[0].AxesCovered.SequenceEqual(["x", "y"]));
        Assert.AreEqual(4, families[0].SpanBytes);
    }

    [TestMethod]
    public void SingletonScoredAddressIsNotAFamily()
    {
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(0, families);
    }

    [TestMethod]
    public void AddressBeyondSpanStartsANewGroupAndIsDroppedAsSingleton()
    {
        // Base-relative span: 0x1014 is +20 from the base 0x1000, beyond the
        // 16-byte window, so it starts a new singleton group (no family) while
        // the first group stays a partial family.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
            Result("0x1014", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        Assert.IsFalse(families[0].Complete);
        Assert.IsTrue(families[0].AxesCovered.SequenceEqual(["x", "y"]));
    }

    [TestMethod]
    public void DifferentEntitiesDoNotGroup()
    {
        // A neighbor reproducing a DIFFERENT entity's axis is not a component
        // of this survivor's vector: both remain singletons.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantB, 2, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(0, families);
    }

    [TestMethod]
    public void InterleavedForeignEntityDoesNotSplitSameEntityPair()
    {
        // Entity 1 at 0x1000 and 0x1008 with entity 2 at 0x1004 between them:
        // the legitimate same-entity pair (8 bytes apart) must still form a
        // family; entity 2's address stays a foreign singleton.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantB, 2, score: 1.0),
            Result("0x1008", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        Assert.AreEqual("0x1000", families[0].BaseAddress);
        Assert.IsTrue(families[0].AxesCovered.SequenceEqual(["x", "z"]));
        Assert.IsFalse(families[0].Complete);
        Assert.HasCount(2, families[0].Members);
    }

    [TestMethod]
    public void EdgeAlignedMemberBlocksCompleteness()
    {
        // A member whose ambiguity band rides the sweep edge ([-28, -22] at a
        // 30s bound) is a bad-anchor symptom: the family exists but cannot be
        // the clean evidence artifact.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0,
                shiftMin: -28, shiftMax: -22),
            Result("0x1008", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 30);

        Assert.HasCount(1, families);
        Assert.IsFalse(families[0].Complete);
        Assert.IsTrue(families[0].Members[1].EdgeAligned);
    }

    [TestMethod]
    public void InteriorBandKeepsCompleteness()
    {
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0,
                shiftMin: -3, shiftMax: -1),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
            Result("0x1008", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 30);

        Assert.HasCount(1, families);
        Assert.IsTrue(families[0].Complete);
    }

    [TestMethod]
    public void DuplicateAxisMultiCopyFamilyIsReportedButIncomplete()
    {
        // Two synchronized copies of x plus y and z: all axes are present but
        // the family is NOT the clean triple (4 members), so it is flagged
        // incomplete — multi-copy is a success signal, not the clean artifact.
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "x", ParticipantA, 1, score: 1.0),
            Result("0x1008", "y", ParticipantA, 1, score: 1.0),
            Result("0x100C", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(1, families);
        Assert.IsFalse(families[0].Complete);
        Assert.IsTrue(families[0].AxesCovered.SequenceEqual(["x", "y", "z"]));
    }

    [TestMethod]
    public void MalformedAndNullResultsAreSkipped()
    {
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            null!,
            Result("", "x", ParticipantA, 1, score: 1.0),
            Result("not-an-address", "x", ParticipantA, 1, score: 1.0),
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        // The malformed entries were skipped; the two valid neighbors still
        // group (as a partial x/y family — a two-member family is never the
        // clean complete triple).
        Assert.HasCount(1, families);
        Assert.IsTrue(families[0].AxesCovered.SequenceEqual(["x", "y"]));
        Assert.HasCount(2, families[0].Members);
    }

    [TestMethod]
    public void FamiliesOrderByMemberCountThenCompletenessThenBaseAddress()
    {
        IReadOnlyList<TrajectoryFamily> families = TrajectoryFamilyBuilder.Build(
        [
            // Two-member partial family at a LOWER address.
            Result("0x1000", "x", ParticipantA, 1, score: 1.0),
            Result("0x1004", "y", ParticipantA, 1, score: 1.0),
            // Complete triple at a HIGHER address.
            Result("0x2000", "x", ParticipantA, 1, score: 1.0),
            Result("0x2004", "y", ParticipantA, 1, score: 1.0),
            Result("0x2008", "z", ParticipantA, 1, score: 1.0),
        ], maxTimeShiftSeconds: 8);

        Assert.HasCount(2, families);
        // Complete family first (tie on member count is broken by completeness).
        Assert.AreEqual("0x2000", families[0].BaseAddress);
        Assert.IsTrue(families[0].Complete);
        Assert.AreEqual("0x1000", families[1].BaseAddress);
        Assert.IsFalse(families[1].Complete);
    }

    [TestMethod]
    public void EmptyAndNullInputsProduceNoFamilies()
    {
        Assert.HasCount(0, TrajectoryFamilyBuilder.Build([], maxTimeShiftSeconds: 8));
        Assert.HasCount(0, TrajectoryFamilyBuilder.Build(null!, maxTimeShiftSeconds: 8));
    }

    [TestMethod]
    public void InvalidArgumentsAreRejected()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryFamilyBuilder.Build([], maxTimeShiftSeconds: -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryFamilyBuilder.Build([], maxTimeShiftSeconds: 8, maxSpanBytes: 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            TrajectoryFamilyBuilder.Build([], maxTimeShiftSeconds: 8, maxSpanBytes: 5000));
    }
}
