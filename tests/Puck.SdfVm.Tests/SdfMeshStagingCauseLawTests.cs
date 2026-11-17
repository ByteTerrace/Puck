using System.Numerics;
using Puck.Abstractions.Counting;
using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

/// <summary>The process-wide <c>sdf.instances</c> counts read as deltas, which a staging on another test's thread would
/// move: the laws reading them run alone.</summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class ProcessWorkCollection {
    /// <summary>The collection's name.</summary>
    public const string Name = "sdf.instances process work";
}

// Every record a staging packs is counted under exactly one cause, on either staging path, so the causes' totals sum to
// the records packed; each topology rebuild under exactly one reason. A dresser and tables staged through a cold
// landing, a pose and a failed publication's retry show each of those causes, and each reason total sums to the
// rebuilds.
[Collection(name: ProcessWorkCollection.Name)]
public sealed class SdfMeshStagingCauseLawTests {
    [Fact]
    public void EveryPackedRecordCountsUnderOneCauseAndEveryRebuildUnderOneReason() {
        var work = SdfMeshHistory.ProcessWork;
        var causes = Enum.GetValues<SdfMeshStagingCause>();
        var reasons = Enum.GetValues<SdfMeshTopologyReason>();
        long[] Read(Func<int, WorkKind> kindOf, int count) => [.. Enumerable.Range(0, count).Select(index => work.Read(kind: kindOf(index)))];
        long[] CauseTotals() => Read(index => SdfMeshHistory.RowsPackedOf(cause: causes[index]), causes.Length);
        long[] ReasonTotals() => Read(index => SdfMeshHistory.TopologyBuildsOf(reason: reasons[index]), reasons.Length);
        var causesBefore = CauseTotals();
        var reasonsBefore = ReasonTotals();
        var packedBefore = work.Read(kind: SdfMeshHistory.RecordsPacked);
        var buildsBefore = work.Read(kind: SdfMeshHistory.DressTopologyBuilds);

        work.ClearPeaks();
        using (var rig = new Rig(slots: 1))
        using (var run = new MeshBakeTestRun(new SdfMeshPathBake())) {
            var poses = Transforms(slots: 1);
            var moved = new SdfMovedTransforms();
            SdfFrame Input(IReadOnlyList<SdfMeshDraw> draws, long revision) {
                moved.Begin(everything: false, tableRows: poses.Length);
                return Frame(program: rig.LiveProgram, time: 0f, transforms: poses) with { MovedTransforms = moved, MeshDraws = draws, MeshDrawsRevision = revision };
            }
            var draws = new List<SdfMeshDraw> { HostDraw(identity: "a", x: 1), HostDraw(identity: "b", x: 2) };

            // The first dress rebuilds its topology for the host's draws; the tables plan the list, hold out the draws
            // whose meshes are owed, and stage them as they land.
            LandRootCut(rig, run.Dresser.Dress(Input(draws: draws, revision: 1)));
            // A pose: the dresser journals the moved row and the tables take the journal.
            draws[0] = draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 5f, yPosition: 0f, zPosition: 0f) };
            RenderRootCut(rig, run.Dresser.Dress(Input(draws: draws, revision: 2)));
            // A publication that throws partway, and its retry from the inputs, which owes every row.
            var faulty = Input(draws: new ThrowOnceDraws([draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 6f, yPosition: 0f, zPosition: 0f) }, draws[1]]), revision: 3);
            Assert.Throws<InvalidOperationException>(() => run.Dresser.Dress(faulty));
            RenderRootCut(rig, run.Dresser.Dress(faulty));
        }

        var packed = (work.Read(kind: SdfMeshHistory.RecordsPacked) - packedBefore);
        var byCause = CauseTotals().Zip(causesBefore, static (after, before) => (after - before)).ToArray();
        var byReason = ReasonTotals().Zip(reasonsBefore, static (after, before) => (after - before)).ToArray();

        Assert.True(packed > 0);
        Assert.Equal(expected: packed, actual: byCause.Sum());
        Assert.True(byCause[(int)SdfMeshStagingCause.Landing] > 0, "landing");
        Assert.True(byCause[(int)SdfMeshStagingCause.Pose] > 0, "pose");
        Assert.True(byCause[(int)SdfMeshStagingCause.Retry] > 0, "retry");
        Assert.Equal(expected: (work.Read(kind: SdfMeshHistory.DressTopologyBuilds) - buildsBefore), actual: byReason.Sum());
        Assert.True(byReason[(int)SdfMeshTopologyReason.HostDraws] > 0, "host-draws");
        Assert.True(byReason[(int)SdfMeshTopologyReason.Retry] > 0, "retry rebuild");
        // No staging of this small list packs past one upload chunk of records, and each cause's peak is one staging's.
        Assert.All(causes, cause => Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: cause)) <= SdfMeshHistory.ChunkRecords));
        Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: SdfMeshStagingCause.Pose)) > 0);
        Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsExtent) > 0);
    }

    [Fact]
    public void AnotherDressersSameSerialStillStatesItsRebuildCause() {
        var work = SdfMeshHistory.ProcessWork;
        using var rig = new Rig(slots: 1);
        using var first = new MeshBakeTestRun(new SdfMeshPathBake());
        using var second = new MeshBakeTestRun(new SdfMeshPathBake());
        var draws = new List<SdfMeshDraw> { HostDraw(identity: "a", x: 1) };
        var input = Frame(program: rig.LiveProgram, time: 0f, transforms: Transforms(slots: 1)) with { MeshDraws = draws };
        var original = first.Dresser.Dress(input);
        LandRootCut(rig, original);
        var before = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Rebuild));
        var cursorBefore = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Cursor));

        draws[0] = draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(5, 0, 0) };
        var replacement = second.Dresser.Dress(input);
        Assert.Equal(original.MeshDrawChangesSerial, replacement.MeshDrawChangesSerial);
        RenderRootCut(rig, replacement);

        Assert.Equal(1L, work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Rebuild)) - before);
        Assert.Equal(cursorBefore, work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Cursor)));
    }

    [Fact]
    public void OneChunkIsNotOverAndASteadyWindowAddsNoOverChunkFrames() {
        var work = SdfMeshHistory.ProcessWork;
        var causes = Enum.GetValues<SdfMeshStagingCause>();
        var pose = SdfMeshStagingCause.Pose;
        using var rig = new Rig(slots: 1);
        using var run = new MeshBakeTestRun(new SdfMeshPathBake());
        var draw = HostDraw(identity: "shared", x: 1);
        var draws = Enumerable.Repeat(draw, SdfMeshHistory.ChunkRecords + 1).ToList();
        var input = Frame(program: rig.LiveProgram, time: 0f, transforms: Transforms(slots: 1)) with { MeshDraws = draws };
        var packedBefore = work.Read(kind: SdfMeshHistory.RecordsPacked);
        var causesBefore = causes.Sum(cause => work.Read(kind: SdfMeshHistory.RowsPackedOf(cause)));
        var landingBefore = work.Read(kind: SdfMeshHistory.FramesOverChunkOf(SdfMeshStagingCause.Landing));
        LandRootCut(rig, run.Dresser.Dress(input));
        Assert.True(work.Read(kind: SdfMeshHistory.FramesOverChunkOf(SdfMeshStagingCause.Landing)) > landingBefore);

        work.ClearPeaks();
        var overBefore = work.Read(kind: SdfMeshHistory.FramesOverChunkOf(pose));
        for (var index = 0; index < SdfMeshHistory.ChunkRecords; index++) {
            draws[index] = draw with { ObjectToWorld = Matrix4x4.CreateTranslation(2, 0, 0) };
        }
        RenderRootCut(rig, run.Dresser.Dress(input with { MeshDrawsRevision = 1 }));
        Assert.Equal(SdfMeshHistory.ChunkRecords, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(pose)));
        Assert.Equal(overBefore, work.Read(kind: SdfMeshHistory.FramesOverChunkOf(pose)));

        for (var index = 0; index < draws.Count; index++) {
            draws[index] = draw with { ObjectToWorld = Matrix4x4.CreateTranslation(3, 0, 0) };
        }
        var settled = run.Dresser.Dress(input with { MeshDrawsRevision = 2 });
        RenderRootCut(rig, settled);
        Assert.Equal(overBefore + 1, work.Read(kind: SdfMeshHistory.FramesOverChunkOf(pose)));
        Assert.Equal(draws.Count, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(pose)));
        Assert.Equal(work.Read(kind: SdfMeshHistory.RecordsPacked) - packedBefore,
            causes.Sum(cause => work.Read(kind: SdfMeshHistory.RowsPackedOf(cause))) - causesBefore);

        var overAtRest = causes.Select(cause => work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause))).ToArray();
        var packedAtRest = work.Read(kind: SdfMeshHistory.RecordsPacked);
        work.ClearPeaks();
        for (var frame = 0; frame < SdfWorldTables.FrameRingSize + 1; frame++) { RenderRootCut(rig, settled); }
        Assert.Equal(overAtRest, causes.Select(cause => work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause))).ToArray());
        Assert.Equal(packedAtRest, work.Read(kind: SdfMeshHistory.RecordsPacked));
        Assert.All(causes, cause => Assert.Equal(0L, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause))));
    }
}
