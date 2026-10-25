using System.Buffers.Binary;
using System.Numerics;
using Puck.Shaders;
using Puck.SignedDistance;
using Puck.Testing;
using Xunit;

namespace Puck.SdfVm.Tests;

// The settle rebake (SdfMeshSettle), driven on the CPU: each group is a box on its own slot and a smaller box on the next
// slot, answered at the poses the batch carries, so a group planned again answers at its settled, snapped pose.
public sealed partial class SdfMeshPathBakeLawTests {
    private static readonly Vector3 OwnHalf = new(value: 0.2f);
    private static readonly Vector3 MemberHalf = new(value: 0.1f);

    [Fact]
    public void ACubicLatticeDispatchesWhenItsPosesExceedTheSampleBudget() {
        var bake = new SdfMeshPathBake { Library = new SdfMeshLibrary(), Residency = "posed-cube" };
        var frame = SettleFrame(groups: 1, memberGap: 100000, offset: 0f, program: SettleProgram(groups: 1, memberGap: 100000, scale: 4f), time: 0f);
        var index = 0L;
        var full = false;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = bake.Dress(frame: frame);
            if (bake.TryTakeForFrame(batch: out var batch, frame: index++)) {
                if (batch.TableEntries > SdfWorldPackage.MeshBakeMaxSamples) {
                    Assert.True(condition: (batch.TableEntries > batch.Samples));
                    var points = new Vector4[batch.TableEntries];
                    Assert.True(condition: batch.WritePoints(table: points));
                    full = true;
                }
                bake.Record(batch: batch);
                bake.Complete(batch: batch, samples: AnswerPosed(batch: batch, empty: false, memberGap: 100000, scale: 4f));
            }
            return !bake.Pending;
        });
        Assert.True(condition: full);
        Assert.Single(collection: bake.Dress(frame: frame).MeshDraws);
    }

    // A group whose member slides away bakes again only once the slide stops: a member still moving, however far from
    // its baked pose, queues nothing, and once it holds still for the settle window the group bakes at the pose snapped
    // to the settle steps while its skinned mesh keeps drawing. Another bake sharing the library takes that geometry's
    // mesh when its member settles near the same pose, baking nothing.
    [Fact]
    public void ASkinnedGroupBakesAgainOnlyOnceItsMemberSettlesAwayFromItsBakedPose() {
        var program = SettleProgram(groups: 1);
        var library = new SdfMeshLibrary();
        var bake = new SdfMeshPathBake { Library = library, Residency = "settle" };
        var index = 0L;
        var settles = library.Settles;

        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: program, time: 0f));
        Assert.True(condition: bake.Pending);
        Assert.False(condition: bake.IrradiancePending);
        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.35f, program: program, time: 0f), index: ref index);

        var baked = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: program, time: 0f)).MeshDraws).Mesh;

        // A member that keeps sliding never settles, however far it drifted.
        for (var time = 1f; (time < 2f); time += 0.05f) {
            _ = bake.Dress(frame: SettleFrame(groups: 1, offset: (0.45f + (0.02f * MathF.Sin(x: (10f * time)))), program: program, time: time));
            Assert.False(condition: bake.Pending);
        }
        Assert.Equal(expected: 0L, actual: settles.Read(kind: SdfMeshSettle.Queued));
        Assert.True(condition: (settles.Read(kind: SdfMeshSettle.Moving) >= 20L));

        // Held still, it waits out the settle window drawing the skinned mesh, then bakes again.
        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.45f, program: program, time: 2f));
        _ = bake.Dress(frame: SettleFrame(groups: 1, offset: 0.45f, program: program, time: 2.3f));
        Assert.False(condition: bake.Pending);

        var waiting = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.45f, program: program, time: 2.6f)).MeshDraws).Mesh;

        Assert.True(condition: bake.Pending);
        Assert.False(condition: bake.IrradiancePending);
        Assert.Same(actual: waiting, expected: baked);
        Assert.Equal(expected: 1L, actual: settles.Read(kind: SdfMeshSettle.Queued));
        DriveSettle(bake: bake, frame: SettleFrame(groups: 1, offset: 0.45f, program: program, time: 2.6f), index: ref index);

        var settled = Assert.Single(collection: bake.Dress(frame: SettleFrame(groups: 1, offset: 0.45f, program: program, time: 2.7f)).MeshDraws).Mesh;
        var member = Assert.Single(collection: settled.Members.ToArray());

        Assert.Equal(expected: 1L, actual: settles.Read(kind: SdfMeshSettle.Done));
        Assert.Equal(expected: (58f / SdfMeshSettle.PositionSteps), actual: member.Position.X, tolerance: 1.0e-5f);
        // The new mesh holds the member's box where it settled: a vertex on its far face, which the old one had at 0.45.
        Assert.Contains(collection: settled.Positions.ToArray(), filter: static point => (MathF.Abs(x: (point.X - (0.1f + (58f / SdfMeshSettle.PositionSteps)))) < 0.01f));

        // A bake sharing the library takes the settled geometry's mesh for a member settling near the same pose.
        var other = new SdfMeshPathBake { Library = library, Residency = "shared" };

        _ = other.Dress(frame: SettleFrame(groups: 1, offset: 0.35f, program: program, time: 0f));
        Assert.False(condition: other.Pending);
        _ = other.Dress(frame: SettleFrame(groups: 1, offset: 0.452f, program: program, time: 1f));
        _ = other.Dress(frame: SettleFrame(groups: 1, offset: 0.452f, program: program, time: 1.6f));
        Assert.False(condition: other.Pending);
        _ = other.Dress(frame: SettleFrame(groups: 1, offset: 0.452f, program: program, time: 1.7f));
        Assert.Equal(expected: 1L, actual: settles.Read(kind: SdfMeshSettle.Reused));
        Assert.Equal(expected: 2L, actual: settles.Read(kind: SdfMeshSettle.Done));
    }
    // Five groups settling at once start two rebakes and wait on the rest (SdfMeshSettle.MaxInFlight), and no more than
    // four start in a second (SdfMeshSettle.MaxPerSecond); a rebake that meshes no surface hands the group its mesh back,
    // and it waits for its member to move before it tries again.
    [Fact]
    public void SettleRebakesKeepTheirCapsAndARefusedOneKeepsItsMesh() {
        const int Groups = 5;
        var program = SettleProgram(groups: Groups);
        var library = new SdfMeshLibrary();
        var bake = new SdfMeshPathBake { Library = library, Residency = "capped" };
        var index = 0L;
        var settles = library.Settles;

        DriveSettle(bake: bake, frame: SettleFrame(groups: Groups, offset: 0.35f, program: program, time: 0f), index: ref index);
        _ = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.45f, program: program, time: 1f));
        _ = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.45f, program: program, time: 1.6f));
        Assert.Equal(expected: 2L, actual: settles.Read(kind: SdfMeshSettle.Queued));
        Assert.Equal(expected: 3L, actual: settles.Read(kind: SdfMeshSettle.Capped));

        // Within the second, four start and the fifth waits.
        DriveSettle(bake: bake, frame: SettleFrame(groups: Groups, offset: 0.45f, program: program, time: 1.6f), index: ref index, until: () => (settles.Read(kind: SdfMeshSettle.Done) == 4L));
        Assert.Equal(expected: 4L, actual: settles.Read(kind: SdfMeshSettle.Queued));
        DriveSettle(bake: bake, frame: SettleFrame(groups: Groups, offset: 0.45f, program: program, time: 2.7f), index: ref index, until: () => (settles.Read(kind: SdfMeshSettle.Done) == 5L));
        Assert.Equal(expected: 5L, actual: settles.Read(kind: SdfMeshSettle.Queued));

        // A clock that starts again forgets the starts; a settled pose meshing no surface keeps the mesh the group had.
        var before = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.45f, program: program, time: 2.8f)).MeshDraws.Select(selector: static draw => draw.Mesh).ToArray();

        _ = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.55f, program: program, time: 0f));
        _ = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.55f, program: program, time: 0.6f));
        Assert.Equal(expected: 7L, actual: settles.Read(kind: SdfMeshSettle.Queued));
        DriveSettle(bake: bake, empty: true, frame: SettleFrame(groups: Groups, offset: 0.55f, program: program, time: 0.6f), index: ref index, until: () => (settles.Read(kind: SdfMeshSettle.Refused) == 2L));

        var after = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.55f, program: program, time: 0.7f)).MeshDraws.Select(selector: static draw => draw.Mesh).ToArray();

        Assert.Equal(expected: before.Take(count: 2), actual: after.Take(count: 2));
        _ = bake.Dress(frame: SettleFrame(groups: Groups, offset: 0.55f, program: program, time: 5f));
        Assert.Equal(expected: 2L, actual: settles.Read(kind: SdfMeshSettle.Refused));
        Assert.Equal(expected: 9L, actual: settles.Read(kind: SdfMeshSettle.Queued));
    }

    // Groups of a box on slot 2g+1 and a smaller box memberGap slots later, smooth-free: each a dynamic instance on its own slot.
    private static SdfProgram SettleProgram(int groups, float scale = 1f, int memberGap = 1, int leadingMaterials = 0) {
        var builder = new SdfProgramBuilder();

        // Materials an earlier palette registers, which number the groups' own one later.
        for (var leading = 0; (leading < leadingMaterials); leading++) {
            _ = builder.AddMaterial(material: new SdfMaterial(Albedo: new Vector3(value: (0.25f + (0.25f * leading)))));
        }

        var material = builder.AddMaterial(material: new SdfMaterial(Albedo: Vector3.One));

        for (var group = 0; (group < groups); group++) {
            var slot = ((2 * group) + 1);

            _ = builder.BeginInstanceDynamic(slot: slot, boundOffset: Vector3.Zero, boundRadius: scale)
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
    private static SdfFrame SettleFrame(SdfProgram program, int groups, float offset, float time, int memberGap = 1) {
        var transforms = new DynamicTransform[((2 * groups) + memberGap)];

        for (var group = 0; (group < groups); group++) {
            var origin = new Vector3(x: (3f * group), y: 0f, z: 0f);

            transforms[((2 * group) + 1)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: origin);
            if (memberGap > 1) { transforms[((2 * group) + 2)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: origin); }
            transforms[((2 * group) + 1 + memberGap)] = new DynamicTransform(Orientation: Quaternion.Identity, Position: (origin + new Vector3(x: offset, y: 0f, z: 0f)));
        }

        return new SdfFrame(Program: program, ProgramChanged: false, Time: time, Views: []) { DynamicTransforms = transforms, FarDistance = 100f };
    }
    // Answers the bake's lattices frame by frame, at the frame's time, until it owes nothing or `until` holds; `empty`
    // answers every sample outside.
    private static void DriveSettle(SdfMeshPathBake bake, SdfFrame frame, ref long index, Func<bool>? until = null, bool empty = false) {
        var next = index;

        TestLiveness.Until(reason: bake.Describe, step: () => {
            _ = bake.Dress(frame: frame);
            if (bake.TryTakeForFrame(batch: out var batch, frame: next++)) {
                bake.Record(batch: batch);
                bake.Complete(batch: batch, samples: AnswerPosed(batch: batch, empty: empty));
            }

            return ((until?.Invoke() ?? false) || !bake.Pending);
        });
        index = next;
    }
    // The bake kernel's answer over a group's two boxes, the member's at the pose the batch carries for its slot.
    private static byte[] AnswerPosed(SdfMeshBakeBatch batch, bool empty, float scale = 1f, int memberGap = 1) {
        var bytes = new byte[((batch.Samples * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint))];
        var sample = 0;
        ReadOnlySpan<Vector3> taps = [new(x: 1f, y: -1f, z: -1f), new(x: -1f, y: -1f, z: 1f), new(x: -1f, y: 1f, z: -1f), new(x: 1f, y: 1f, z: 1f)];

        for (var lattice = 0; (lattice < batch.Lattices.Length); lattice++) {
            var request = batch.Lattices[lattice];

            var (first, rows) = batch.Poses[lattice];
            var member = ((request.Selector + memberGap) - first);
            var shapes = new Shape[] {
                new(Center: Vector3.Zero, Half: (OwnHalf * scale), Rotation: Quaternion.Identity, Material: 0),
                new(Center: new Vector3(x: rows.Span[(1 + (3 * member))].X, y: rows.Span[(1 + (3 * member))].Y, z: rows.Span[(1 + (3 * member))].Z), Half: (MemberHalf * scale), Rotation: Quaternion.Identity, Material: 0),
            };
            var spacing = (0.015625f * MathF.Min(x: request.Cell.X, y: MathF.Min(x: request.Cell.Y, y: request.Cell.Z)));

            for (var local = 0; (local < request.Samples); local++, sample++) {
                var position = (request.Points.IsEmpty
                    ? (request.Origin + (new Vector3(x: (local % request.Dims.X), y: ((local / request.Dims.X) % request.Dims.Y), z: (local / (request.Dims.X * request.Dims.Y))) * request.Cell))
                    : new Vector3(x: request.Points.Span[local].X, y: request.Points.Span[local].Y, z: request.Points.Span[local].Z));

                var (value, shape) = (empty ? (1f, 0) : Nearest(count: 2, first: 0, point: position, shapes: shapes));
                var record = bytes.AsSpan(start: ((sample * SdfWorldPackage.MeshBakeSampleWords) * sizeof(uint)));

                BinaryPrimitives.WriteSingleLittleEndian(destination: record, value: value);
                if (request.Gradients) {
                    var gradient = Vector3.Zero;

                    foreach (var tap in taps) {
                        gradient += (tap * (empty ? 1f : Nearest(count: 2, first: 0, point: (position + (tap * spacing)), shapes: shapes).Value));
                    }
                    gradient *= (0.25f / spacing);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[8..], value: gradient.X);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[12..], value: gradient.Y);
                    BinaryPrimitives.WriteSingleLittleEndian(destination: record[16..], value: gradient.Z);
                } else if (!request.Points.IsEmpty) {
                    BinaryPrimitives.WriteInt32LittleEndian(destination: record[8..], value: (request.Selector + shape));
                }
            }
        }

        return bytes;
    }
}
