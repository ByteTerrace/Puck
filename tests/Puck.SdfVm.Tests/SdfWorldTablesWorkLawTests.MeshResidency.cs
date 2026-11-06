using System.Numerics;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
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
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        var landing = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.NotSame(expected: upload, actual: landing);
        Assert.True(condition: rig.Engine.MeshesLanding);
        Assert.False(condition: rig.Engine.MeshesResident);
        landing.Completed = true;
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
        var draw = new SdfMeshDraw(Identity: "geometry", Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity);
        var frame = rig.Frame with { MeshDraws = [draw], MeshDrawsRevision = 1 };

        rig.Engine.Pack(frame: frame);
        Assert.False(condition: rig.Engine.MeshesResident);
        Assert.Equal(expected: 0u, actual: rig.Engine.MeshDrawCount);
        // The grown region's carry rides the first upload; the landing job, queued after it, completes it too.
        var geometryFence = Land(rig: rig, frame: frame);
        Assert.Equal(expected: 1u, actual: rig.Engine.MeshDrawCount);
        Assert.True(condition: geometryFence.IsSignaled);
        Assert.True(condition: rig.Engine.MeshesResident);

        for (var revision = 2; (revision <= 5); revision++) {
            frame = frame with {
                MeshDraws = [draw with { ObjectToWorld = Matrix4x4.CreateTranslation(xPosition: revision, yPosition: 0f, zPosition: 0f) }],
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
        // The draw whose mesh is owed waits out of the staged records rather than drawing words not yet landed.
        Assert.Equal(expected: 0u, actual: rig.Engine.MeshDrawCount);
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
        var smallDraw = new SdfMeshDraw(Identity: "small", Material: 0, Mesh: small, ObjectToWorld: Matrix4x4.Identity);
        var frame = rig.Frame with {
            MeshDraws = [new SdfMeshDraw(Identity: "big", Material: 0, Mesh: big, ObjectToWorld: Matrix4x4.Identity), smallDraw],
            MeshDrawsRevision = 1,
        };

        rig.Engine.Pack(frame: frame);
        var grown = rig.Engine.MeshRegionBytes;
        Assert.False(condition: rig.Engine.MeshesResident);
        // The big mesh lands in several chunks, each its own job.
        rig.Engine.SubmitUpload();
        var oldFence = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        for (var pump = 0; rig.Engine.MeshesLanding; pump++) {
            Assert.True(condition: (pump < 128));
            rig.Engine.GpuWork.Pump(tables: rig.Engine);
            // Waiting the latest job completes every earlier submission on the queue.
            rig.Gpu.LastSubmittedFence!.Wait();
            rig.Engine.GpuWork.Pump(tables: rig.Engine);
            rig.Engine.Pack(frame: frame);
        }
        oldFence.Completed = true;
        Assert.True(condition: rig.Engine.MeshesResident);

        var relocated = frame with { MeshDraws = [smallDraw], MeshDrawsRevision = 2 };
        rig.Engine.Pack(frame: relocated);
        Assert.True(condition: (rig.Engine.MeshRegionBytes < grown));
        Assert.True(condition: oldFence.IsSignaled);
        // The next slot's fence has never been submitted and is initially signaled too; neither can prove the carry.
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.SubmitUpload();
        var carried = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.False(condition: rig.Engine.MeshesResident);
        carried.Completed = true;
        // The carry has completed, but the relocated meshes have not landed.
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        rig.Gpu.LastSubmittedFence!.Wait();
        rig.Engine.GpuWork.Pump(tables: rig.Engine);
        rig.Engine.Pack(frame: relocated);
        Assert.True(condition: rig.Engine.MeshesResident);
    }
}
