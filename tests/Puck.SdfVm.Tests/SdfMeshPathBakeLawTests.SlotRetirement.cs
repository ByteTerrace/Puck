using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    [Fact]
    public void AKeptSeparateGeometryMovesToItsNewOwnersSlotWithoutRebaking() {
        SdfProgram ProgramOn(int slot) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(new SdfMaterial(Vector3.One));

            builder.BeginInstanceDynamic(slot, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true);
            builder.ResetPoint().TransformDynamic(slot).Sphere(1, material);
            builder.EndInstance();
            return builder.Build();
        }
        var poses = new[] { new DynamicTransform(Vector3.Zero, Quaternion.Identity), new DynamicTransform(new Vector3(10, 0, 0), Quaternion.Identity) };
        var first = ProgramOn(0);
        var second = ProgramOn(1);
        var group = Assert.Single(SdfMeshBakeGroup.Plan(first, poses, 40, static (selector, dynamic) => new SdfMeshBakeGroup(selector, dynamic)));
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });
        var library = new SdfMeshLibrary();

        library.Store(group.Sealed, mesh, group.MaterialOrder);
        var bake = new SdfMeshPathBake { Library = library };

        bake.Dress(new SdfFrame(Program: first, ProgramChanged: true, Time: 0, Views: []) { DynamicTransforms = poses });
        var draw = Assert.Single(bake.Dress(new SdfFrame(Program: second, ProgramChanged: true, Time: 1, Views: []) { DynamicTransforms = poses }).MeshDraws);

        Assert.Same(mesh, draw.Mesh);
        Assert.Equal(1, draw.FrameSlot);
        Assert.Equal(10, draw.ObjectToWorld.M41);
        Assert.False(bake.Pending);
    }
}
