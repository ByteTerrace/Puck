using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

// A body with two forms (SdfMeshPathBake.Forms.cs): the one it drew, a sphere on slot 0, and the one its look now names,
// a box on slot 1 and a ball on slot 2. A body never draws a form whose groups have not all landed: it draws the one it
// drew until then, and once they have, that form alone.
public sealed partial class SdfMeshPathBakeLawTests {
    private static readonly DynamicTransform[] FormPoses = [new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity), new(Vector3.Zero, Quaternion.Identity)];

    private static SdfProgram FormsProgram() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Vector3.One));

        builder.BeginInstanceDynamic(slot: 0, boundOffset: Vector3.Zero, boundRadius: 2, separateBake: true, boundInSlotFrame: true);
        builder.ResetPoint().TransformDynamic(slot: 0).Sphere(radius: 1, material: material);
        builder.EndInstance();
        builder.BeginInstanceDynamic(slot: 1, boundOffset: Vector3.Zero, boundRadius: 2, separateBake: true, boundInSlotFrame: true);
        builder.ResetPoint().TransformDynamic(slot: 1).Box(halfExtents: new Vector3(value: 0.5f), material: material, round: 0f);
        builder.EndInstance();
        builder.BeginInstanceDynamic(slot: 2, boundOffset: Vector3.Zero, boundRadius: 2, separateBake: true, boundInSlotFrame: true);
        builder.ResetPoint().TransformDynamic(slot: 2).Translate(offset: new Vector3(x: 0, y: 1, z: 0)).Sphere(radius: 0.25f, material: material);
        builder.EndInstance();
        return builder.Build();
    }
    // Stores a mesh for the group riding each listed slot, and returns each by its slot.
    private static Dictionary<int, SdfMesh> StoreForms(SdfMeshLibrary library, SdfProgram program, int[] slots) {
        var meshes = new Dictionary<int, SdfMesh>();

        foreach (var group in SdfMeshBakeGroup.Plan(program, FormPoses, 40, static (selector, dynamic) => new SdfMeshBakeGroup(dynamic: dynamic, selector: selector))) {
            if (Array.IndexOf(array: slots, value: group.Slot) < 0) { continue; }
            var mesh = new SdfMesh(positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY }, indices: new uint[] { 0, 1, 2 });

            library.Store(group.Sealed, mesh, group.MaterialOrder);
            meshes[group.Slot] = mesh;
        }
        Assert.Equal(expected: slots.Length, actual: meshes.Count);
        return meshes;
    }
    private static SdfFrame FormsFrame(SdfProgram program, SdfMeshForm drew, SdfMeshForm named) => new(Program: program, ProgramChanged: true, Time: 0, Views: []) {
        DynamicTransforms = FormPoses,
        MeshForms = [new SdfMeshFormRange(Form: named, FirstSlot: 1, Named: true, SlotCount: 2), new SdfMeshFormRange(Form: drew, FirstSlot: 0, Named: false, SlotCount: 1)],
    };

    [Fact]
    public void ABodyDrawsTheFormItDrewWhileAGroupOfTheNamedOneHasNotLanded() {
        var program = FormsProgram();
        using var library = new SdfMeshLibrary();
        var meshes = StoreForms(library: library, program: program, slots: [0, 1]);
        using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, PlannedTier = MeshBakeTestRun.PlannedTier }, ownsLibrary: false);
        var owner = new object();
        var drew = new SdfMeshForm(Owner: owner) { Drawn = true };
        var named = new SdfMeshForm(Owner: owner);

        // The named form's box has landed and its ball has not: drawing it would show the box alone.
        for (var frame = 0; (frame < 3); frame++) {
            var draw = Assert.Single(collection: bakeRun.Dresser.Dress(frame: FormsFrame(drew: drew, named: named, program: program)).DressedDraws());

            Assert.Same(expected: meshes[0], actual: draw.Mesh);
            Assert.Equal(expected: 0, actual: draw.FrameSlot);
        }
        Assert.True(condition: drew.Drawn);
        Assert.False(condition: named.Drawn);
        Assert.True(condition: bakeRun.Bake.Pending);
    }
    [Fact]
    public void ANamedFormWhoseGroupsAllShowTheirProductTakesTheDrawsWholeAndTheFormItReplacedDrawsNothing() {
        var program = FormsProgram();
        using var library = new SdfMeshLibrary();
        var meshes = StoreForms(library: library, program: program, slots: [0, 1, 2]);
        using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, PlannedTier = MeshBakeTestRun.PlannedTier }, ownsLibrary: false);
        var owner = new object();
        var drew = new SdfMeshForm(Owner: owner) { Drawn = true };
        var named = new SdfMeshForm(Owner: owner);
        IReadOnlyList<SdfMeshDraw> draws = [];

        for (var frame = 0; (frame < 3); frame++) {
            draws = bakeRun.Dresser.Dress(frame: FormsFrame(drew: drew, named: named, program: program)).DressedDraws();
        }
        Assert.Equal(expected: [1, 2], actual: draws.Select(selector: static draw => draw.FrameSlot).Order());
        Assert.Contains(collection: draws, filter: draw => ReferenceEquals(objA: draw.Mesh, objB: meshes[1]));
        Assert.Contains(collection: draws, filter: draw => ReferenceEquals(objA: draw.Mesh, objB: meshes[2]));
        Assert.True(condition: named.Drawn);
        Assert.False(condition: drew.Drawn);
    }
}
