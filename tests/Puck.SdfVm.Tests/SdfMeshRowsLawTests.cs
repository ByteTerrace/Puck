using System.Numerics;
using System.Runtime.InteropServices;
using Puck.Hosting.Jobs;
using Puck.SignedDistance;
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
            var meshes = Enumerable.Range(count: 6, start: 0).Select(selector: i => HostDraw(identity: $"mesh{i}", x: i)).ToArray();
            var runs = new SdfMeshRows.Run[12];
            var authored = new SdfMeshDraw[12][];
            var random = new Random(Seed: 0x51df);

            SdfFrame FrameNow() => Frame(program: rig.LiveProgram, time: 0, transforms: Transforms(slots: 1)) with {
                MeshRows = rows,
                MeshDrawsRevision = rows.Revision,
                MeshDrawChanges = rows.Changes,
                MeshDrawChangesSerial = rows.Serial,
            };
            void Put(int owner, int count, SdfMeshDraw draw) {
                var old = runs[owner];
                var run = ((old.Count == count) ? old : writer.Allocate(count: count));

                authored[owner] = Enumerable.Repeat(count: count, element: draw).ToArray();
                for (var i = 0; (i < count); i++) { writer.Set(run, i, draw, new(Host: owner)); }
                if (old != run) { writer.Free(run: old); }
                runs[owner] = run;
            }
            void Verify(bool land = true) {
                writer.Publish();
                var frame = FrameNow();

                if (land) { LandRootCut(frame: frame, rig: rig); } else { RenderRootCut(frame: frame, rig: rig); }
                Assert.Null(@object: rig.Engine.RowCheckFailure);
                Assert.Equal(authored.Sum(selector: a => (a?.Length ?? 0)), rows.LiveCount);
                for (var owner = 0; (owner < runs.Length); owner++) {
                    for (var i = 0; (i < runs[owner].Count); i++) { Assert.True(condition: rows[(runs[owner].Start + i)].SameStagedContent(other: in authored[owner][i])); }
                }
            }
            Put(0, 1, meshes[0]); Verify();
            // Growing the reserve over already placed words must stage the moved mesh's chain in this call.
            Put(1, (SdfMeshRegionArena.MinimumDrawCapacity + 1), meshes[1]); Verify();
            var stable = runs[0];
            var occupancy = rows.Occupancy(row: stable.Start);

            for (var edit = 0; (edit < 96); edit++) {
                var owner = random.Next(2, runs.Length);

                Put(owner, random.Next(maxValue: 7, minValue: 0), meshes[random.Next(maxValue: meshes.Length)] with { Glass = ((owner & 1) == 0) });
                Verify();
                Assert.Equal(stable, runs[0]);
                Assert.Equal(occupancy, rows.Occupancy(row: stable.Start));
            }
            // Same address, same mesh, same identity, no intervening absent publication: occupancy alone must seed.
            var before = SdfMeshHistory.ProcessWork.Read(kind: SdfMeshHistory.Seeded);

            writer.Free(run: runs[0]); runs[0] = writer.Allocate(count: 1);
            writer.Set(runs[0], 0, meshes[0], new(Host: 0)); Verify();
            Assert.True(condition: (SdfMeshHistory.ProcessWork.Read(kind: SdfMeshHistory.Seeded) > before));
            rig.Engine.ForceMeshLimit(bytes: 0); Verify(land: false);
            Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
            MemoryAccount.Shared.TrimCaches();
            rig.Engine.ForceMeshLimit(bytes: null); Verify();
            rig.Engine.ForceMeshRelocation(); Verify();
            rig.Engine.ForceSkipTables();
            for (var i = 0; (i < (SdfMovedTransforms.History + 4)); i++) { Verify(); }
            // Start over frees even an allocated run the author did not retain, without resetting occupancy.
            _ = writer.Allocate(count: 9);
            writer.StartOver();
            Array.Clear(array: runs); Array.Clear(array: authored);
            Put(0, 1, meshes[0]); Verify();
            Assert.True(condition: (rows.Occupancy(row: 0) > occupancy));
            output.WriteLine(message: $"edits=96 extent={rows.Count} live={rows.LiveCount} pack={SdfRowChecks.ProcessWork.Read(kind: SdfRowChecks.Packed)} ledger={SdfRowChecks.ProcessWork.Read(kind: SdfRowChecks.Ledger)} seeds={SdfRowChecks.ProcessWork.Read(kind: SdfRowChecks.Seeds)}");
            writer.StartOver();
        } finally { SdfRowChecks.Enabled = false; }
    }
    [Fact]
    public void AStandingHandOverPacksOnlyItsOwnedRowsAndStartOversAloneRebuild() {
        using var producer = new RootCutProducer(false, secondGroup: true);
        using var rig = new Rig(3);
        var work = SdfMeshHistory.ProcessWork;
        var firstHost = HostDraw(identity: "host-a", x: 0);
        var secondHost = HostDraw(identity: "host-b", x: 1);
        var input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { firstHost, secondHost } };
        var frame = producer.Dress(frame: input);

        LandRootCut(frame: frame, rig: rig);
        var table = frame.MeshRows!;
        var unchangedRow = table.Live().Single(predicate: p => Equals(objA: p.Draw.Identity, objB: secondHost.Identity)).Row;

        uint Generation(int row) => MemoryMarshal.Cast<byte, uint>(span: rig.Gpu.DeviceLocal(rig.Engine.MeshRegionBytes, "mesh-region"))[(((row * SdfMeshRecord.DrawWords) + SdfMeshRecord.SelectionWord) + 11)];
        var generation = Generation(row: unchangedRow);
        var packed = work.Read(kind: SdfMeshHistory.RecordsPacked);
        var planned = work.Read(kind: SdfMeshHistory.StagingPlanRecords);
        var rebuilds = work.Read(kind: SdfMeshHistory.DressTopologyBuilds);
        var owned = work.Read(kind: SdfMeshHistory.RowsOwnedChanged);
        var walked = work.Read(kind: SdfMeshHistory.RowsWalkedWhole);

        input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { firstHost with { Mesh = secondHost.Mesh }, secondHost }, MeshDrawsRevision = 1 };
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        frame = producer.Dress(frame: input);
        RenderRootCut(frame: frame, rig: rig);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        output.WriteLine($"standing-hand-over packed={work.Read(SdfMeshHistory.RecordsPacked) - packed} planned={work.Read(SdfMeshHistory.StagingPlanRecords) - planned} owned={work.Read(SdfMeshHistory.RowsOwnedChanged) - owned} whole={work.Read(SdfMeshHistory.RowsWalkedWhole) - walked} allocated={allocated}B");
        Assert.Equal(1, (work.Read(kind: SdfMeshHistory.RecordsPacked) - packed));
        Assert.Equal(1, (work.Read(kind: SdfMeshHistory.StagingPlanRecords) - planned));
        Assert.Equal((work.Read(kind: SdfMeshHistory.RowsOwnedChanged) - owned), (work.Read(kind: SdfMeshHistory.RecordsPacked) - packed));
        Assert.Equal(rebuilds, work.Read(kind: SdfMeshHistory.DressTopologyBuilds));
        Assert.Equal(walked, work.Read(kind: SdfMeshHistory.RowsWalkedWhole));
        Assert.Equal(generation, Generation(row: unchangedRow));
        SdfRowChecks.PackRequested = true;
        frame = producer.Dress(frame: input);
        RenderRootCut(frame: frame, rig: rig);
        Assert.Equal((rebuilds + 1), work.Read(kind: SdfMeshHistory.DressTopologyBuilds));
        Assert.True(condition: frame.MeshRows!.Canonical);
        var locked = producer.Dress(frame: input with { TickLocked = true });

        Assert.Equal((rebuilds + 1), work.Read(kind: SdfMeshHistory.DressTopologyBuilds));
        Assert.True(condition: locked.MeshRows!.Canonical);
    }
    [Fact]
    public void TheDressOracleCoversHostAndRigidClipsFaultRetryAndCanonicalLockedFrames() {
        SdfRowChecks.Enabled = true;
        try {
            using var producer = new RootCutProducer(false, secondGroup: true);
            var host = HostDraw(identity: "host", x: 0) with { FrameSlot = 0 };
            var copy = new SdfMeshInstance(-1, "rigid", new(Vector3.Zero, Quaternion.Identity), 1, true) { Mesh = host.Mesh };
            var input = producer.FrameAt(1, 1, 1) with { MeshDraws = new[] { host }, MeshInstances = new[] { copy } };
            var first = producer.Dress(frame: input);
            var table = first.MeshRows!;
            var occupancy = table.Occupancy(row: 0);
            var clip = new SdfSlotClip(0, 2, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY);
            var clipped = producer.Dress(frame: input with { SlotClips = new[] { clip } });

            Assert.Equal(8, clipped.MeshRows!.LiveCount);
            Assert.Equal(4, producer.Dress(frame: input).MeshRows!.LiveCount);
            SdfRowChecks.DressFaultAfter = 1;
            Assert.ThrowsAny<InvalidOperationException>(testCode: () => producer.Dress(frame: input with { MeshDrawsRevision = 2 }));
            var retried = producer.Dress(frame: input);

            Assert.True(condition: (retried.MeshRows!.Occupancy(row: 0) > occupancy));
            var grown = producer.Dress(frame: input with { MeshDraws = new[] { host, host }, TickLocked = true });

            Assert.True(condition: grown.MeshRows!.Canonical);
            var builds = SdfMeshHistory.ProcessWork.Read(kind: SdfMeshHistory.DressTopologyBuilds);

            _ = producer.Dress(frame: input with { MeshDraws = new[] { host, host }, TickLocked = true });
            Assert.Equal(builds, SdfMeshHistory.ProcessWork.Read(kind: SdfMeshHistory.DressTopologyBuilds));
        } finally { SdfRowChecks.Enabled = false; SdfRowChecks.DressFaultAfter = 0; }
    }

    [Fact]
    public void RandomProductsCopiesProgramsAndClipsAgreeWithTheRebuildAfterEveryPublication() {
        SdfRowChecks.Enabled = true;
        try {
            using var library = new SdfMeshLibrary();
            using var run = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, PlannedTier = static _ => 0 }, ownsLibrary: false);
            using var rig = new Rig(1);
            var transforms = Transforms(1);
            var programs = new SdfProgram[3];
            var meshes = Enumerable.Range(0, 3).Select(i => HostDraw($"part{i}", i).Mesh).ToArray();
            for (var i = 0; i < programs.Length; i++) {
                var builder = new SdfProgramBuilder();
                builder.AddMaterial(new SdfMaterial(Vector3.One));
                builder.BeginInstanceDynamic(0, Vector3.Zero, 2, separateBake: true).ResetPoint().TransformDynamic(0).Sphere(0.5f + i * 0.1f, 0).EndInstance();
                programs[i] = builder.Build();
                foreach (var group in SdfMeshBakeGroup.Plan(programs[i], transforms, 40, static (slot, dynamic) => new SdfMeshBakeGroup(slot, dynamic))) {
                    library.Store(group.Sealed, meshes[0] with { Chunks = meshes.Take(i + 1).ToArray() }, group.MaterialOrder, form: group.Form);
                }
            }
            var random = new Random(0x119b);
            for (var edit = 0; edit < 64; edit++) {
                var copies = Enumerable.Range(0, random.Next(1, 5)).Select(i => new SdfMeshInstance(0, $"copy{i}", new(new Vector3(i, edit % 3, 0), Quaternion.Identity), 1, true) { Mesh = (i % 2 == 0 ? null : meshes[1]) }).ToArray();
                var host = Enumerable.Range(0, random.Next(0, 4)).Select(i => new SdfMeshDraw(meshes[2], Matrix4x4.CreateTranslation(i, 0, 0), 0, $"host{i}") { FrameSlot = 0 }).ToArray();
                var frame = Frame(programs[random.Next(programs.Length)], edit, transforms) with {
                    MeshDraws = host, MeshDrawsRevision = edit, MeshAssetSlots = new[] { 0 }, MeshInstances = copies,
                    SlotClips = edit % 3 == 0 ? new[] { new SdfSlotClip(0, 1, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY) } : Array.Empty<SdfSlotClip>(),
                    TickLocked = edit % 7 == 0,
                };
                var dressed = run.Dresser.Dress(frame);
                LandRootCut(rig, dressed);
                Assert.Null(rig.Engine.RowCheckFailure);
                if (frame.TickLocked) { Assert.True(dressed.MeshRows!.Canonical); }
            }
        } finally { SdfRowChecks.Enabled = false; }
    }
}
