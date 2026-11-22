using System.Numerics;
using Puck.SignedDistance;
using Xunit;

namespace Puck.SdfVm.Tests;

// The host bound of a skinned draw (SdfMeshBox.TryOfSkin) is the box every pass selects the draw by once a staging writes
// it into the record, so a vertex the kernels pose outside it is culled at a view's edge. These laws pose a skin through
// its CPU twin (SdfMeshSkin.Pose) at the envelope's extremes, every member turned up to half a turn and moved by up to
// the rest box's span, blended and rigid vertices alike, and require every posed vertex inside the bound.
public sealed class SdfMeshSkinBoundLawTests {
    private const int Root = 4;
    private static readonly Vector3[] MemberRests = [new(x: 0.4f, y: 0f, z: 0f), new(x: -0.4f, y: 0.1f, z: 0f), new(x: 0f, y: 0.45f, z: -0.2f)];

    [Fact]
    public void EveryPosedVertexLiesInsideTheHostBound() {
        var mesh = Skin();
        var random = new Random(Seed: 20261120);
        var worst = 0f;

        for (var trial = 0; (trial < 400); trial++) {
            // A turn of at least 170 degrees on every fourth trial: the blend's widest screw.
            var poses = new Dictionary<int, DynamicTransform>();

            foreach (ref readonly var member in mesh.Members.Span) {
                var angle = (((trial % 4) == 0) ? (MathF.PI * (0.945f + (0.055f * random.NextSingle()))) : (MathF.PI * random.NextSingle()));

                poses[member.SlotOffset] = new(Position: (member.Position + (RandomUnit(random: random) * random.NextSingle())), Orientation: Quaternion.CreateFromAxisAngle(axis: RandomUnit(random: random), angle: angle));
            }

            var rootTurn = Quaternion.CreateFromAxisAngle(axis: RandomUnit(random: random), angle: (MathF.Tau * random.NextSingle()));
            var rootPosition = (RandomUnit(random: random) * 20f);
            var scale = (0.5f + (2f * random.NextSingle()));
            var transforms = Transforms(poses: poses, rootPosition: rootPosition, rootTurn: rootTurn, scale: scale);
            var matrix = ((Matrix4x4.CreateScale(scale: scale) * Matrix4x4.CreateFromQuaternion(quaternion: rootTurn)) * Matrix4x4.CreateTranslation(position: rootPosition));
            var draw = new SdfMeshDraw(identity: new object(), material: 0, mesh: mesh, objectToWorld: matrix) { FrameSlot = Root };

            Assert.True(condition: SdfMeshBox.TryOfSkin(box: out var box, draw: in draw, transforms: transforms));
            Assert.True(condition: Matrix4x4.Invert(matrix: new Matrix4x4(
                m11: box.HalfX.X, m12: box.HalfX.Y, m13: box.HalfX.Z, m14: 0f,
                m21: box.HalfY.X, m22: box.HalfY.Y, m23: box.HalfY.Z, m24: 0f,
                m31: box.HalfZ.X, m32: box.HalfZ.Y, m33: box.HalfZ.Z, m34: 0f,
                m41: 0f, m42: 0f, m43: 0f, m44: 1f), result: out var unbox));

            foreach (var vertex in SdfMeshSkin.Pose(mesh: mesh, poses: poses).Positions.Span) {
                var local = Vector3.Abs(value: Vector3.Transform(position: (Vector3.Transform(position: vertex, matrix: matrix) - box.Center), matrix: unbox));

                worst = MathF.Max(x: worst, y: MathF.Max(x: local.X, y: MathF.Max(x: local.Y, y: local.Z)));
            }
        }

        // The bound's own coordinate of the farthest vertex: at most one inside it.
        Assert.InRange(actual: worst, low: 0f, high: 1f);
    }

    // A skin over a lattice of its rest box: vertices riding the root, one member alone, or two blended at every share.
    private static SdfMesh Skin() {
        var positions = new List<Vector3>();
        var owners = new List<byte>();
        var influences = new List<SdfMeshInfluence>();
        var shares = new ushort[] { 0, 8192, 16384, 32768, 49152, 57344 };

        for (var x = 0; (x < 5); x++) {
            for (var y = 0; (y < 5); y++) {
                for (var z = 0; (z < 5); z++) {
                    var index = positions.Count;
                    var first = ((index % 4) == 3) ? SdfMesh.NoMember : ((byte)(index % 3));
                    var second = ((byte)((index + 1) % 3));
                    var tuple = ((first == SdfMesh.NoMember)
                        ? new SdfMeshSkinTuple(First: first, Influence: SdfMeshInfluence.Rigid)
                        : SdfMeshSkinTuple.Of(first: first, gradient: Vector3.Zero, second: second, share: shares[(index % shares.Length)]));

                    positions.Add(item: new Vector3(x: ((x * 0.25f) - 0.5f), y: ((y * 0.25f) - 0.5f), z: ((z * 0.25f) - 0.5f)));
                    owners.Add(item: tuple.First);
                    influences.Add(item: tuple.Influence);
                }
            }
        }

        var members = MemberRests.Select(selector: static (rest, index) => new SdfMeshMember(Orientation: Quaternion.Identity, Position: rest, SlotOffset: (index + 1))).ToArray();
        var indices = Enumerable.Range(start: 0, count: ((positions.Count / 3) * 3)).Select(selector: static index => ((uint)index)).ToArray();

        return new SdfMesh(indices: indices, influences: influences.ToArray(), members: members, positions: positions.ToArray(), vertexMembers: owners.ToArray());
    }
    // The world transforms of a root slot and its members, which place each member's group-frame pose where the draw's
    // matrix places the group.
    private static DynamicTransform[] Transforms(Dictionary<int, DynamicTransform> poses, Vector3 rootPosition, Quaternion rootTurn, float scale) {
        var transforms = new DynamicTransform[(Root + MemberRests.Length + 1)];

        Array.Fill(array: transforms, value: new DynamicTransform(Position: Vector3.Zero, Orientation: Quaternion.Identity));
        transforms[Root] = new(Position: rootPosition, Orientation: rootTurn) { Scale = scale };
        foreach (var (offset, pose) in poses) {
            transforms[(Root + offset)] = new(Position: (rootPosition + Vector3.Transform(rotation: rootTurn, value: (pose.Position * scale))), Orientation: (rootTurn * pose.Orientation));
        }

        return transforms;
    }
    private static Vector3 RandomUnit(Random random) {
        var value = new Vector3(x: ((random.NextSingle() * 2f) - 1f), y: ((random.NextSingle() * 2f) - 1f), z: ((random.NextSingle() * 2f) - 1f));

        return ((value.LengthSquared() > 1e-6f) ? Vector3.Normalize(value: value) : Vector3.UnitX);
    }
}
