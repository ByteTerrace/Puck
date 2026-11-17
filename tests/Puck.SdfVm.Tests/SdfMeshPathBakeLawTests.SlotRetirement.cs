using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed partial class SdfMeshPathBakeLawTests {
    [Fact]
    public void AKeptSeparateGeometryMovesToItsNewOwnersSlotWithoutRebaking() {
        SdfProgram ProgramOn(int slot) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

            builder.BeginInstanceDynamic(slot, Vector3.Zero, 2, separateBake: true, boundInSlotFrame: true);
            builder.ResetPoint().TransformDynamic(slot: slot).Sphere(1, material);
            builder.EndInstance();
            return builder.Build();
        }
        var poses = new[] { new DynamicTransform(Vector3.Zero, Quaternion.Identity), new DynamicTransform(new Vector3(x: 10, y: 0, z: 0), Quaternion.Identity) };
        var first = ProgramOn(slot: 0);
        var second = ProgramOn(slot: 1);
        var group = Assert.Single(collection: SdfMeshBakeGroup.Plan(first, poses, 40, static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector)));
        var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });
        using var library = new SdfMeshLibrary();

        library.Store(group.Sealed, mesh, group.MaterialOrder);
        using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library }, ownsLibrary: false);
        var bake = bakeRun.Bake;
        var dresser = bakeRun.Dresser;

        dresser.Dress(frame: new SdfFrame(Program: first, ProgramChanged: true, Time: 0, Views: []) { DynamicTransforms = poses });
        var draw = Assert.Single(collection: dresser.Dress(frame: new SdfFrame(Program: second, ProgramChanged: true, Time: 1, Views: []) { DynamicTransforms = poses }).DressedDraws());

        Assert.Same(mesh, draw.Mesh);
        Assert.Equal(1, draw.FrameSlot);
        Assert.Equal(10, draw.ObjectToWorld.M41);
        Assert.False(condition: bake.Pending);
    }
}
