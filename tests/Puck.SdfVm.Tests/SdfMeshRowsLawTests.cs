using System.Numerics;
using Puck.SignedDistance;
using Xunit;
using static Puck.SdfVm.Tests.SdfWorldTablesUploadLawTests;

namespace Puck.SdfVm.Tests;

[Collection(ProcessWorkCollection.Name)]
public sealed class SdfMeshRowsLawTests(ITestOutputHelper output) {
    [Fact]
    public void ClipChangesVisitOnlyTheirSlotsAndRestoreEveryOwnerIncludingAnAlreadyEqualAuthoredPlane() {
        SdfRowChecks.Enabled = true;
        try {
            using var rig = new Rig(3);
            var (rows, writer) = SdfMeshRows.Create();
            var work = SdfMeshHistory.ProcessWork;
            var background = writer.Allocate(count: (SdfMeshHistory.ChunkRecords + 1));
            var draw = HostDraw(identity: "background", x: 0) with { IsDynamic = true };

            for (var i = 0; (i < background.Count); i++) { writer.Set(background, i, draw, new(Host: i)); }
            var owned = writer.Allocate(count: 3);
            SdfMeshRows.Owner[] owners = [new(Host: background.Count), new(Instance: 0, RigidCopy: true), new(Instance: 1)];
            var authored = Enumerable.Range(0, 3).Select(selector: i => draw with { Identity = new object(), FrameSlot = i, ClipPlane = Vector4.UnitX }).ToArray();

            authored[0] = authored[0] with { CarryTranslation = Vector3.UnitZ, CarryScale = 0.5f };

            for (var i = 0; (i < 3); i++) { writer.Set(owned, i, authored[i], owners[i]); }
            var transforms = Transforms(slots: 3);
            var moved = new SdfMovedTransforms();

            SdfFrame FrameNow() {
                moved.Begin(everything: false, tableRows: transforms.Length);
                return Frame(program: rig.LiveProgram, time: 0, transforms: transforms) with { MeshRows = rows, MovedTransforms = moved };
            }
            // Reserve growth is covered separately; keep room for this straddle so it moves no mesh placement.
            var reserve = writer.Allocate(count: 3);

            writer.Publish(cause: SdfMeshStagingCause.Rebuild); LandRootCut(frame: FrameNow(), rig: rig);
            writer.Free(run: reserve); writer.Publish(); RenderRootCut(frame: FrameNow(), rig: rig);
            var rebuild = work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: SdfMeshStagingCause.Rebuild));
            var whole = work.Read(kind: SdfMeshHistory.RowsWalkedWhole);
            var packed = work.Read(kind: SdfMeshHistory.RecordsPacked);
            var clip = new SdfSlotClip(0, 3, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY);
            var clipped = owners.Select(selector: o => work.Read(kind: SdfMeshHistory.RowsClippedOf(owner: o))).ToArray();
            var restored = owners.Select(selector: o => work.Read(kind: SdfMeshHistory.RowsRestoredOf(owner: o))).ToArray();

            writer.SetClips(clips: [clip]); writer.Publish(); LandRootCut(frame: FrameNow(), rig: rig);
            Assert.Equal((background.Count + 6), rows.LiveCount);
            Assert.Equal(3, (work.Read(kind: SdfMeshHistory.RecordsPacked) - packed)); // Base planes already equal.
            Assert.Equal(whole, work.Read(kind: SdfMeshHistory.RowsWalkedWhole));
            Assert.Equal(rebuild, work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: SdfMeshStagingCause.Rebuild)));
            var serial = rows.Serial;

            writer.SetClips(clips: [clip]); writer.Publish(); RenderRootCut(frame: FrameNow(), rig: rig);
            Assert.Equal(serial, rows.Serial);
            for (var i = 0; (i < owners.Length); i++) { Assert.Equal((clipped[i] + 1), work.Read(kind: SdfMeshHistory.RowsClippedOf(owner: owners[i]))); }
            packed = work.Read(kind: SdfMeshHistory.RecordsPacked);
            authored[1] = authored[1].PlacedAt(objectToWorld: Matrix4x4.CreateTranslation(Vector3.One)) with { Glass = true };
            writer.Set(owned, 1, authored[1], owners[1]); writer.Publish(); RenderRootCut(frame: FrameNow(), rig: rig);
            Assert.Equal(2, (work.Read(kind: SdfMeshHistory.RecordsPacked) - packed));
            Assert.Equal(2, rows.GlassCount);
            writer.SetClips(clips: []); writer.Publish(); RenderRootCut(frame: FrameNow(), rig: rig);
            Assert.Equal((background.Count + 3), rows.LiveCount);
            Assert.Equal(1, rows.GlassCount);
            for (var i = 0; (i < 3); i++) {
                Assert.True(condition: rows[(owned.Start + i)].SameStagedContent(other: in authored[i]));
                Assert.Equal((restored[i] + 1), work.Read(kind: SdfMeshHistory.RowsRestoredOf(owner: owners[i])));
            }
            Assert.Equal(whole, work.Read(kind: SdfMeshHistory.RowsWalkedWhole));
            Assert.Null(@object: rig.Engine.RowCheckFailure);
            output.WriteLine(message: $"clip extent={rows.Count} clipped=host:1,copy:2,in-place:1 restored=host:1,copy:1,in-place:1 steady-journal=standing moved-packed=2 over-chunk.rebuild-delta={(work.Read(kind: SdfMeshHistory.FramesOverChunkOf(cause: SdfMeshStagingCause.Rebuild)) - rebuild)} whole-delta={(work.Read(kind: SdfMeshHistory.RowsWalkedWhole) - whole)}");
            writer.StartOver();
        } finally { SdfRowChecks.Enabled = false; }
    }

    [Fact]
    public void IndexedClipsMatchTheRebuildWhenTheyOverlapReorderReslotAndReuseSourceAddresses() {
        var (rows, writer) = SdfMeshRows.Create();
        var run = writer.Allocate(count: 4);
        var draw = HostDraw(identity: "source", x: 0);
        var authored = Enumerable.Range(0, 4).Select(selector: i => draw with { Identity = new object(), FrameSlot = i }).ToArray();
        var first = new SdfSlotClip(0, 3, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY);
        var second = first with { FirstSlot = 1, Plane = Vector4.UnitZ, CopyTranslation = Vector3.UnitZ };

        void Verify(params SdfSlotClip[] clips) {
            writer.SetClips(clips: clips); writer.Publish();
            var expected = authored.ToList();

            SdfSlotClip.Apply(draws: expected, clips: clips);
            foreach (var (_, staged) in rows.Live()) {
                var index = expected.FindIndex(match: d => d.SameStagedContent(other: in staged));

                Assert.True(condition: (index >= 0)); expected.RemoveAt(index: index);
            }
            Assert.Empty(collection: expected);
        }
        for (var i = 0; (i < 4); i++) { writer.Set(run, i, authored[i], new(Host: i)); }
        Verify(first, second); Verify(second, first);
        authored[0] = authored[0] with { FrameSlot = 3, Glass = true };
        writer.Set(run, 0, authored[0], new(Host: 0)); Verify(second, first);
        writer.Free(run: run); run = writer.Allocate(count: 4);
        for (var i = 0; (i < 4); i++) { writer.Set(run, i, authored[i], new(Host: i)); }
        Verify(first, second); Verify(first with { FirstSlot = 2, SlotCount = 1 });
        Verify(first);
        Verify(first with { Plane = new Vector4(1, BitConverter.Int32BitsToSingle(value: int.MinValue), 0, 0) });
        Verify();
        writer.StartOver();
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
            var transforms = Transforms(slots: 1);
            var programs = new SdfProgram[3];
            var meshes = Enumerable.Range(count: 3, start: 0).Select(selector: i => HostDraw(identity: $"part{i}", x: i).Mesh).ToArray();

            for (var i = 0; (i < programs.Length); i++) {
                var builder = new SdfProgramBuilder();

                builder.AddMaterial(material: new SdfMaterial(Vector3.One));
                builder.BeginInstanceDynamic(0, Vector3.Zero, 2, separateBake: true).ResetPoint().TransformDynamic(slot: 0).Sphere((0.5f + (i * 0.1f)), 0).EndInstance();
                programs[i] = builder.Build();
                foreach (var group in SdfMeshBakeGroup.Plan(programs[i], transforms, 40, static (slot, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: slot))) {
                    library.Store(group.Sealed, meshes[0] with { Chunks = meshes.Take(count: (i + 1)).ToArray() }, group.MaterialOrder, form: group.Form);
                }
            }
            var random = new Random(Seed: 0x119b);

            for (var edit = 0; (edit < 64); edit++) {
                var copies = Enumerable.Range(0, random.Next(maxValue: 5, minValue: 1)).Select(selector: i => new SdfMeshInstance(0, $"copy{i}", new(new Vector3(x: i, y: (edit % 3), z: 0), Quaternion.Identity), 1, true) { Mesh = (((i % 2) == 0) ? null : meshes[1]) }).ToArray();
                var host = Enumerable.Range(0, random.Next(maxValue: 4, minValue: 0)).Select(selector: i => new SdfMeshDraw(meshes[2], Matrix4x4.CreateTranslation(xPosition: i, yPosition: 0, zPosition: 0), 0, $"host{i}") { FrameSlot = 0 }).ToArray();
                var frame = Frame(program: programs[random.Next(maxValue: programs.Length)], time: edit, transforms: transforms) with {
                    MeshDraws = host,
                    MeshDrawsRevision = edit,
                    MeshAssetSlots = new[] { 0 },
                    MeshInstances = copies,
                    SlotClips = (((edit % 3) == 0) ? new[] { new SdfSlotClip(0, 1, Vector4.UnitX, Quaternion.Identity, Vector3.UnitY, Vector4.UnitY) } : Array.Empty<SdfSlotClip>()),
                    TickLocked = ((edit % 7) == 0),
                };
                var dressed = run.Dresser.Dress(frame: frame);

                LandRootCut(frame: dressed, rig: rig);
                Assert.Null(@object: rig.Engine.RowCheckFailure);
                if (frame.TickLocked) { Assert.True(condition: dressed.MeshRows!.Canonical); }
            }
        } finally { SdfRowChecks.Enabled = false; }
    }
}
