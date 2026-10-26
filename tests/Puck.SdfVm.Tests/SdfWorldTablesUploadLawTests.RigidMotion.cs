using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfWorldTablesUploadLawTests {
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MovingRigidCopiesKeepTheirPreviousConsumedMatrix(bool authoredMesh) {
        using var rig = new Rig(slots: 1);
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });
        var builder = new SdfProgramBuilder();
        builder.AddMaterial(new SdfMaterial(Vector3.One));
        builder.BeginInstanceDynamic(0, Vector3.Zero, 2).ResetPoint().TransformDynamic(0).Sphere(1, 0).EndInstance();
        var program = builder.Build();
        var poses = new[] { new DynamicTransform(Vector3.Zero, Quaternion.Identity) };
        var group = Assert.Single(SdfMeshBakeGroup.Plan(program, poses, 40, static (selector, dynamic) => new SdfMeshBakeGroup(selector, dynamic)));
        var library = new SdfMeshLibrary();
        library.Store(group.Sealed, mesh, group.MaterialOrder);
        var bake = new SdfMeshPathBake { Library = library };
        SdfMeshDraw Draw(float x) {
            var copy = new SdfMeshInstance(0, "body", new DynamicTransform(new Vector3(x, 0, 0), Quaternion.Identity), 1) { Mesh = authoredMesh ? mesh : null };
            var frame = new SdfFrame(Program: program, ProgramChanged: false, Time: x, Views: []) { DynamicTransforms = poses, MeshAssetSlots = [0], MeshInstances = [copy] };
            return bake.Dress(frame).MeshDraws[authoredMesh ? 1 : 0];
        }
        var first = Draw(1);
        rig.Render(time: 0, meshDraws: [first]);
        var moved = Draw(2);
        Assert.Equal(first.MotionIdentity, moved.MotionIdentity);
        Assert.NotEqual(first.Identity, moved.Identity);
        rig.Render(time: 0, meshDraws: [moved]);
        Assert.Equal(first.ObjectToWorld, System.Runtime.InteropServices.MemoryMarshal.Read<Matrix4x4>(rig.Gpu.DeviceLocal(part: "previous-mesh-transforms", sizeBytes: (ulong)SdfMeshRegion.DrawBytes)));
        rig.Render(time: 0, meshDraws: [moved]);
        Assert.Equal(moved.ObjectToWorld, System.Runtime.InteropServices.MemoryMarshal.Read<Matrix4x4>(rig.Gpu.DeviceLocal(part: "previous-mesh-transforms", sizeBytes: (ulong)SdfMeshRegion.DrawBytes)));
    }
}
