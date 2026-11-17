using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Hosting.Jobs;
using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

[Collection(ProcessWorkCollection.Name)]
public sealed class SdfMeshRowsLawTests(ITestOutputHelper output) {
    [Fact]
    public void RandomOwnerEditsKeepUnrelatedRowsAndTheirHistoryWhileEveryPackedRowMatchesItsOccupancy() {
        SdfRowChecks.Enabled = true;
        try {
            using var rig = new Rig(1);
            var (rows, writer) = SdfMeshRows.Create();
            var meshes = Enumerable.Range(0, 6).Select(i => HostDraw($"mesh{i}", i)).ToArray();
            var runs = new SdfMeshRows.Run[12];
            var authored = new SdfMeshDraw[12][];
            var random = new Random(0x51df);
            SdfFrame FrameNow() => Frame(rig.LiveProgram, 0, Transforms(1)) with {
                MeshRows = rows, MeshDrawsRevision = rows.Revision, MeshDrawChanges = rows.Changes, MeshDrawChangesSerial = rows.Serial,
            };
            void Put(int owner, int count, SdfMeshDraw draw) {
                var old = runs[owner];
                var run = old.Count == count ? old : writer.Allocate(count);
                authored[owner] = Enumerable.Repeat(draw, count).ToArray();
                for (var i = 0; i < count; i++) { writer.Set(run, i, draw, new(Host: owner)); }
                if (old != run) { writer.Free(old); }
                runs[owner] = run;
            }
            void Verify(bool land = true) {
                writer.Publish();
                var frame = FrameNow();
                if (land) { LandRootCut(rig, frame); } else { RenderRootCut(rig, frame); }
                Assert.Null(rig.Engine.RowCheckFailure);
                Assert.Equal(authored.Sum(a => a?.Length ?? 0), rows.LiveCount);
                for (var owner = 0; owner < runs.Length; owner++) {
                    for (var i = 0; i < runs[owner].Count; i++) { Assert.True(rows[runs[owner].Start + i].SameStagedContent(in authored[owner][i])); }
                }
            }
            Put(0, 1, meshes[0]); Verify();
            // Growing the reserve over already placed words must stage the moved mesh's chain in this call.
            Put(1, SdfMeshRegionArena.MinimumDrawCapacity + 1, meshes[1]); Verify();
            var stable = runs[0];
            var occupancy = rows.Occupancy(stable.Start);
            for (var edit = 0; edit < 96; edit++) {
                var owner = random.Next(2, runs.Length);
                Put(owner, random.Next(0, 7), meshes[random.Next(meshes.Length)] with { Glass = (owner & 1) == 0 });
                Verify();
                Assert.Equal(stable, runs[0]);
                Assert.Equal(occupancy, rows.Occupancy(stable.Start));
            }
            // Same address, same mesh, same identity, no intervening absent publication: occupancy alone must seed.
            var before = SdfMeshHistory.ProcessWork.Read(SdfMeshHistory.Seeded);
            writer.Free(runs[0]); runs[0] = writer.Allocate(1);
            writer.Set(runs[0], 0, meshes[0], new(Host: 0)); Verify();
            Assert.True(SdfMeshHistory.ProcessWork.Read(SdfMeshHistory.Seeded) > before);
            rig.Engine.ForceMeshLimit(0); Verify(land: false);
            Assert.True(rig.Engine.DrawsAwaitMeshes);
            MemoryAccount.Shared.TrimCaches();
            rig.Engine.ForceMeshLimit(null); Verify();
            rig.Engine.ForceMeshRelocation(); Verify();
            rig.Engine.ForceSkipTables();
            for (var i = 0; i < SdfMovedTransforms.History + 4; i++) { Verify(); }
            // Start over frees even an allocated run the author did not retain, without resetting occupancy.
            _ = writer.Allocate(9);
            writer.StartOver();
            Array.Clear(runs); Array.Clear(authored);
            Put(0, 1, meshes[0]); Verify();
            Assert.True(rows.Occupancy(0) > occupancy);
            output.WriteLine($"edits=96 extent={rows.Count} live={rows.LiveCount} pack={SdfRowChecks.ProcessWork.Read(SdfRowChecks.Packed)} ledger={SdfRowChecks.ProcessWork.Read(SdfRowChecks.Ledger)} seeds={SdfRowChecks.ProcessWork.Read(SdfRowChecks.Seeds)}");
            writer.StartOver();
        } finally { SdfRowChecks.Enabled = false; }
    }

    [Fact]
    public void AStandingHandOverPacksOnlyItsOwnedRowsAndStartOversAloneRebuild() {
        using var producer = new RootCutProducer(false, secondGroup: true);
        using var rig = new Rig(3);
        var work = SdfMeshHistory.ProcessWork;
        var firstHost = HostDraw("host-a", 0);
        var secondHost = HostDraw("host-b", 1);
        var input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { firstHost, secondHost } };
        var frame = producer.Dress(input);
        LandRootCut(rig, frame);
        var table = frame.MeshRows!;
        var unchangedRow = table.Live().Single(p => Equals(p.Draw.Identity, secondHost.Identity)).Row;
        uint Generation(int row) => MemoryMarshal.Cast<byte, uint>(rig.Gpu.DeviceLocal(rig.Engine.MeshRegionBytes, "mesh-region"))[row * SdfMeshRecord.DrawWords + SdfMeshRecord.SelectionWord + 11];
        var generation = Generation(unchangedRow);
        var packed = work.Read(SdfMeshHistory.RecordsPacked);
        var planned = work.Read(SdfMeshHistory.StagingPlanRecords);
        var rebuilds = work.Read(SdfMeshHistory.DressTopologyBuilds);
        var owned = work.Read(SdfMeshHistory.RowsOwnedChanged);
        var walked = work.Read(SdfMeshHistory.RowsWalkedWhole);
        input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { firstHost with { Mesh = secondHost.Mesh }, secondHost }, MeshDrawsRevision = 1 };
        frame = producer.Dress(input);
        RenderRootCut(rig, frame);
        Assert.Equal(1, work.Read(SdfMeshHistory.RecordsPacked) - packed);
        Assert.Equal(1, work.Read(SdfMeshHistory.StagingPlanRecords) - planned);
        Assert.Equal(work.Read(SdfMeshHistory.RowsOwnedChanged) - owned, work.Read(SdfMeshHistory.RecordsPacked) - packed);
        Assert.Equal(rebuilds, work.Read(SdfMeshHistory.DressTopologyBuilds));
        Assert.Equal(walked, work.Read(SdfMeshHistory.RowsWalkedWhole));
        Assert.Equal(generation, Generation(unchangedRow));
        SdfRowChecks.PackRequested = true;
        frame = producer.Dress(input);
        RenderRootCut(rig, frame);
        Assert.Equal(rebuilds + 1, work.Read(SdfMeshHistory.DressTopologyBuilds));
        Assert.True(frame.MeshRows!.Canonical);
        var locked = producer.Dress(input with { TickLocked = true });
        Assert.Equal(rebuilds + 1, work.Read(SdfMeshHistory.DressTopologyBuilds));
        Assert.True(locked.MeshRows!.Canonical);
    }

    [Fact]
    public void TheDressOracleCoversHostAndRigidClipsFaultRetryAndCanonicalLockedFrames() {
        SdfRowChecks.Enabled = true;
        try {
            using var producer = new RootCutProducer(false, secondGroup: true);
            var host = HostDraw("host", 0) with { FrameSlot = 0 };
            var input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { host } };
            var first = producer.Dress(input);
            var table = first.MeshRows!;
            var occupancy = table.Occupancy(0);
            var clip = new SdfSlotClip(0, 2, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY);
            var clipped = producer.Dress(input with { SlotClips = new[] { clip } });
            Assert.Equal(6, clipped.MeshRows!.LiveCount);
            Assert.Equal(3, producer.Dress(input).MeshRows!.LiveCount);
            SdfRowChecks.DressFaultAfter = 1;
            Assert.Throws<InvalidOperationException>(() => producer.Dress(input with { MeshDrawsRevision = 2 }));
            var retried = producer.Dress(input);
            Assert.True(retried.MeshRows!.Occupancy(0) > occupancy);
            var grown = producer.Dress(input with { MeshDraws = new[] { host, host }, TickLocked = true });
            Assert.True(grown.MeshRows!.Canonical);
            var builds = SdfMeshHistory.ProcessWork.Read(SdfMeshHistory.DressTopologyBuilds);
            _ = producer.Dress(input with { MeshDraws = new[] { host, host }, TickLocked = true });
            Assert.Equal(builds, SdfMeshHistory.ProcessWork.Read(SdfMeshHistory.DressTopologyBuilds));
        } finally { SdfRowChecks.Enabled = false; SdfRowChecks.DressFaultAfter = 0; }
    }
}
