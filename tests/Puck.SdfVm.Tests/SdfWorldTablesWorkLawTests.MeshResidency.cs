using System.Numerics;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    // The frames a landing may take at the upload budget's rate: ten seconds of the stepped clock.
    private const int MostLandingFrames = 600;

    private static SdfMesh ResidencyTriangle(float extent = 1f) => new(
        positions: new Vector3[] { Vector3.Zero, (Vector3.UnitX * extent), (Vector3.UnitY * extent) },
        indices: new uint[] { 0, 1, 2 }
    );

    // Lands what a packed frame owes through the background work, as the residency does: the frame's upload, the pump
    // after it, which submits the landing job, the job's completion, the pump that answers it, and the next staging,
    // which names the landed draws. Returns the upload's fence.
    private static FakeGpuDevice.Fence Land(Rig rig, SdfFrame frame) {
        rig.Engine.SubmitUpload();
        var upload = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        // A landing's words are packed on a worker before a pump can take its job, so the pumps wait the packing out.
        TestLiveness.Within(
            building: () => rig.Engine.MeshesPacking,
            frames: 4,
            reason: () => rig.Engine.MeshResidencyPending(),
            step: () => {
                rig.Engine.GpuWork.Pump(tables: rig.Engine);
                return !ReferenceEquals(objA: rig.Gpu.LastSubmittedFence, objB: upload);
            }
        );
        var landing = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.True(condition: rig.Engine.MeshesLanding);
        Assert.False(condition: rig.Engine.MeshesResident);
        // The queue retires in order: the landing's completion completes every submission queued before it.
        landing.Wait();
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        // The words have landed, so a residency whose view is held reads resident, but the staged records still hold the
        // draw out until the next staging names it.
        Assert.False(condition: rig.Engine.MeshesLanding);
        Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
        rig.Engine.Pack(frame: frame);
        Assert.False(condition: rig.Engine.DrawsAwaitMeshes);
        return upload;
    }

    [Fact]
    public void GeometryLandsThroughBackgroundWorkAndWaitsForItsOwnSubmissionButNotLaterPoseOrLightingUploads() {
        using var rig = new Rig(holdFences: true, distinctBuffers: true);
        var mesh = ResidencyTriangle();
        var draw = new SdfMeshDraw(identity: "geometry", material: 0, mesh: mesh, objectToWorld: Matrix4x4.Identity);
        var frame = rig.Frame with { MeshDraws = [draw], MeshDrawsRevision = 1 };

        rig.Engine.Pack(frame: frame);
        Assert.False(condition: rig.Engine.MeshesResident);
        // The staged table holds a row for each draw the producer lists, absent until its mesh lands.
        Assert.Equal(expected: 1u, actual: rig.Engine.MeshDrawCount);
        Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
        // The landing job is queued after the first upload, whose copies the store owed, so its completion completes that
        // upload too; the meshes stand once both have.
        var geometryFence = Land(rig: rig, frame: frame);
        Assert.Equal(expected: 1u, actual: rig.Engine.MeshDrawCount);
        Assert.True(condition: geometryFence.IsSignaled);
        Assert.True(condition: rig.Engine.MeshesResident, userMessage: rig.Engine.MeshResidencyPending());

        for (var revision = 2; (revision <= 5); revision++) {
            frame = frame with {
                MeshDraws = [draw.PlacedAt(objectToWorld: Matrix4x4.CreateTranslation(xPosition: revision, yPosition: 0f, zPosition: 0f))],
                MeshDrawsRevision = revision,
            };
            rig.Engine.Pack(frame: frame);
            Assert.True(condition: rig.Engine.MeshesResident);
            rig.Engine.SubmitUpload();
            var later = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
            // A nonblocking readiness read must leave this unrelated held submission outstanding.
            Assert.True(condition: rig.Engine.MeshesResident);
            Assert.False(condition: later.IsSignaled);
        }

        var replacement = ResidencyTriangle(extent: 2f);
        var replaced = frame with { MeshDraws = [draw with { Mesh = replacement }], MeshDrawsRevision = 6 };
        rig.Engine.Pack(frame: replaced);
        Assert.False(condition: rig.Engine.MeshesResident);
        // The draw whose mesh is owed waits absent in the staged records rather than drawing words not yet landed.
        Assert.Equal(expected: 1u, actual: rig.Engine.MeshDrawCount);
        Assert.True(condition: rig.Engine.DrawsAwaitMeshes);
        _ = Land(rig: rig, frame: replaced);
        Assert.Equal(expected: 1u, actual: rig.Engine.MeshDrawCount);
        Assert.True(condition: rig.Engine.MeshesResident);
    }

    [Fact]
    public void ARelocatedMeshBufferCannotBecomeResidentThroughItsOldGeometryFence() {
        using var rig = new Rig(holdFences: true, distinctBuffers: true);
        // Beyond the arena's retained slack: removing this mesh forces relocation and a real region shrink.
        const int BigVertices = ((40 << 20) / (SdfMeshRecord.VertexWords * sizeof(uint)));
        var big = new SdfMesh(positions: new Vector3[BigVertices], indices: new uint[] { 0, 1, 2 });
        var small = ResidencyTriangle();
        var smallDraw = new SdfMeshDraw(identity: "small", material: 0, mesh: small, objectToWorld: Matrix4x4.Identity);
        var frame = rig.Frame with {
            MeshDraws = [new SdfMeshDraw(identity: "big", material: 0, mesh: big, objectToWorld: Matrix4x4.Identity), smallDraw],
            MeshDrawsRevision = 1,
        };

        rig.Engine.Pack(frame: frame);
        var grown = rig.Engine.MeshRegionBytes;
        Assert.False(condition: rig.Engine.MeshesResident);
        // The big mesh lands in several jobs, at the upload budget's rate (SdfGpuWork.UploadBytesPerSecond) of the device's
        // stepped clock, so the loop is bounded in frames.
        rig.Engine.SubmitUpload();
        var oldFence = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);

        // Each frame waits out the landing words a worker packs, so the frames, not the pool's timing, bound the loops.
        void Frame(SdfFrame drawn) {
            rig.Step();
            rig.Engine.GpuWork.Pump(tables: rig.Engine);
            // Waiting the latest job completes every earlier submission on the queue.
            rig.Gpu.LastSubmittedFence!.Wait();
            rig.Engine.GpuWork.Pump(tables: rig.Engine);
            rig.Engine.Pack(frame: drawn);
        }
        TestLiveness.Within(
            building: () => rig.Engine.MeshesPacking,
            frames: MostLandingFrames,
            reason: () => $"The mesh region never landed its meshes: {rig.Engine.MeshResidencyPending()}",
            step: () => {
                if (!rig.Engine.MeshesLanding) { return true; }
                Frame(drawn: frame);
                return false;
            }
        );
        oldFence.Completed = true;
        Assert.True(condition: rig.Engine.MeshesResident);

        // Without the big mesh the presentation's region compacts, moving the small mesh down on the GPU, and shrinks once
        // the moves' sources are free, which takes the residency's uploads; none of them completes here, so the old
        // fence, signaled long since, proves nothing about the words the small mesh now stands on.
        var relocated = frame with { MeshDraws = [smallDraw], MeshDrawsRevision = 2 };
        TestLiveness.Within(
            building: () => rig.Engine.MeshesPacking,
            frames: MostLandingFrames,
            reason: () => $"The mesh region never shrank from {grown}B: {rig.Engine.MeshRegionBytes}B",
            step: () => {
                rig.Engine.Pack(frame: relocated);
                if (rig.Engine.MeshRegionBytes < grown) { return true; }
                rig.Step();
                rig.Engine.SubmitUpload();
                rig.Engine.GpuWork.Pump(tables: rig.Engine);
                return false;
            }
        );
        Assert.True(condition: oldFence.IsSignaled);
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.SubmitUpload();
        Assert.False(condition: rig.Engine.MeshesResident);
        // The meshes stand once the work after the shrink has landed and completed.
        TestLiveness.Within(
            building: () => rig.Engine.MeshesPacking,
            frames: MostLandingFrames,
            reason: () => $"The relocated meshes never landed: {rig.Engine.MeshResidencyPending()}",
            step: () => {
                if (rig.Engine.MeshesResident) { return true; }
                Frame(drawn: relocated);
                return false;
            }
        );
    }
}
