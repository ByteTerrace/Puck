using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Reads one rest-frame influence tuple per exact mesh position, shared by every vertex copy there.</summary>
public static class SdfMeshSkin {
    /// <summary>Poses a stored skin in its group frame for CPU audit. Slot offsets name current rigid delta frames.
    /// The normalized dual quaternion uses the existing Maths carrier; the GPU counterpart uses presentation floats.</summary>
    public static SdfMesh Pose(SdfMesh mesh, IReadOnlyDictionary<int, DynamicTransform> poses) {
        if (mesh.VertexMembers.IsEmpty) { return mesh; }
        var positions = new Vector3[mesh.Positions.Length];
        var normals = mesh.Normals.ToArray();

        (Quaternion Q, Vector3 T) Motion(byte member) {
            if (member == SdfMesh.NoMember) { return (Quaternion.Identity, Vector3.Zero); }
            var rest = mesh.Members.Span[member];

            if (!poses.TryGetValue(key: rest.SlotOffset, value: out var current)) { return (Quaternion.Identity, Vector3.Zero); }
            var q = Quaternion.Normalize(value: (current.Orientation * Quaternion.Inverse(value: rest.Orientation)));

            return (q, (current.Position - Vector3.Transform(rest.Position, q)));
        }
        for (var vertex = 0; (vertex < positions.Length); vertex++) {
            var p = mesh.Positions.Span[vertex];
            var n = ((normals.Length > 0) ? normals[vertex] : Vector3.UnitY);

            var (q0, t0) = Motion(member: mesh.VertexMembers.Span[vertex]);
            var influence = (mesh.Influences.IsEmpty ? default : mesh.Influences.Span[vertex]);

            if (influence.Share == 0) {
                positions[vertex] = (Vector3.Transform(rotation: q0, value: p) + t0);
                if (normals.Length > 0) { normals[vertex] = Vector3.Transform(rotation: q0, value: n); }
                continue;
            }
            var (q1, t1) = Motion(member: influence.Second);
            if (Quaternion.Dot(quaternion1: q0, quaternion2: q1) < 0f) { q1 = Quaternion.Negate(value: q1); }
            var a = FixedRigidTransform.FromRotationTranslation(rotation: FixedQuaternion.FromQuaternion(value: q0), translation: FixedVector3.FromVector3(value: t0));
            var b = FixedRigidTransform.FromRotationTranslation(rotation: FixedQuaternion.FromQuaternion(value: q1), translation: FixedVector3.FromVector3(value: t1));
            var w = FixedQ4816.FromDouble(value: (influence.Share / 65535d));
            var left = (FixedQ4816.One - w);
            var blended = FixedRigidTransform.FromDualQuaternion(value: new(((a.Value.Real * left) + (b.Value.Real * w)), ((a.Value.Dual * left) + (b.Value.Dual * w))));
            var rotation = blended.Rotation.ToQuaternion();
            var separation = (((Vector3.Transform(rotation: q1, value: p) + t1) - Vector3.Transform(rotation: q0, value: p)) - t0);
            var u = Vector3.Transform(separation, Quaternion.Conjugate(value: rotation));
            var v = influence.Gradient;

            positions[vertex] = (Vector3.Transform(rotation: rotation, value: p) + blended.Translation.ToVector3());
            if (normals.Length > 0) { normals[vertex] = Vector3.Normalize(value: Vector3.Transform((n - (v * (Vector3.Dot(vector1: u, vector2: n) / (1f + Vector3.Dot(vector1: u, vector2: v))))), rotation)); }
        }
        return new(positions, mesh.Indices, normals, mesh.TriangleMaterials, mesh.Members, mesh.VertexMembers, mesh.Influences) { BakeCell = mesh.BakeCell };
    }
    /// <summary>Counts blended vertices and positions whose copies carry different tuples.</summary>
    public static (int Blended, int SplitPlaces) Audit(SdfMesh mesh) {
        if (mesh.VertexMembers.IsEmpty) { return (0, 0); }
        var places = new Dictionary<Vector3, (byte, SdfMeshInfluence)>();
        var split = new HashSet<Vector3>();
        var blended = 0;

        for (var at = 0; (at < mesh.Positions.Length); at++) {
            var tuple = (mesh.VertexMembers.Span[at], (mesh.Influences.IsEmpty ? default : mesh.Influences.Span[at]));
            var position = mesh.Positions.Span[at];

            if (!places.TryAdd(key: position, value: tuple) && (places[position] != tuple)) { split.Add(item: position); }
            if (tuple.Item2.Blended) { blended++; }
        }
        return (blended, split.Count);
    }
    /// <summary>Bakes a nonrepresentable seam as rigid owner surfaces through the existing contour pipeline.</summary>
    public static SdfMesh? Split(SdfProgram program, SdfMeshBakeGroup group) {
        var pieces = new List<SdfMesh>();
        var vertexMembers = new List<byte>();
        var members = group.Geometry.Poses.Where(predicate: p => (p.Key != group.Slot)).OrderBy(keySelector: p => p.Key).Select(selector: p => new SdfMeshMember((p.Key - group.Slot), p.Value.Position, p.Value.Orientation)).ToArray();
        var cell = ((group.Detail > 0f) ? MathF.Min(x: SdfMeshPathBake.MinimumCell, y: group.Detail) : SdfMeshPathBake.MinimumCell);

        foreach (var (slot, fieldProgram) in SdfMeshAudit.MemberFields(group: group, program: program)) {
            using var field = new SdfMeshCpuField(program: fieldProgram);
            var parts = program.Instances[group.FirstInstance].Parts.Where(predicate: p => (p.Slot == slot)).ToArray();
            var low = ((parts.Length > 0) ? parts.Aggregate(new Vector3(value: float.MaxValue), (at, part) => Vector3.Min(value1: at, value2: part.Low)) : group.Low);
            var high = ((parts.Length > 0) ? parts.Aggregate(new Vector3(value: float.MinValue), (at, part) => Vector3.Max(value1: at, value2: part.High)) : group.High);
            var piece = SdfMeshPathBake.ContourLevel(field, low, high, cell, 0f, group.Geometry.ContainsGlyph, false, null, int.MaxValue, 0f, out _);

            if (piece is null) { continue; }
            pieces.Add(item: piece);
            var member = ((slot == group.Slot) ? SdfMesh.NoMember : (byte)Array.FindIndex(array: members, match: m => (m.SlotOffset == (slot - group.Slot))));

            vertexMembers.AddRange(collection: Enumerable.Repeat(member, piece.Positions.Length));
        }
        if (pieces.Count == 0) { return null; }
        var joined = SdfMeshChunks.Join(chunks: pieces);

        return new(joined.Positions, joined.Indices, joined.Normals, joined.TriangleMaterials, members, vertexMembers.ToArray());
    }
    /// <summary>Attaches propagated two-owner shares. A nonrepresentable group returns null and a reason for a rigid split.</summary>
    public static SdfMesh? Build(SdfMesh mesh, SdfProgram program, SdfMeshBakeGroup group, out string? refusal) {
        var poses = group.Geometry.Poses;
        var slots = poses.Keys.Order().ToArray();
        var others = slots.Where(predicate: slot => (slot != group.Slot)).ToArray();

        if (others.Length > SdfMesh.MaxMembers) { throw new InvalidOperationException(message: "Skin member capacity exceeded."); }
        var members = others.Select(selector: slot => new SdfMeshMember((slot - group.Slot), poses[slot].Position, poses[slot].Orientation)).ToArray();
        var owners = new List<int>();
        var owner = group.Slot;

        foreach (var index in group.InstructionIndices(program: program)) {
            var instruction = program.Instructions[index];

            if (instruction.Op == SdfOp.ResetPoint) { owner = group.Slot; }
            if (instruction.Op == SdfOp.TransformDynamic) { owner = ((int)instruction.Data0.X); }
            if (instruction.Op == SdfOp.ShapeBlend) { owners.Add(item: Array.IndexOf(array: slots, value: owner)); }
        }
        if (!SdfMeshAudit.TryEvaluator(SdfMeshAudit.Field(program, group), out var field, out refusal)) { return null; }
        var first = new byte[mesh.Positions.Length];
        var influences = new SdfMeshInfluence[first.Length];
        var places = new Dictionary<Vector3, (byte First, SdfMeshInfluence Influence)>();
        var weights = new FixedQ4816[slots.Length];

        for (var vertex = 0; (vertex < first.Length); vertex++) {
            var point = mesh.Positions.Span[vertex];

            if (!places.TryGetValue(key: point, value: out var tuple)) {
                var fixedPoint = FixedVector3.FromVector3(value: point);

                if (!field.ReadInfluences(position: FixedPosition.FromLocal(local: fixedPoint), shapeOwners: owners, shares: weights)) { refusal = "field influence reading refused"; return null; }
                var order = Enumerable.Range(0, weights.Length).OrderByDescending(keySelector: at => weights[at]).ThenBy(keySelector: at => slots[at]).ToArray();

                if ((order.Length > 2) && (weights[order[2]] > FixedQ4816.FromRawBits(value: 1024))) { refusal = "third owner share exceeds 1/64"; return null; }
                var a = order[0];
                var b = ((order.Length > 1) ? order[1] : a);
                // Slot order fixes the quaternion hemisphere reference, independently of the changing share.
                if (slots[a] > slots[b]) { (a, b) = (b, a); }
                var total = (weights[a] + ((a == b) ? FixedQ4816.Zero : weights[b]));
                var share = (((a == b) || (total == FixedQ4816.Zero)) ? FixedQ4816.Zero : (weights[b] / total));
                var gradient = FixedVector3.Zero;

                if ((share > FixedQ4816.Zero) && (share < FixedQ4816.One)) {
                    if (!field.ReadInfluenceGradient(FixedPosition.FromLocal(local: fixedPoint), owners, slots.Length, a, b, out gradient)) { refusal = "share gradient left the field"; return null; }
                }
                byte Member(int index) => ((slots[index] == group.Slot) ? SdfMesh.NoMember : (byte)Array.IndexOf(array: others, value: slots[index]));
                var quantized = ((ushort)Math.Clamp((((share.Value * ushort.MaxValue) + 32768) / 65536), 0L, ushort.MaxValue));

                tuple = (Member(index: a), SdfMeshInfluence.Pack(Member(index: b), quantized, gradient.ToVector3()));
                places.Add(key: point, value: tuple);
            }
            first[vertex] = tuple.First;
            influences[vertex] = tuple.Influence;
        }
        refusal = null;
        return new(mesh.Positions, mesh.Indices, mesh.Normals, mesh.TriangleMaterials, members, first, influences);
    }
}
