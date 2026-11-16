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
        using (var producer = new RootCutProducer(skinned: false, secondGroup: true))
        using (var rig = new Rig(slots: 3)) {
            rig.Engine.UploadProgram(producer.Program);
            // The first dress rebuilds its topology; the tables plan the list, hold out the draws whose meshes are owed,
            // and stage them as they land.
            LandRootCut(rig, producer.Publish(rootX: 1, memberY: 1, unusedZ: 1));
            // A pose: the dresser journals the moved rows and the tables take the journal.
            RenderRootCut(rig, producer.Publish(rootX: 2, memberY: 1, unusedZ: 1));
            // A publication that throws partway, and its retry from the inputs, which owes every row.
            var faulty = producer.FrameAt(rootX: 3, memberY: 1, unusedZ: 1) with {
                MeshDraws = new ThrowOnceDraws([HostDraw(identity: "a", x: 1), HostDraw(identity: "b", x: 2)]),
                MeshDrawsRevision = 1,
            };
            Assert.Throws<InvalidOperationException>(() => producer.Dress(faulty));
            RenderRootCut(rig, producer.Dress(faulty));
        }

        var packed = (work.Read(kind: SdfMeshHistory.RecordsPacked) - packedBefore);
        var byCause = CauseTotals().Zip(causesBefore, static (after, before) => (after - before)).ToArray();
        var byReason = ReasonTotals().Zip(reasonsBefore, static (after, before) => (after - before)).ToArray();

        Assert.True(packed > 0);
        Assert.Equal(expected: packed, actual: byCause.Sum());
        Assert.True(byCause[(int)SdfMeshStagingCause.Landing] > 0);
        Assert.True(byCause[(int)SdfMeshStagingCause.Pose] > 0);
        Assert.True(byCause[(int)SdfMeshStagingCause.Retry] > 0);
        Assert.Equal(expected: (work.Read(kind: SdfMeshHistory.DressTopologyBuilds) - buildsBefore), actual: byReason.Sum());
        Assert.True(byReason[(int)SdfMeshTopologyReason.Retry] > 0);
        // No staging of this small list packs past one upload chunk of records, and each cause's peak is one staging's.
        Assert.All(causes, cause => Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: cause)) <= SdfMeshHistory.ChunkRecords));
        Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsPackedOf(cause: SdfMeshStagingCause.Pose)) > 0);
        Assert.True(work.Read(kind: SdfMeshHistory.PeakRowsExtent) > 0);
    }
}
