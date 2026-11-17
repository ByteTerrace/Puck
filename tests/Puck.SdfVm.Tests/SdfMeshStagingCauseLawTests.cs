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

        long[] Read(Func<int, WorkKind> kindOf, int count) => [.. Enumerable.Range(count: count, start: 0).Select(selector: index => work.Read(kind: kindOf(index)))];
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
            LandRootCut(rig, run.Dresser.Dress(frame: Input(draws: draws, revision: 1)));
            // A pose: the dresser journals the moved row and the tables take the journal.
            draws[0] = draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 5f, yPosition: 0f, zPosition: 0f) };
            RenderRootCut(rig, run.Dresser.Dress(frame: Input(draws: draws, revision: 2)));
            // A publication that throws partway, and its retry from the inputs, which owes every row.
            var faulty = Input(draws: new ThrowOnceDraws(draws: [draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 6f, yPosition: 0f, zPosition: 0f) }, draws[1]]), revision: 3);

            Assert.Throws<InvalidOperationException>(testCode: () => run.Dresser.Dress(frame: faulty));
            RenderRootCut(rig, run.Dresser.Dress(frame: faulty));
        }

        var packed = (work.Read(kind: SdfMeshHistory.RecordsPacked) - packedBefore);
        var byCause = CauseTotals().Zip(resultSelector: static (after, before) => (after - before), second: causesBefore).ToArray();
        var byReason = ReasonTotals().Zip(resultSelector: static (after, before) => (after - before), second: reasonsBefore).ToArray();

        Assert.True(condition: (packed > 0));
        Assert.Equal(expected: packed, actual: byCause.Sum());
        Assert.True(condition: (byCause[((int)SdfMeshStagingCause.Landing)] > 0), userMessage: "landing");
        Assert.True(condition: (byCause[((int)SdfMeshStagingCause.Pose)] > 0), userMessage: "pose");
        Assert.True(condition: (byCause[((int)SdfMeshStagingCause.Retry)] > 0), userMessage: "retry");
        Assert.Equal(expected: (work.Read(kind: SdfMeshHistory.DressTopologyBuilds) - buildsBefore), actual: byReason.Sum());
        Assert.True(condition: (byReason[((int)SdfMeshTopologyReason.Retry)] > 0), userMessage: "retry rebuild");
        // No staging of this small list packs past one upload chunk of records, and each cause's peak is one staging's.
        Assert.All(causes, cause => Assert.True(condition: (work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: cause)) <= SdfMeshHistory.ChunkRecords)));
        Assert.True(condition: (work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: SdfMeshStagingCause.Pose)) > 0));
        Assert.True(condition: (work.Read(kind: SdfMeshHistory.PeakRowsExtent) > 0));
    }
    [Fact]
    public void AnotherDressersSameSerialSeedsItsRowsWithoutClaimingARebuild() {
        var work = SdfMeshHistory.ProcessWork;
        using var rig = new Rig(slots: 1);
        using var first = new MeshBakeTestRun(new SdfMeshPathBake());
        using var second = new MeshBakeTestRun(new SdfMeshPathBake());
        var draws = new List<SdfMeshDraw> { HostDraw(identity: "a", x: 1) };
        var input = Frame(program: rig.LiveProgram, time: 0f, transforms: Transforms(slots: 1)) with { MeshDraws = draws };
        var original = first.Dresser.Dress(frame: input);

        LandRootCut(frame: original, rig: rig);
        var before = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Rebuild));
        var cursorBefore = work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Cursor));

        draws[0] = draws[0] with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 5, yPosition: 0, zPosition: 0) };
        var replacement = second.Dresser.Dress(frame: input);

        Assert.Equal(original.MeshRows!.Serial, replacement.MeshRows!.Serial);
        RenderRootCut(frame: replacement, rig: rig);

        Assert.Equal(0L, (work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: SdfMeshStagingCause.Rebuild)) - before));
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
        var draws = Enumerable.Repeat(count: (SdfMeshHistory.ChunkRecords + 1), element: draw).ToList();
        var input = Frame(program: rig.LiveProgram, time: 0f, transforms: Transforms(slots: 1)) with { MeshDraws = draws };
        var packedBefore = work.Read(kind: SdfMeshHistory.RecordsPacked);
        var causesBefore = causes.Sum(selector: cause => work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: cause)));
        var landingBefore = work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: SdfMeshStagingCause.Landing));

        LandRootCut(rig, run.Dresser.Dress(frame: input));
        Assert.True(condition: (work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: SdfMeshStagingCause.Landing)) > landingBefore));

        work.ClearPeaks();
        var overBefore = work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: pose));

        for (var index = 0; (index < SdfMeshHistory.ChunkRecords); index++) {
            draws[index] = draw with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 2, yPosition: 0, zPosition: 0) };
        }
        RenderRootCut(rig, run.Dresser.Dress(frame: input with { MeshDrawsRevision = 1 }));
        Assert.Equal(SdfMeshHistory.ChunkRecords, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: pose)));
        Assert.Equal(overBefore, work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: pose)));

        for (var index = 0; (index < draws.Count); index++) {
            draws[index] = draw with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: 3, yPosition: 0, zPosition: 0) };
        }
        var settled = run.Dresser.Dress(frame: input with { MeshDrawsRevision = 2 });

        RenderRootCut(frame: settled, rig: rig);
        Assert.Equal((overBefore + 1), work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: pose)));
        Assert.Equal(draws.Count, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: pose)));
        Assert.Equal((work.Read(kind: SdfMeshHistory.RecordsPacked) - packedBefore),
            (causes.Sum(selector: cause => work.Read(kind: SdfMeshHistory.RowsPackedOf(cause: cause))) - causesBefore));

        var overAtRest = causes.Select(selector: cause => work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: cause))).ToArray();
        var packedAtRest = work.Read(kind: SdfMeshHistory.RecordsPacked);

        work.ClearPeaks();
        for (var frame = 0; (frame < (SdfWorldTables.FrameRingSize + 1)); frame++) { RenderRootCut(frame: settled, rig: rig); }
        Assert.Equal(overAtRest, causes.Select(selector: cause => work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: cause))).ToArray());
        Assert.Equal(packedAtRest, work.Read(kind: SdfMeshHistory.RecordsPacked));
        Assert.All(causes, cause => Assert.Equal(0L, work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: cause))));
    }
}
