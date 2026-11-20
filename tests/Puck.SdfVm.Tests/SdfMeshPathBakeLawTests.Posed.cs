using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// Groups that read other slots, driven on the CPU: each group is a box on its own slot and a smaller box on the next slot,
// answered at the poses the batch carries, so a group planned again answers at the pose its geometry keyed.
public sealed partial class SdfMeshPathBakeLawTests {
    private static readonly Vector3 OwnHalf = new(value: 0.2f);
    private static readonly Vector3 MemberHalf = new(value: 0.1f);

    // A member this many slots past its group's own: the group's poses span three rows a slot over the gap, more point
    // table entries than one dispatch's samples (SdfWorldPackage.MeshBakeMaxSamples).
    private const int PosedGap = ((SdfWorldPackage.MeshBakeMaxSamples / 3) + 1);

    [Fact]
    public void ACubicLatticeDispatchesWhenItsPosesExceedTheSampleBudget() {
        using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "posed-cube" });
        var bake = bakeRun.Bake;
        var dresser = bakeRun.Dresser;
        var frame = PosedFrame(groups: 1, memberGap: PosedGap, offset: 0f, program: PosedProgram(groups: 1, memberGap: PosedGap, scale: 4f), time: 0f);
        var full = false;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = dresser.Dress(frame: frame);
            if (bake.TryTake(batch: out var batch, field: bake.Program)) {
                if (batch.InputRows > SdfWorldPackage.MeshBakeMaxSamples) {
                    Assert.True(condition: (batch.InputRows > batch.Samples));
                    var points = new Vector4[batch.InputRows];

                    Assert.True(condition: batch.WritePoints(table: points));
                    full = true;
                }
                bake.Complete(batch: batch, samples: AnswerPosed(batch: batch, empty: false, memberGap: PosedGap, scale: 4f));
            }
            return !bake.Pending;
        });
        Assert.True(condition: full);
        Assert.Single(collection: dresser.Dress(frame: frame).DressedDraws());
    }
    // A box on its own slot reading the next slot as an operand (SdfInstanceRange.Operands) bakes with the operand where
    // that slot stands, snapped to the group's step, and never as a skin member: a move within a step plans nothing, and
    // a move past one plans the group again while its mesh keeps drawing, the new mesh holding the operand's box where it
    // stands.
    [Fact]
    public void AGroupIsPlannedAgainWhenItsOperandMovesAStep() {
        var program = PosedProgram(groups: 1, operand: true);
        using var library = new SdfMeshLibrary();
        using var bakeRun = new MeshBakeTestRun(new SdfMeshPathBake { Library = library, PlannedTier = static _ => 0, Residency = "operand" }, ownsLibrary: false);
        var bake = bakeRun.Bake;
        var dresser = bakeRun.Dresser;
        var index = 0L;
        var inputs = library.Inputs;

        DrivePosed(bake: bake, dresser: dresser, frame: PosedFrame(groups: 1, offset: 0.351f, program: program, time: 0f), index: ref index);

        var baked = Assert.Single(collection: dresser.Dress(frame: PosedFrame(groups: 1, offset: 0.351f, program: program, time: 0.1f)).DressedDraws()).Mesh;

        Assert.Empty(collection: baked.Members.ToArray());
        _ = dresser.Dress(frame: PosedFrame(groups: 1, offset: 0.3511f, program: program, time: 0.2f));
        Assert.False(condition: bake.Pending);
        Assert.Equal(expected: 0L, actual: inputs.Read(kind: SdfMeshInputs.Operand));

        var waiting = Assert.Single(collection: dresser.Dress(frame: PosedFrame(groups: 1, offset: 0.451f, program: program, time: 0.3f)).DressedDraws()).Mesh;

        Assert.True(condition: bake.Pending);
        Assert.Same(actual: waiting, expected: baked);
        Assert.Equal(expected: 1L, actual: inputs.Read(kind: SdfMeshInputs.Operand));
        DrivePosed(bake: bake, dresser: dresser, frame: PosedFrame(groups: 1, offset: 0.451f, program: program, time: 0.4f), index: ref index);

        var moved = Assert.Single(collection: dresser.Dress(frame: PosedFrame(groups: 1, offset: 0.451f, program: program, time: 0.5f)).DressedDraws()).Mesh;

        Assert.NotSame(actual: moved, expected: baked);
        Assert.Empty(collection: moved.Members.ToArray());
        Assert.Contains(collection: moved.Positions.ToArray(), filter: static point => (MathF.Abs(x: (point.X - 0.551f)) < 0.01f));
        Assert.Equal(expected: 1L, actual: inputs.Read(kind: SdfMeshInputs.Operand));
    }

    // Groups of a box on slot 2g+1 and a smaller box memberGap slots later, smooth-free: each a dynamic instance on its own
    // slot, the smaller box read as an operand when `operand` holds.
    private static SdfProgram PosedProgram(int groups, float scale = 1f, int memberGap = 1, int leadingMaterials = 0, bool operand = false) {
        var builder = new SdfProgramBuilder();

        // Materials an earlier palette registers, which number the groups' own one later.
        for (var leading = 0; (leading < leadingMaterials); leading++) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: (0.25f + (0.25f * leading)))));
        }

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var group = 0; (group < groups); group++) {
            var slot = ((2 * group) + 1);

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: scale, operands: (operand ? [new SdfInstancePart(Detail: 0f, High: (MemberHalf * scale), Low: (-MemberHalf * scale), Slot: (slot + memberGap))] : null))
                .ResetPoint().TransformDynamic(slot: slot).Box(halfExtents: (OwnHalf * scale), material: material, round: 0f)
                .ResetPoint().TransformDynamic(slot: (slot + memberGap)).Box(halfExtents: (MemberHalf * scale), material: material, round: 0f);
            if (memberGap > 1) {
                // An enclosed detail records a second member pose without changing the union's surface.
                _ = builder.ResetPoint().TransformDynamic(slot: (slot + 1)).Sphere(radius: 0.01f, material: material);
            }
            _ = builder.EndInstance();
        }

        return builder.Build();
    }
    // A frame with each group's own slot 3 m along X from the last and its member `offset` along X from it.
    private static SdfFrame PosedFrame(SdfProgram program, int groups, float offset, float time, int memberGap = 1) {
        var transforms = new DynamicTransform[((2 * groups) + memberGap)];

        for (var group = 0; (group < groups); group++) {
            var origin = new Vector3(x: (3f * group), y: 0f, z: 0f);

            transforms[((2 * group) + 1)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: origin);
            if (memberGap > 1) { transforms[((2 * group) + 2)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: origin); }
            transforms[(((2 * group) + 1) + memberGap)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: (origin + new Vector3(x: offset, y: 0f, z: 0f)));
        }

        return new SdfFrame(Program: program, ProgramChanged: false, Time: time, Views: []) { DynamicTransforms = transforms, FarDistance = 100f };
    }
    // Answers the bake's lattices frame by frame, at the frame's time, until it owes nothing or `until` holds; `empty`
    // answers every sample outside.
    private static void DrivePosed(SdfMeshPathBake bake, SdfMeshDresser dresser, SdfFrame frame, ref long index, Func<bool>? until = null, bool empty = false) {
        var next = index;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = dresser.Dress(frame: frame);
            if (bake.TryTake(batch: out var batch, field: bake.Program)) {
                bake.Complete(batch: batch, samples: AnswerPosed(batch: batch, empty: empty));
            }

            return ((until?.Invoke() ?? false) || !bake.Pending);
        });
        index = next;
    }
    // The bake kernel's answer over a group's two boxes, the member's at the pose the batch carries for its slot.
    private static byte[] AnswerPosed(SdfMeshBakeBatch batch, bool empty, float scale = 1f, int memberGap = 1) {
        var bytes = new byte[batch.OutputBytes];
        var offset = 0;

        for (var lattice = 0; (lattice < batch.Lattices.Length); lattice++) {
            var request = batch.Lattices[lattice];

            var (first, rows) = batch.Poses[lattice];
            var member = ((request.Selector + memberGap) - first);
            var shapes = new Shape[] {
                new(Center: Vector3.Zero, Half: (OwnHalf * scale), Rotation: Quaternion.Identity, Material: 0),
                new(Center: new Vector3(x: rows.Span[(1 + (3 * member))].X, y: rows.Span[(1 + (3 * member))].Y, z: rows.Span[(1 + (3 * member))].Z), Half: (MemberHalf * scale), Rotation: Quaternion.Identity, Material: 0),
            };
            var spacing = request.TapSpacing;

            for (var local = 0; local < request.Samples; local++) {
                var position = request.SampleAt(local: local);

                var (value, shape) = (empty ? (1f, 0) : Nearest(count: 2, first: 0, point: position, shapes: shapes));
                var gradient = request.Gradients ? SdfMeshBakeTaps.Gradient(field: point => (empty ? 1f : Nearest(count: 2, first: 0, point: point, shapes: shapes).Value), point: position, spacing: spacing) : Vector3.Zero;
                request.WriteSample(bytes.AsSpan(offset, request.OutputBytes), local, value, 0, gradient);
            }
            offset += request.OutputBytes;
        }

        return bytes;
    }
}
