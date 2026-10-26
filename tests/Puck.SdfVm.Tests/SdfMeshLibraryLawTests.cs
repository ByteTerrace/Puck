using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

public sealed class SdfMeshLibraryLawTests {
    [Fact]
    public void BakeGroupsKeepTheirOwnDetailWhileSharingOneMotionSlot() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));

        for (var index = 0; (index < 2); index++) {
            builder.BeginInstanceDynamic(3, new Vector3((index * 10), 0, 0), 1, detail: ((index == 0) ? 0.002f : 0), separateBake: true);
            builder.ResetPoint().TransformDynamic(3).Translate(new Vector3((index * 10), 0, 0)).Sphere(material: material, radius: 0.5f);
            builder.EndInstance();
        }
        var transforms = Enumerable.Repeat(new DynamicTransform(Vector3.Zero, Quaternion.Identity), 4).ToArray();
        var program = builder.Build();
        var groups = SdfMeshBakeGroup.Plan(program, transforms, 40, static (selector, dynamic) => new SdfMeshBakeGroup(selector, dynamic));

        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Equal(3, group.Slot));
        Assert.NotEqual(groups[0].Selector, groups[1].Selector);
        Assert.Equal(0.002f, groups[0].Detail);
        Assert.Equal(0, groups[1].Detail);
        Assert.True((groups[0].High.X < groups[1].Low.X));
        var bake = new SdfMeshPathBake();

        bake.Dress(new SdfFrame(Program: program, ProgramChanged: true, Time: 0, Views: []) { DynamicTransforms = transforms });
        Assert.True(bake.TryTakeForFrame(0, out var batch));
        Assert.All(batch!.Lattices, lattice => Assert.Equal(3, lattice.FrameSlot));
    }
    [Fact]
    public void AnUnrepresentableSkinIsRefusedBeforeAnyBakeIsQueued() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(new SdfMaterial(Vector3.One));

        builder.BeginInstanceDynamic(0, Vector3.Zero, 1);
        for (var slot = 1; (slot <= 256); slot++) { builder.ResetPoint().TransformDynamic(slot).Sphere(material: material, radius: 0.01f); }
        builder.EndInstance();
        var transforms = Enumerable.Repeat(new DynamicTransform(Vector3.Zero, Quaternion.Identity), 257).ToArray();
        var refusal = Assert.Throws<InvalidOperationException>(() => SdfMeshBakeGroup.Plan(builder.Build(), transforms, 40, static (selector, dynamic) => new SdfMeshBakeGroup(selector, dynamic)));

        Assert.Contains("instance 0", refusal.Message);
        Assert.Contains("256 skin members", refusal.Message);
    }

    private static readonly Vector2[] Triangle = [new(x: 0f, y: 0.5f), new(x: 0.5f, y: -0.5f), new(x: -0.5f, y: -0.5f)];

    [Fact]
    public void ByteBudgetEvictsOnlyTheOldestGeometryAndKeepsAMissDerivable() {
        var library = new SdfMeshLibrary(byteBudget: 512);

        static SdfMeshGeometry Key(float x) {
            var builder = new SdfMeshGeometryBuilder(dynamic: false);
            var field = new SdfProgramBuilder();
            var material = field.AddMaterial(new SdfMaterial(Vector3.One));
            var program = field.ResetPoint().Translate(new Vector3(x, 0, 0)).Sphere(material: material, radius: 1).Build();

            for (var index = 0; (index < program.Instructions.Count); index++) { builder.AppendInstruction(index: index, program: program, transforms: []); }
            return builder.Geometry;
        }
        var first = Key(0); var recent = Key(1); var incoming = Key(2);

        library.StoreEmpty(first); library.StoreEmpty(recent);
        library.StoreEmpty(incoming);
        Assert.False(library.HoldsEmpty(first));
        Assert.True(library.HoldsEmpty(recent));
        Assert.True(library.HoldsEmpty(incoming));
        Assert.Equal(512, library.RetainedBytes);
        library.StoreEmpty(first);
        Assert.True(library.HoldsEmpty(first));
        Assert.Equal(2, library.Count);
    }
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

        other.AppendInstance(instance: 0, program: crowded, transforms: []);
        world.AppendInstruction(index: 0, program: crowded, transforms: []);
        Assert.NotEqual(expected: crowdedGeometry, actual: other.Geometry);
        Assert.NotEqual(expected: crowdedGeometry.Hash, actual: world.Geometry.Hash);
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
    [Fact]
    public void APooledGroupKeysEveryShapesPlaceTurnBlendAndSizeAndSharesWhereverItStands() {
        var plate = Plate.Rest;

        var (geometry, _) = PlateGeometry(plate: plate);

        // A later shape rides its own slot inside the group's instance, so the frame's transforms place and turn it.
        Assert.NotEqual(expected: geometry, actual: PlateGeometry(plate: (plate with { CutPosition = new Vector3(x: 0.02f, y: 0.01f, z: 0f) })).Geometry);
        Assert.NotEqual(expected: geometry, actual: PlateGeometry(plate: (plate with { CutRotation = Quaternion.CreateFromAxisAngle(axis: Vector3.UnitZ, angle: 0.3f) })).Geometry);
        Assert.NotEqual(expected: geometry, actual: PlateGeometry(plate: (plate with { CutBlend = SdfBlendOp.Union })).Geometry);
        Assert.NotEqual(expected: geometry, actual: PlateGeometry(plate: (plate with { CutHalfExtents = new Vector3(x: 0.1f, y: 0.2f, z: 0.1f) })).Geometry);
        Assert.NotEqual(expected: geometry, actual: PlateGeometry(plate: (plate with { VentLane = 0.5f })).Geometry);

        // The same plate on other slots, under a root elsewhere and turned, is the same geometry.
        var elsewhere = (plate with {
            RootPosition = new Vector3(x: 41.5f, y: 3.25f, z: -27f),
            RootRotation = Quaternion.CreateFromYawPitchRoll(yaw: 1.1f, pitch: 0.2f, roll: -0.4f),
            SlotBase = 23,
        });

        Assert.Equal(expected: geometry, actual: PlateGeometry(plate: elsewhere).Geometry);
    }
    [Fact]
    public void ABakeTakesAPooledGroupOnlyWhileItsShapesStandWhereTheStoredMeshHadThem() {
        var library = new SdfMeshLibrary();

        var (geometry, materials) = PlateGeometry(plate: Plate.Rest);

        library.Store(geometry: geometry, materials: materials, mesh: Mesh(materials: [0u, 0u, 1u]));

        var elsewhere = (Plate.Rest with { RootPosition = new Vector3(x: -12f, y: 0.5f, z: 30f), SlotBase = 40 });
        var moved = (Plate.Rest with { CutPosition = new Vector3(x: 0f, y: 0.08f, z: 0f) });

        Assert.False(condition: Baking(library: library, plate: elsewhere));
        Assert.True(condition: Baking(library: library, plate: moved));
    }
    [Fact]
    public void AWorldGroupKeysTheTransformsAheadOfItsFirstShape() {
        static SdfProgram Ball(float x) {
            var builder = new SdfProgramBuilder();
            var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

            return builder.ResetPoint().Translate(offset: new Vector3(x: x, y: 0f, z: 0f)).Sphere(material: material, radius: 0.5f).Build();
        }

        var program = Ball(x: 0f);
        var builder = new SdfMeshGeometryBuilder(dynamic: false);

        for (var index = 0; (index < program.Instructions.Count); index++) {
            builder.AppendInstruction(index: index, program: program, transforms: []);
        }
        // The world group bakes over a box the frame's reach wide, which its geometry holds.
        builder.AppendReach(reach: SdfFrame.DefaultFarDistance);

        var library = new SdfMeshLibrary();

        library.Store(geometry: builder.Geometry, materials: [], mesh: Mesh(materials: [0u, 0u, 0u]));

        bool Bakes(SdfProgram ball) {
            var bake = new SdfMeshPathBake { Library = library, Residency = "ball" };

            _ = bake.Dress(frame: new SdfFrame(Program: ball, ProgramChanged: true, Time: 0f, Views: []));

            return bake.Pending;
        }

        Assert.False(condition: Bakes(ball: Ball(x: 0f)));
        Assert.True(condition: Bakes(ball: Ball(x: 0.25f)));
    }

    // One pod plate the way the stamp pool emits a blend group: one dynamic instance on its first shape's slot, each shape
    // riding its own slot (SlotBase on), whose transforms place the shapes under the creation's root; a superellipsoid
    // shell, a box it is intersected with, and a vent box subtracted under a lane erosion.
    private sealed record Plate(Vector3 RootPosition, Quaternion RootRotation, int SlotBase, Vector3 CutPosition, Quaternion CutRotation, Vector3 CutHalfExtents, SdfBlendOp CutBlend, float VentLane) {
        public static Plate Rest { get; } = new(
            CutBlend: SdfBlendOp.Intersection,
            CutHalfExtents: new Vector3(x: 0.1f, y: 0.1f, z: 0.1f),
            CutPosition: new Vector3(x: 0.05f, y: 0f, z: 0f),
            CutRotation: Quaternion.Identity,
            RootPosition: new Vector3(x: 2f, y: 1f, z: 0f),
            RootRotation: Quaternion.Identity,
            SlotBase: 5,
            VentLane: 0f
        );

        public SdfProgram Program() {
            var builder = new SdfProgramBuilder();
            var shell = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX));
            var vent = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY));

            return builder.BeginInstanceDynamic(boundOffset: Vector3.Zero, boundRadius: 0.5f, slot: SlotBase)
                .PushField(compose: SdfBlendOp.Union)
                .ResetPoint().TransformDynamic(slot: SlotBase)
                .Superellipsoid(exponent: SdfProgramBuilder.MinSuperellipsoidExponent, material: shell, radii: new Vector3(x: 0.15f, y: 0.15f, z: 0.05f))
                .ResetPoint().TransformDynamic(slot: (SlotBase + 1))
                .Box(blend: CutBlend, halfExtents: CutHalfExtents, material: shell, round: 0f)
                .ResetPoint().TransformDynamic(slot: (SlotBase + 2))
                .LaneErode(from: 0f, lane: 0, noiseScale: 8f, reach: 0.05f, to: 1f)
                .Box(blend: SdfBlendOp.Subtraction, halfExtents: new Vector3(x: 0.02f, y: 0.02f, z: 0.2f), material: vent, round: 0f)
                .PopField()
                .EndInstance()
                .Build();
        }
        public DynamicTransform[] Transforms() {
            var transforms = new DynamicTransform[(SlotBase + 3)];

            DynamicTransform Shape(Vector3 position, Quaternion rotation, float lane = 0f) => new(
                Lanes: new Vector4(x: lane, y: 0f, z: 0f, w: 0f),
                Orientation: (RootRotation * rotation),
                Position: (RootPosition + Vector3.Transform(rotation: RootRotation, value: position))
            );

            transforms[SlotBase] = Shape(position: Vector3.Zero, rotation: Quaternion.Identity);
            transforms[(SlotBase + 1)] = Shape(position: CutPosition, rotation: CutRotation);
            transforms[(SlotBase + 2)] = Shape(lane: VentLane, position: new Vector3(x: -0.05f, y: 0.03f, z: 0f), rotation: Quaternion.CreateFromAxisAngle(axis: Vector3.UnitY, angle: 0.5f));

            return transforms;
        }
    }

    private static (SdfMeshGeometry Geometry, int[] Materials) PlateGeometry(Plate plate) {
        var builder = new SdfMeshGeometryBuilder(dynamic: true);

        builder.AppendInstance(instance: 0, program: plate.Program(), transforms: plate.Transforms());

        return (builder.Geometry, [.. builder.Materials]);
    }
    // Whether a bake of the plate holding the library still owes a lattice: it bakes the plate rather than taking a mesh.
    private static bool Baking(SdfMeshLibrary library, Plate plate) {
        var bake = new SdfMeshPathBake { Library = library, Residency = "plate" };

        _ = bake.Dress(frame: new SdfFrame(Program: plate.Program(), ProgramChanged: true, Time: 0f, Views: []) { DynamicTransforms = plate.Transforms() });

        return bake.Pending;
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

        builder.AppendInstance(instance: instance, program: program, transforms: []);

        return (builder.Geometry, [.. builder.Materials]);
    }
    private static SdfMesh Mesh(uint[] materials) => new(
        indices: new uint[] { 0, 1, 2, 0, 2, 1, 1, 2, 0 },
        positions: new Vector3[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY },
        triangleMaterials: materials
    );
}
