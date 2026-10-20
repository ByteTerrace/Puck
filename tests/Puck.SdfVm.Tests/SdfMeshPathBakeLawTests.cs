using System.Buffers.Binary;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

/// <summary>
/// Laws over <see cref="SdfMeshPathBake"/>'s groups, driven on the CPU: the test answers every lattice the bake takes as
/// the bake kernel would (<c>sdf-mesh-bake.comp.hlsl</c>), from a field of boxes it evaluates itself, one value, material
/// and tetrahedral gradient a sample.
/// </summary>
public sealed class SdfMeshPathBakeLawTests {
    // A static copy of two shapes turned 30 degrees about up: a wide plate and a small block standing on it, the block
    // baking with the plate (one run), then a lone block elsewhere.
    private static readonly Quaternion Turn = Quaternion.CreateFromAxisAngle(angle: (MathF.PI / 6f), axis: Vector3.UnitY);
    private static readonly Shape[] Shapes = [
        new(Center: new Vector3(x: 0f, y: 0.3f, z: 0f), Half: new Vector3(x: 3f, y: 0.3f, z: 3f), Rotation: Turn, Material: 0),
        new(Center: new Vector3(x: 0.4f, y: 0.7f, z: -0.2f), Half: new Vector3(value: 0.1f), Rotation: Turn, Material: 1),
        new(Center: new Vector3(x: 8f, y: 0.5f, z: 0f), Half: new Vector3(value: 0.5f), Rotation: Quaternion.Identity, Material: 0),
    ];

    // The one bake both laws read, each its own group: a union's final lattice holds about a million samples, so the
    // bake, and not the laws, is the class's cost.
    private static readonly Lazy<(SdfMeshPathBake Bake, SdfFrame Frame)> Baked = new(valueFactory: static () => {
        var (program, materials) = Program(shapes: Shapes);
        var bake = new SdfMeshPathBake { Residency = "copies" };
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Drive(bake: bake, frame: frame, materials: materials, shapes: Shapes);

        return (bake, frame);
    });

    [Fact]
    public void AStaticCopyBakesAsOneUnionMeshKeepingItsSmallShapeAtItsOwnResolution() {
        var (bake, frame) = Baked.Value;

        // The run of two shapes is one group, the lone block another.
        Assert.Contains(expectedSubstring: "groups=2 meshes=2", actualString: bake.Describe());

        var meshes = bake.Dress(frame: frame).MeshDraws.Select(selector: static draw => draw.Mesh).ToArray();
        var copy = Assert.Single(collection: meshes, predicate: static mesh => mesh.Positions.ToArray().Any(predicate: static point => (MathF.Abs(point.X) < 1f)));
        var positions = copy.Positions.ToArray();

        // The copy's mesh is the union's surface: no vertex off it, and none on the plate's face under the block, where
        // two meshes once fought over one face.
        var worst = positions.Max(selector: static point => MathF.Abs(Union(point: point, count: 2)));
        var worstAt = positions.MaxBy(keySelector: static point => MathF.Abs(Union(point: point, count: 2)));

        Assert.True(condition: (worst < 0.004f), userMessage: $"a vertex at {worstAt} lies {worst} from the union, {Field(shape: Shapes[0], point: worstAt)} from the plate and {Field(shape: Shapes[1], point: worstAt)} from the block");
        Assert.DoesNotContain(collection: positions, filter: static point => (MathF.Abs(point.Y - 0.6f) < 0.002f) && (Field(shape: Shapes[1], point: new Vector3(x: point.X, y: 0.7f, z: point.Z)) < -0.005f));

        // The block keeps the resolution its own lattice gave it (MinimumCell), finer than the union's lattice: its
        // vertices lie closer together than the union's cell.
        var block = positions.Where(predicate: static point => (Field(shape: Shapes[1], point: point) > -0.001f) && (Field(shape: Shapes[1], point: point) < 0.001f) && (point.Y > 0.65f)).ToArray();

        Assert.True(condition: (block.Length > 60), userMessage: $"the block holds {block.Length} vertices");
    }
    // Two slabs of two materials side by side, one static copy turned 30 degrees about up, share a flat top: their
    // boundary crosses the face along no crease and across the lattice's cells. The mesh cuts its triangles along it, so
    // all but a thousandth of the top within 3 cm of the boundary takes the material of the slab it lies over, where
    // splitting each quad into quarters steps the boundary across half a fine cell.
    [Fact]
    public void AMaterialBoundaryAcrossAFaceLiesWhereItsShapesMeet() {
        Shape[] slabs = [
            new(Center: new Vector3(x: -0.2f, y: 0.05f, z: 0f), Half: new Vector3(x: 0.2f, y: 0.05f, z: 0.3f), Rotation: Turn, Material: 0),
            new(Center: Vector3.Transform(value: new Vector3(x: 0.4f, y: 0f, z: 0f), rotation: Turn) + new Vector3(x: -0.2f, y: 0.05f, z: 0f), Half: new Vector3(x: 0.2f, y: 0.05f, z: 0.3f), Rotation: Turn, Material: 1),
        ];
        var (program, materials) = Program(shapes: slabs);
        var bake = new SdfMeshPathBake { Residency = "slabs" };
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Drive(bake: bake, frame: frame, materials: materials, shapes: slabs);

        var mesh = Assert.Single(collection: bake.Dress(frame: frame).MeshDraws).Mesh;
        var positions = mesh.Positions.ToArray();
        var indices = mesh.Indices.ToArray();
        var triangleMaterials = mesh.TriangleMaterials.ToArray();
        var wrong = 0f;
        var area = 0f;

        for (var triangle = 0; (triangle < triangleMaterials.Length); triangle++) {
            var (a, b, c) = (positions[(int)indices[(3 * triangle)]], positions[(int)indices[((3 * triangle) + 1)]], positions[(int)indices[((3 * triangle) + 2)]]);
            var centre = (((a + b) + c) / 3f);
            // The signed distance across the boundary, the plane x = 0 of the slabs' frame turned about the first slab's
            // inner face.
            var across = Vector3.Transform(value: (centre - new Vector3(x: -0.2f, y: 0.05f, z: 0f)), rotation: Quaternion.Conjugate(value: Turn)).X - 0.2f;

            if ((MathF.Abs(centre.Y - 0.1f) > 0.002f) || (MathF.Abs(across) > 0.03f)) {
                continue;
            }

            var size = (0.5f * Vector3.Cross(vector1: (b - a), vector2: (c - a)).Length());

            area += size;
            wrong += ((triangleMaterials[triangle] != (uint)materials[((across < 0f) ? 0 : 1)]) ? size : 0f);
        }

        Assert.True(condition: (area > 0f));
        Assert.True(condition: (wrong < (0.001f * area)), userMessage: $"{wrong / area:P3} of the top beside the boundary takes the other slab's material");
    }
    // Three slabs of three materials meeting at one point of a flat top keep all three about it, and a strip of a fourth
    // shape thinner than a cell across one of them, which no mesh vertex lies on, keeps its material where it crosses.
    [Fact]
    public void ThreeMaterialsMeetAtAPointAndAStripThinnerThanACellStays() {
        Shape[] slabs = [
            new(Center: new Vector3(x: -0.2f, y: 0.05f, z: 0f), Half: new Vector3(x: 0.2f, y: 0.05f, z: 0.3f), Rotation: Turn, Material: 0),
            new(Center: (Vector3.Transform(value: new Vector3(x: 0.4f, y: 0f, z: 0.15f), rotation: Turn) + new Vector3(x: -0.2f, y: 0.05f, z: 0f)), Half: new Vector3(x: 0.2f, y: 0.05f, z: 0.15f), Rotation: Turn, Material: 1),
            new(Center: (Vector3.Transform(value: new Vector3(x: 0.4f, y: 0f, z: -0.15f), rotation: Turn) + new Vector3(x: -0.2f, y: 0.05f, z: 0f)), Half: new Vector3(x: 0.2f, y: 0.05f, z: 0.15f), Rotation: Turn, Material: 2),
            new(Center: (Vector3.Transform(value: new Vector3(x: -0.1f, y: 0.0004f, z: 0f), rotation: Turn) + new Vector3(x: -0.2f, y: 0.05f, z: 0f)), Half: new Vector3(x: 0.002f, y: 0.05f, z: 0.3f), Rotation: Turn, Material: 2),
        ];
        var (program, materials) = Program(oneCopy: true, shapes: slabs);
        var bake = new SdfMeshPathBake { Residency = "junction" };
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Drive(bake: bake, frame: frame, materials: materials, shapes: slabs);

        var mesh = Assert.Single(collection: bake.Dress(frame: frame).MeshDraws).Mesh;
        var positions = mesh.Positions.ToArray();
        var indices = mesh.Indices.ToArray();
        var triangleMaterials = mesh.TriangleMaterials.ToArray();
        var junction = new HashSet<uint>();
        var strip = 0;

        for (var triangle = 0; (triangle < triangleMaterials.Length); triangle++) {
            var centre = (((positions[(int)indices[(3 * triangle)]] + positions[(int)indices[((3 * triangle) + 1)]]) + positions[(int)indices[((3 * triangle) + 2)]]) / 3f);
            var local = Vector3.Transform(value: (centre - new Vector3(x: -0.2f, y: 0.05f, z: 0f)), rotation: Quaternion.Conjugate(value: Turn));

            if (MathF.Abs(centre.Y - 0.1f) > 0.002f) { continue; }
            if (Vector2.Distance(value1: new Vector2(x: local.X, y: local.Z), value2: new Vector2(x: 0.2f, y: 0f)) < 0.02f) { _ = junction.Add(item: triangleMaterials[triangle]); }
            strip += (((MathF.Abs(local.X + 0.1f) < 0.002f) && (MathF.Abs(local.Z) < 0.25f) && (triangleMaterials[triangle] == (uint)materials[2])) ? 1 : 0);
        }

        Assert.Equal(expected: 3, actual: junction.Count);
        Assert.True(condition: (strip > 10), userMessage: $"{strip} triangles of the strip's material lie on it");
    }
    // A turned box whose edges are rounded by a little less than the lattice's cell: the contour puts a crease vertex
    // where its crossings' planes meet, at the faces' meeting line proud of the rounded edge in the cells whose crossings
    // miss the rounding, so the bake moves every vertex its planes placed that the field puts off the surface back onto it.
    // Every vertex lies within the check's tolerance of the box, a tenth of the 5 mm fine cell.
    [Fact]
    public void ARoundedEdgeKeepsItsVerticesOnTheRounding() {
        Shape[] boxes = [new(Center: new Vector3(x: 0.003f, y: 0.2f, z: -0.004f), Half: new Vector3(value: 0.15f), Rotation: Turn, Material: 0, Round: 0.012f)];
        var (program, materials) = Program(shapes: boxes);
        var bake = new SdfMeshPathBake { Residency = "rounded" };
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Drive(bake: bake, frame: frame, materials: materials, shapes: boxes);

        var positions = Assert.Single(collection: bake.Dress(frame: frame).MeshDraws).Mesh.Positions.ToArray();
        var worst = positions.Max(selector: point => MathF.Abs(Field(shape: boxes[0], point: point)));
        Assert.True(condition: (worst < 0.00055f), userMessage: $"a vertex lies {worst} from the rounded box");
    }
    [Fact]
    public void ASharpEdgeShadesAsTheFacesMeetingAtIt() {
        var (bake, frame) = Baked.Value;
        var lone = bake.Dress(frame: frame).MeshDraws.Select(selector: static draw => draw.Mesh).Single(predicate: static mesh => (mesh.Positions.Span[0].X > 6f));
        var positions = lone.Positions.ToArray();
        var normals = lone.Normals.ToArray();

        // The cube shades as its six faces: the normals a triangle's vertices interpolate at its centre, along its edges
        // as well as across its faces, are the face's own, but within a few cells of a corner. A normal read from the
        // field's gradients off the surface, as near an edge they point at the other face, bends a band along every
        // edge.
        var indices = lone.Indices.ToArray();
        var area = 0f;
        var bent = 0f;

        for (var triangle = 0; (triangle < indices.Length); triangle += 3) {
            var (a, b, c) = ((int)indices[triangle], (int)indices[(triangle + 1)], (int)indices[(triangle + 2)]);
            var centre = (((positions[a] + positions[b]) + positions[c]) / 3f);
            var local = (centre - Shapes[2].Center);

            if ((MathF.Abs(local.X) > 0.44f) && (MathF.Abs(local.Y) > 0.44f) && (MathF.Abs(local.Z) > 0.44f)) { continue; }

            var size = (0.5f * Vector3.Cross(vector1: (positions[b] - positions[a]), vector2: (positions[c] - positions[a])).Length());
            var normal = Vector3.Normalize(value: ((normals[a] + normals[b]) + normals[c]));
            var face = Vector3.Abs(value: local);
            var truth = ((face.X >= MathF.Max(face.Y, face.Z))
                ? new Vector3(x: MathF.Sign(local.X), y: 0f, z: 0f)
                : ((face.Y >= face.Z) ? new Vector3(x: 0f, y: MathF.Sign(local.Y), z: 0f) : new Vector3(x: 0f, y: 0f, z: MathF.Sign(local.Z))));

            area += size;
            bent += ((Vector3.Dot(vector1: normal, vector2: truth) < MathF.Cos(x: (10f * (MathF.PI / 180f)))) ? size : 0f);
        }

        Assert.True(condition: (bent < (0.03f * area)), userMessage: $"{bent / area:P2} of the cube's area shades more than 10 degrees off its face");
    }

    [Fact]
    public void ABakeOfGroupsItsStoreKeptLoadsThemAndRebakesOnlyAGroupWhoseEntryIsDamaged() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-store-");
        var (program, materials) = Program(shapes: Shapes);
        // A reach just inside the plate's bound, which the union's box, and so its geometry, holds.
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 4.25f };
        var directory = scratch.PathOf(name: "meshes");
        // Each run is a process of its own: a library of its own over the one store.
        SdfMeshPathBake Run(string name) {
            var bake = new SdfMeshPathBake { Identity = 5, Library = new SdfMeshLibrary(store: new SdfMeshStore(directory: directory)), Residency = name };

            Drive(bake: bake, frame: frame, materials: materials, shapes: Shapes);

            return bake;
        }
        Vector3[][] Meshes(SdfMeshPathBake bake) => [.. bake.Dress(frame: frame).MeshDraws.Select(selector: static draw => draw.Mesh.Positions.ToArray())];

        var baked = Run(name: "first");
        var entries = Directory.GetFiles(path: directory).OrderBy(keySelector: static entry => new FileInfo(fileName: entry).Length).ToArray();

        Assert.Equal(expected: 2, actual: entries.Length);
        Assert.Contains(expectedSubstring: "loaded=0", actualString: baked.Describe());

        // An unchanged program bakes nothing: every group's mesh is the one the first run meshed.
        var loaded = Run(name: "second");

        Assert.Contains(expectedSubstring: "dispatches=0 ", actualString: loaded.Describe());
        Assert.Contains(expectedSubstring: "loaded=2", actualString: loaded.Describe());
        Assert.Equal(expected: Meshes(bake: baked), actual: Meshes(bake: loaded));

        // A damaged entry is refused and its group alone bakes, its mesh kept again.
        File.WriteAllBytes(bytes: [1, 2, 3], path: entries[0]);

        var mended = Run(name: "third");

        Assert.Contains(expectedSubstring: "loaded=1", actualString: mended.Describe());
        Assert.DoesNotContain(expectedSubstring: "dispatches=0 ", actualString: mended.Describe());
        Assert.True(condition: (new FileInfo(fileName: entries[0]).Length > 3));
        Assert.Equal(expected: Meshes(bake: baked), actual: Meshes(bake: mended));
    }

    private readonly record struct Shape(Vector3 Center, Vector3 Half, Quaternion Rotation, int Material, float Round = 0f);

    private static float Field(Shape shape, Vector3 point) {
        var local = Vector3.Transform(value: (point - shape.Center), rotation: Quaternion.Conjugate(value: shape.Rotation));
        var q = (Vector3.Abs(value: local) - (shape.Half - new Vector3(value: shape.Round)));

        return ((Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f)) - shape.Round);
    }
    // The union of the first `count` shapes from `first`, and the material of the nearest.
    private static float Union(Vector3 point, int count, int first = 0) => Nearest(count: count, first: first, point: point, shapes: Shapes).Value;
    private static (float Value, int Shape) Nearest(Vector3 point, int count, int first, Shape[] shapes) {
        var best = (Value: float.PositiveInfinity, Shape: first);

        for (var shape = first; (shape < (first + count)); shape++) {
            var value = Field(shape: shapes[shape], point: point);

            if (value < best.Value) { best = (value, shape); }
        }

        return best;
    }
    // A blend group's shapes ride two slots inside one dynamic instance, its own and a second. However the frame places
    // the pair, the group bakes through its own slot at the identity and the second where it stands relative to it, as
    // its geometry hashed both, and never through the frame's transforms: two frames that move the pair together stage
    // the same slot poses, so its mesh is the same on every boot and whatever the body does while it bakes.
    [Fact]
    public void ADynamicGroupBakesThroughThePosesItsGeometryHashedWhereverTheFramePlacesIt() {
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.BeginInstanceDynamic(slot: 1, boundOffset: Vector3.Zero, boundRadius: 1f)
            .ResetPoint()
            .TransformDynamic(slot: 1)
            .Box(halfExtents: new Vector3(value: 0.2f), material: material, round: 0f)
            .ResetPoint()
            .TransformDynamic(slot: 2)
            .Box(halfExtents: new Vector3(value: 0.1f), material: material, round: 0f)
            .EndInstance()
            .Build();
        var offset = new Vector3(x: 0.3f, y: 0.05f, z: 0f);
        var hinge = Quaternion.CreateFromAxisAngle(angle: 0.4f, axis: Vector3.UnitX);

        (SdfMeshBakeBatch Batch, SdfMeshPathBake Bake) Take(Vector3 position, Quaternion orientation) {
            var bake = new SdfMeshPathBake { Residency = "pair" };
            var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) {
                DynamicTransforms = [default, new DynamicTransform(Orientation: orientation, Position: position), new DynamicTransform(Orientation: (orientation * hinge), Position: (position + Vector3.Transform(value: offset, rotation: orientation)))],
                FarDistance = 100f,
            };

            _ = bake.Dress(frame: frame);
            Assert.True(condition: bake.TryTakeForFrame(batch: out var batch, frame: 0L));

            return (batch, bake);
        }

        var turn = Quaternion.CreateFromAxisAngle(angle: 2.1f, axis: Vector3.Normalize(value: new Vector3(x: 1f, y: 2f, z: 3f)));
        var (near, _) = Take(orientation: Quaternion.Identity, position: Vector3.Zero);
        var (far, _) = Take(orientation: turn, position: new Vector3(x: 41.7f, y: -3.2f, z: 9.9f));
        var (first, rows) = Assert.Single(collection: near.Poses);
        var farRows = Assert.Single(collection: far.Poses).Rows;

        // The slots' rows agree; only the first row, the turn a dynamic bound's world-axis offset takes into the group's
        // frame, follows where the frame turned the pair.
        Assert.Equal(expected: 1, actual: first);
        Assert.Equal(expected: rows.Span[1..].ToArray(), actual: farRows.Span[1..].ToArray());
        Assert.Equal(expected: new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), actual: rows.Span[0]);
        Assert.Equal(expected: Quaternion.Inverse(value: turn).W, actual: farRows.Span[0].W, tolerance: 0.0001f);
        Assert.Equal(expected: [Vector4.Zero, new Vector4(x: 0f, y: 0f, z: 0f, w: 1f), Vector4.Zero], actual: rows.Span[1..4].ToArray());
        Assert.Equal(expected: 0.3f, actual: rows.Span[4].X, tolerance: 0.001f);
        Assert.Equal(expected: hinge.X, actual: rows.Span[5].X, tolerance: 0.001f);

        // The poses follow the batch's points in the point table, where the lattice's fourth row names them.
        var table = new Vector4[near.TableEntries];

        Assert.True(condition: near.WritePoints(table: table));
        Assert.Equal(expected: rows.ToArray(), actual: table.AsSpan(start: near.PoseBase(lattice: 0)).ToArray());
        Assert.Equal(expected: (near.Samples + rows.Length), actual: near.PoseBase(lattice: 1));
    }
    // A blend group's two shapes ride two slots, the group's own and a second turned on a hinge: each vertex of its mesh
    // rides the slot of the shape it lies on, so the second shape's vertices follow that slot as it moves (SdfMesh.Members),
    // the region lays the member and each vertex's member out for the vertex stage, and the store keeps them.
    [Fact]
    public void AGroupWhoseShapesRideTwoSlotsBakesEachVertexOntoItsOwnShapesSlot() {
        using var scratch = new TemporaryDirectory(prefix: "puck-mesh-skin-");
        var builder = new SdfProgramBuilder();
        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));
        var program = builder.BeginInstanceDynamic(slot: 1, boundOffset: Vector3.Zero, boundRadius: 1f)
            .ResetPoint()
            .TransformDynamic(slot: 1)
            .Box(halfExtents: new Vector3(value: 0.2f), material: material, round: 0f)
            .ResetPoint()
            .TransformDynamic(slot: 2)
            .Box(halfExtents: new Vector3(value: 0.1f), material: material, round: 0f)
            .EndInstance()
            .Build();
        var hinge = Quaternion.CreateFromAxisAngle(angle: 0.4f, axis: Vector3.UnitX);
        Shape[] pair = [
            new(Center: Vector3.Zero, Half: new Vector3(value: 0.2f), Rotation: Quaternion.Identity, Material: material),
            new(Center: new Vector3(x: 0.35f, y: 0f, z: 0f), Half: new Vector3(value: 0.1f), Rotation: hinge, Material: material),
        ];
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) {
            DynamicTransforms = [default, new DynamicTransform(Orientation: Quaternion.Identity, Position: Vector3.Zero), new DynamicTransform(Orientation: hinge, Position: pair[1].Center)],
            FarDistance = 100f,
        };
        var directory = scratch.PathOf(name: "meshes");
        SdfMeshPathBake Run(string name) {
            var bake = new SdfMeshPathBake { Identity = 7, Library = new SdfMeshLibrary(store: new SdfMeshStore(directory: directory)), Residency = name };
            var index = 0L;

            TestLiveness.Until(reason: bake.Describe, step: () => {
                _ = bake.Dress(frame: frame);
                if (bake.TryTakeForFrame(batch: out var batch, frame: index++)) {
                    bake.Record(batch: batch);
                    bake.Complete(batch: batch, samples: AnswerSlots(batch: batch, shapes: pair));
                }

                return !bake.Pending;
            });

            return bake;
        }

        var baked = Run(name: "first");
        var draw = Assert.Single(collection: baked.Dress(frame: frame).MeshDraws);
        var mesh = draw.Mesh;
        var member = Assert.Single(collection: mesh.Members.ToArray());
        var positions = mesh.Positions.ToArray();
        var vertexMembers = mesh.VertexMembers.ToArray();

        Assert.Contains(expectedSubstring: "skinned=1", actualString: baked.Describe());
        Assert.Equal(expected: 1, actual: member.SlotOffset);
        Assert.Equal(expected: 0.35f, actual: member.Position.X, tolerance: 0.001f);
        Assert.Equal(expected: hinge.X, actual: member.Orientation.X, tolerance: 0.001f);
        // Away from the seam each vertex rides the shape it lies on: the hinged box's its member, the group's box none.
        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            var own = Field(point: positions[vertex], shape: pair[0]);
            var other = Field(point: positions[vertex], shape: pair[1]);

            if (MathF.Abs(own - other) > 0.01f) {
                Assert.Equal(expected: ((other < own) ? (byte)0 : SdfMesh.NoMember), actual: vertexMembers[vertex]);
            }
        }
        Assert.Contains(collection: vertexMembers, filter: static member => (member == 0));
        Assert.Contains(collection: vertexMembers, filter: static member => (member == SdfMesh.NoMember));

        // The region's record flags the members and names their words: the count, the member and a word a vertex.
        var placements = new Dictionary<SdfMesh, SdfMeshRegionMesh>(comparer: ReferenceEqualityComparer.Instance);
        var layout = SdfMeshRegion.Plan(draws: [draw], meshes: placements);
        var words = new uint[layout.Words];

        SdfMeshRegion.Write(destination: words, draws: [draw], layout: layout, meshes: placements);

        var skin = (int)words[32];

        Assert.Equal(expected: SdfMeshRecord.SkinFlag, actual: (words[20] & SdfMeshRecord.SkinFlag));
        Assert.Equal(expected: 1u, actual: words[skin]);
        Assert.Equal(expected: 1u, actual: words[skin + 1]);
        Assert.Equal(expected: BitConverter.SingleToUInt32Bits(value: member.Orientation.W), actual: words[skin + 8]);
        for (var vertex = 0; (vertex < vertexMembers.Length); vertex++) {
            Assert.Equal(expected: ((vertexMembers[vertex] == SdfMesh.NoMember) ? uint.MaxValue : vertexMembers[vertex]), actual: words[skin + 1 + SdfMeshRecord.MemberWords + vertex]);
        }

        // Another run loads the group from the store with its members.
        var loaded = Run(name: "second");
        var reloaded = Assert.Single(collection: loaded.Dress(frame: frame).MeshDraws).Mesh;

        Assert.Contains(expectedSubstring: "loaded=1", actualString: loaded.Describe());
        Assert.Equal(expected: mesh.Members.ToArray(), actual: reloaded.Members.ToArray());
        Assert.Equal(expected: vertexMembers, actual: reloaded.VertexMembers.ToArray());
    }
    // The program: one static instance per shape with its tight bound, the second baking with the first, or with
    // `oneCopy` every shape after the first baking with the one before it.
    private static (SdfProgram Program, int[] Materials) Program(Shape[] shapes, bool oneCopy = false) {
        var builder = new SdfProgramBuilder();
        int[] materials = [builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX)), builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitY))];

        for (var index = 0; (index < shapes.Length); index++) {
            var shape = shapes[index];

            _ = builder.BeginInstance(bakesWithPrevious: (oneCopy ? (index > 0) : (index == 1)), boundCenter: shape.Center, boundRadius: (shape.Half.Length() + 0.05f))
                .ResetPoint()
                .Translate(offset: shape.Center)
                .Rotate(rotation: shape.Rotation)
                .Box(halfExtents: shape.Half, material: materials[shape.Material], round: shape.Round)
                .EndInstance();
        }

        return (builder.Build(), materials);
    }
    // Answers the bake's lattices frame by frame until it has meshed every group.
    private static void Drive(SdfMeshPathBake bake, SdfFrame frame, int[] materials, Shape[] shapes) {
        var index = 0L;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = bake.Dress(frame: frame);
            if (bake.TryTakeForFrame(batch: out var batch, frame: index++)) {
                bake.Record(batch: batch);
                bake.Complete(batch: batch, samples: Answer(batch: batch, materials: materials, shapes: shapes));
            }

            return !bake.Pending;
        });
    }
    // The bake kernel's answer to a batch: each lattice's samples, x fastest, grid or points, over the instances its
    // selector names.
    private static byte[] Answer(SdfMeshBakeBatch batch, int[] materials, Shape[] shapes) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var sample = 0;
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

        foreach (var lattice in batch.Lattices) {
            var first = -(lattice.Selector + 1);
            var spacing = (0.015625f * MathF.Min(lattice.Cell.X, MathF.Min(lattice.Cell.Y, lattice.Cell.Z)));

            for (var local = 0; (local < lattice.Samples); local++, sample++) {
                var position = (lattice.Points.IsEmpty
                    ? (lattice.Origin + (new Vector3(x: (local % lattice.Dims.X), y: ((local / lattice.Dims.X) % lattice.Dims.Y), z: (local / (lattice.Dims.X * lattice.Dims.Y))) * lattice.Cell))
                    : new Vector3(x: lattice.Points.Span[local].X, y: lattice.Points.Span[local].Y, z: lattice.Points.Span[local].Z));
                var (value, shape) = Nearest(count: lattice.Instances, first: first, point: position, shapes: shapes);
                var gradient = Vector3.Zero;

                if (lattice.Gradients) {
                    foreach (var tap in taps) {
                        gradient += (tap * Nearest(count: lattice.Instances, first: first, point: (position + (tap * spacing)), shapes: shapes).Value);
                    }
                    // The four taps sum to four times the spacing times the gradient.
                    gradient *= (0.25f / spacing);
                }

                var record = bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: value);
                BinaryPrimitives.WriteInt32LittleEndian(destination: record[4..], value: materials[shapes[shape].Material]);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
            }
        }

        return bytes;
    }
    // The bake kernel's answer to a pair's batch: each sample's value, material and tetrahedral gradient over the shapes, the
    // first riding slot one and the second slot two; a points lattice without gradients answers the winning shape's slot in
    // the first gradient word.
    private static byte[] AnswerSlots(SdfMeshBakeBatch batch, Shape[] shapes) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var sample = 0;
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

        foreach (var lattice in batch.Lattices) {
            var spacing = (0.015625f * MathF.Min(lattice.Cell.X, MathF.Min(lattice.Cell.Y, lattice.Cell.Z)));

            for (var local = 0; (local < lattice.Samples); local++, sample++) {
                var position = (lattice.Points.IsEmpty
                    ? (lattice.Origin + (new Vector3(x: (local % lattice.Dims.X), y: ((local / lattice.Dims.X) % lattice.Dims.Y), z: (local / (lattice.Dims.X * lattice.Dims.Y))) * lattice.Cell))
                    : new Vector3(x: lattice.Points.Span[local].X, y: lattice.Points.Span[local].Y, z: lattice.Points.Span[local].Z));
                var (value, shape) = Nearest(count: shapes.Length, first: 0, point: position, shapes: shapes);
                var record = bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: value);
                BinaryPrimitives.WriteInt32LittleEndian(destination: record[4..], value: shapes[shape].Material);
                if (lattice.Gradients) {
                    var gradient = Vector3.Zero;

                    foreach (var tap in taps) {
                        gradient += (tap * Nearest(count: shapes.Length, first: 0, point: (position + (tap * spacing)), shapes: shapes).Value);
                    }
                    gradient *= (0.25f / spacing);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
                } else if (!lattice.Points.IsEmpty) {
                    BinaryPrimitives.WriteInt32LittleEndian(destination: record[8..], value: (shape + 1));
                }
            }
        }

        return bytes;
    }
}
