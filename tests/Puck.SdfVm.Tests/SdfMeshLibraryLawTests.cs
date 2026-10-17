using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfMeshLibraryLawTests {
    private static readonly Vector2[] Triangle = [new(x: 0f, y: 0.5f), new(x: 0.5f, y: -0.5f), new(x: -0.5f, y: -0.5f)];

    [Fact]
    public void OneBodyHashesAlikeInEveryProgramThatCarriesIt() {
        var crowded = Body(crowd: true, slot: 3, offset: new Vector3(x: 1f, y: 0f, z: 0f));
        var alone = Body(crowd: false, slot: 7, offset: new Vector3(x: 0f, y: 1f, z: 0f));
        var (crowdedGeometry, crowdedMaterials) = Geometry(program: crowded, instance: 1);
        var (aloneGeometry, aloneMaterials) = Geometry(program: alone, instance: 0);

        // Another palette numbering, slot, world-axis bound and side-table offset leave the body's geometry alone; its
        // materials are the program's own.
        Assert.Equal(expected: crowdedGeometry, actual: aloneGeometry);
        Assert.True(condition: crowdedGeometry.Dynamic);
        Assert.Equal(expected: [1, 2], actual: crowdedMaterials);
        Assert.Equal(expected: [0, 1], actual: aloneMaterials);

        // Another surface is another geometry, and so is the same surface baked in world space.
        var other = new SdfMeshGeometryBuilder(dynamic: true);
        var world = new SdfMeshGeometryBuilder(dynamic: false);

        other.AppendInstance(instance: 0, program: crowded);
        world.AppendInstruction(index: 0, program: crowded);
        Assert.NotEqual(expected: crowdedGeometry, actual: other.Geometry);
        Assert.NotEqual(expected: crowdedGeometry.Hash, actual: world.Geometry.Hash);
    }
    [Fact]
    public void ATakenMeshIsTheStoredOneOrTheStoredOneRenumberedByPlace() {
        var library = new SdfMeshLibrary();
        var geometry = new SdfMeshGeometry(Dynamic: true, Hash: 42);
        var mesh = Mesh(materials: [4u, 5u, 9u]);

        Assert.False(condition: library.TryTake(geometry: geometry, materials: [4, 5], mesh: out _));

        library.Store(geometry: geometry, materials: [4, 5], mesh: mesh);
        library.Store(geometry: geometry, materials: [0, 1], mesh: Mesh(materials: [0u, 1u, 0u]));

        Assert.Equal(expected: 1, actual: library.Count);
        Assert.True(condition: library.TryTake(geometry: geometry, materials: [4, 5], mesh: out var same));
        Assert.Same(expected: mesh, actual: same);
        Assert.True(condition: library.TryTake(geometry: geometry, materials: [1, 2], mesh: out var renumbered));
        // A material no shape of the group carries keeps its id.
        Assert.Equal(expected: [1u, 2u, 9u], actual: renumbered.TriangleMaterials.ToArray());
        Assert.Equal(expected: mesh.Positions, actual: renumbered.Positions);
        Assert.True(condition: library.TryTake(geometry: geometry, materials: [1, 2], mesh: out var again));
        Assert.Same(expected: renumbered, actual: again);

        for (var hash = 0; (hash < SdfMeshLibrary.Capacity); hash++) {
            library.Store(geometry: new SdfMeshGeometry(Dynamic: false, Hash: (UInt128)hash), materials: [], mesh: mesh);
        }

        // Past its capacity the library empties whole.
        Assert.Equal(expected: 1, actual: library.Count);
    }
    [Fact]
    public void ABakeTakesAGroupAnotherBakeOfItsLibraryMeshedRatherThanBakingIt() {
        var library = new SdfMeshLibrary();
        var crowded = Body(crowd: true, slot: 3, offset: Vector3.Zero);
        var alone = Body(crowd: false, slot: 7, offset: Vector3.Zero);
        var (geometry, materials) = Geometry(program: crowded, instance: 1);

        library.Store(geometry: geometry, materials: [.. materials], mesh: Mesh(materials: [1u, 2u, 2u]));

        var bake = new SdfMeshPathBake { Library = library, Residency = "alone" };
        var dressed = bake.Dress(frame: new SdfFrame(Program: alone, ProgramChanged: true, Time: 0f, Views: []) {
            DynamicTransforms = [.. Enumerable.Repeat(element: new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity), count: 8)],
        });

        Assert.False(condition: bake.Pending);

        var draw = Assert.Single(collection: dressed.MeshDraws);

        Assert.Equal(expected: 7, actual: draw.FrameSlot);
        Assert.Equal(expected: [0u, 1u, 1u], actual: draw.Mesh.TriangleMaterials.ToArray());
    }

    // A program holding a body on a dynamic slot: a sphere and a convex prism of two materials. A crowded program
    // registers a material and a static prism of its own first, so the body's materials and side-table entry sit later.
    private static SdfProgram Body(bool crowd, int slot, Vector3 offset) {
        var builder = new SdfProgramBuilder();

        if (crowd) {
            var first = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));

            _ = builder.BeginInstance(boundCenter: new Vector3(x: 5f, y: 0f, z: 0f), boundRadius: 1f)
                .Translate(offset: new Vector3(x: 5f, y: 0f, z: 0f))
                .ConvexPolygon(Triangle, 0f, SdfLift.Extrude, 0.5f, first)
                .EndInstance();
        }

        var skin = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));
        var shell = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitZ));

        _ = builder.BeginInstanceDynamic(boundOffset: offset, boundRadius: 1f, slot: slot)
            .ResetPoint()
            .TransformDynamic(slot: slot)
            .Sphere(material: skin, radius: 0.5f)
            .ResetPoint()
            .TransformDynamic(slot: slot)
            .ConvexPolygon(Triangle, 0f, SdfLift.Extrude, 0.25f, shell)
            .EndInstance();

        return builder.Build();
    }
    private static (SdfMeshGeometry Geometry, int[] Materials) Geometry(SdfProgram program, int instance) {
        var builder = new SdfMeshGeometryBuilder(dynamic: program.Instances[instance].IsDynamic);

        builder.AppendInstance(instance: instance, program: program);

        return (builder.Geometry, [.. builder.Materials]);
    }
    private static SdfMesh Mesh(uint[] materials) => new(
        indices: new uint[] { 0, 1, 2, 0, 2, 1, 1, 2, 0 },
        positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        triangleMaterials: materials
    );
}
