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
        var (program, materials) = Program();
        var bake = new SdfMeshPathBake { Residency = "copies" };
        var frame = new SdfFrame(Program: program, ProgramChanged: true, Time: 0f, Views: []) { FarDistance = 100f };

        Drive(bake: bake, frame: frame, materials: materials);

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

    private readonly record struct Shape(Vector3 Center, Vector3 Half, Quaternion Rotation, int Material);

    private static float Field(Shape shape, Vector3 point) {
        var local = Vector3.Transform(value: (point - shape.Center), rotation: Quaternion.Conjugate(value: shape.Rotation));
        var q = (Vector3.Abs(value: local) - shape.Half);

        return (Vector3.Max(value1: q, value2: Vector3.Zero).Length() + MathF.Min(MathF.Max(q.X, MathF.Max(q.Y, q.Z)), 0f));
    }
    // The union of the first `count` shapes from `first`, and the material of the nearest.
    private static float Union(Vector3 point, int count, int first = 0) => Nearest(count: count, first: first, point: point).Value;
    private static (float Value, int Shape) Nearest(Vector3 point, int count, int first) {
        var best = (Value: float.PositiveInfinity, Shape: first);

        for (var shape = first; (shape < (first + count)); shape++) {
            var value = Field(shape: Shapes[shape], point: point);

            if (value < best.Value) { best = (value, shape); }
        }

        return best;
    }
    // The program: one static instance per shape with its tight bound, the block baking with the plate before it.
    private static (SdfProgram Program, int[] Materials) Program() {
        var builder = new SdfProgramBuilder();
        int[] materials = [builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One)), builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.UnitX))];

        for (var index = 0; (index < Shapes.Length); index++) {
            var shape = Shapes[index];

            _ = builder.BeginInstance(bakesWithPrevious: (index == 1), boundCenter: shape.Center, boundRadius: (shape.Half.Length() + 0.05f))
                .ResetPoint()
                .Translate(offset: shape.Center)
                .Rotate(rotation: shape.Rotation)
                .Box(halfExtents: shape.Half, material: materials[shape.Material], round: 0f)
                .EndInstance();
        }

        return (builder.Build(), materials);
    }
    // Answers the bake's lattices frame by frame until it has meshed every group.
    private static void Drive(SdfMeshPathBake bake, SdfFrame frame, int[] materials) {
        var index = 0L;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = bake.Dress(frame: frame);
            if (bake.TryTakeForFrame(batch: out var batch, frame: index++)) {
                bake.Record(batch: batch);
                bake.Complete(batch: batch, samples: Answer(batch: batch, materials: materials));
            }

            return !bake.Pending;
        });
    }
    // The bake kernel's answer to a batch: each lattice's samples, x fastest, grid or points, over the instances its
    // selector names.
    private static byte[] Answer(SdfMeshBakeBatch batch, int[] materials) {
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
                var (value, shape) = Nearest(count: lattice.Instances, first: first, point: position);
                var gradient = Vector3.Zero;

                if (lattice.Gradients) {
                    foreach (var tap in taps) {
                        gradient += (tap * Union(count: lattice.Instances, first: first, point: (position + (tap * spacing))));
                    }
                }

                var record = bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: value);
                BinaryPrimitives.WriteInt32LittleEndian(destination: record[4..], value: materials[Shapes[shape].Material]);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
            }
        }

        return bytes;
    }
}
