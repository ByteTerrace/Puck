using System.Numerics;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesWorkLawTests {
    private static SdfMesh ResidencyTriangle(float extent = 1f) => new(
        positions: new Vector3[] { Vector3.Zero, (Vector3.UnitX * extent), (Vector3.UnitY * extent) },
        indices: new uint[] { 0, 1, 2 }
    );

    [Fact]
    public void GeometryWaitsForItsOwnSubmissionAndCompletionButNotLaterPoseOrLightingUploads() {
        using var rig = new Rig(holdFences: true, distinctBuffers: true);
        var mesh = ResidencyTriangle();
        var draw = new SdfMeshDraw(Identity: "geometry", Material: 0, Mesh: mesh, ObjectToWorld: Matrix4x4.Identity);
        var frame = rig.Frame with { MeshDraws = [draw], MeshDrawsRevision = 1 };

        rig.Engine.Pack(frame: frame);
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.SubmitUpload();
        var geometryFence = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.False(condition: geometryFence.IsSignaled);
        Assert.False(condition: rig.Engine.MeshesResident);
        geometryFence.Completed = true;
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
            Assert.False(condition: later.IsSignaled);
            Assert.True(condition: rig.Engine.MeshesResident);
            // A nonblocking readiness read must leave this unrelated held submission outstanding.
            Assert.False(condition: later.IsSignaled);
            if ((revision % SdfWorldTables.FrameRingSize) == 1) { Assert.Same(expected: geometryFence, actual: later); }
        }

        var replacement = ResidencyTriangle(extent: 2f);
        rig.Engine.Pack(frame: frame with { MeshDraws = [draw with { Mesh = replacement }], MeshDrawsRevision = 6 });
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.SubmitUpload();
        var replacementFence = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.False(condition: rig.Engine.MeshesResident);
        replacementFence.Completed = true;
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
        rig.Engine.SubmitUpload();
        var oldFence = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        oldFence.Completed = true;
        Assert.True(condition: rig.Engine.MeshesResident);

        rig.Engine.Pack(frame: frame with { MeshDraws = [smallDraw], MeshDrawsRevision = 2 });
        Assert.True(condition: (rig.Engine.MeshRegionBytes < grown));
        Assert.True(condition: oldFence.IsSignaled);
        // The next slot's fence has never been submitted and is initially signaled too; neither can prove the carry.
        Assert.False(condition: rig.Engine.MeshesResident);
        rig.Engine.SubmitUpload();
        var carried = Assert.IsType<FakeGpuDevice.Fence>(@object: rig.Gpu.LastSubmittedFence);
        Assert.False(condition: carried.IsSignaled);
        Assert.False(condition: rig.Engine.MeshesResident);
        carried.Completed = true;
        Assert.True(condition: rig.Engine.MeshesResident);
    }
}
