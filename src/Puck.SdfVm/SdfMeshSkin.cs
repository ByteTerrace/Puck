using System.Numerics;
using Puck.Abstractions.Counting;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.SdfVm;

/// <summary>Reads one rest-frame influence tuple per exact mesh position, shared by every vertex copy there, from a skinned
/// group's own instructions (<see cref="SdfMeshSkinSource"/>), never from the program a bake carries.</summary>
public static class SdfMeshSkin {
    /// <summary>The most samples a rigid fallback's lattice holds per owner surface: a union final lattice's
    /// budget. A finer cell past it coarsens until the lattice fits.</summary>
    public const long SplitSamples = 1_048_576L;
    /// <summary>The counter source's name.</summary>
    public const string SourceName = "sdf.mesh-skin";

    /// <summary>Counts each skinned mesh a bake group starts drawing.</summary>
    public static readonly WorkKind Meshes = new(name: "sdf.mesh-skin.meshes", unit: "meshes", workClass: WorkClass.Pacing);
    /// <summary>Counts those meshes' vertices that ride the draw's own slot alone: the rigid path under the draw's matrix.</summary>
    public static readonly WorkKind RootVertices = new(name: "sdf.mesh-skin.root-vertices", unit: "vertices", workClass: WorkClass.Pacing);
    /// <summary>Counts those meshes' vertices that ride one moving member alone: the vertex stage's rigid path under that
    /// member's slot, their material layers in its frame.</summary>
    public static readonly WorkKind MemberVertices = new(name: "sdf.mesh-skin.member-vertices", unit: "vertices", workClass: WorkClass.Pacing);
    /// <summary>Counts those meshes' vertices that blend two owners' motions.</summary>
    public static readonly WorkKind BlendedVertices = new(name: "sdf.mesh-skin.blended-vertices", unit: "vertices", workClass: WorkClass.Pacing);

    private static readonly WorkKind[] DeclaredKinds = [Meshes, RootVertices, MemberVertices, BlendedVertices];

    /// <summary>Gets the kinds the counters count, in report order, for a reader that registers the source before a
    /// library exists.</summary>
    public static ReadOnlySpan<WorkKind> Kinds => DeclaredKinds;

    /// <summary>Creates the counters one library's bakes count into.</summary>
    /// <returns>A set over <see cref="Kinds"/>, every count zero.</returns>
    public static WorkCounterSet Counters() => new(kinds: DeclaredKinds, name: SourceName);

    /// <summary>Counts a skinned mesh a group starts drawing and its vertices by path; a mesh with no members counts
    /// nothing.</summary>
    /// <param name="counters">The counters (<see cref="Counters"/>).</param>
    /// <param name="mesh">The mesh.</param>
    public static void Count(WorkCounterSet counters, SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(argument: counters);
        ArgumentNullException.ThrowIfNull(argument: mesh);
        if (mesh.VertexMembers.IsEmpty) { return; }
        var (root, member, blended) = Paths(mesh: mesh);

        counters.Count(kind: Meshes);
        counters.Add(amount: root, kind: RootVertices);
        counters.Add(amount: member, kind: MemberVertices);
        counters.Add(amount: blended, kind: BlendedVertices);
    }

    /// <summary>Poses a stored skin in its group frame for CPU audit. Slot offsets name current rigid delta frames.
    /// The normalized dual quaternion uses the existing Maths carrier; the GPU counterpart uses presentation floats.</summary>
    /// <param name="mesh">The stored skin, in its group's frame.</param>
    /// <param name="poses">The current pose of each member slot, by offset from the group's slot.</param>
    /// <returns>The posed mesh; <paramref name="mesh"/> itself when it carries no members.</returns>
    public static SdfMesh Pose(SdfMesh mesh, IReadOnlyDictionary<int, DynamicTransform> poses) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: poses);
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
            var influence = (mesh.Influences.IsEmpty ? SdfMeshInfluence.Rigid : mesh.Influences.Span[vertex]);

            if (!influence.Blended) {
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
    /// <summary>Counts a skin's vertices by vertex-stage path and the positions whose copies carry different
    /// tuples.</summary>
    /// <param name="mesh">The skin.</param>
    /// <returns>The vertices riding the draw's slot alone, riding one member alone and blending two, and the positions
    /// whose copies disagree; all zero for a mesh with no members.</returns>
    public static (int Root, int Member, int Blended, int SplitPlaces) Audit(SdfMesh mesh) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        if (mesh.VertexMembers.IsEmpty) { return (0, 0, 0, 0); }
        var places = new Dictionary<Vector3, SdfMeshSkinTuple>();
        var split = new HashSet<Vector3>();

        for (var at = 0; (at < mesh.Positions.Length); at++) {
            var tuple = new SdfMeshSkinTuple(First: mesh.VertexMembers.Span[at], Influence: (mesh.Influences.IsEmpty ? SdfMeshInfluence.Rigid : mesh.Influences.Span[at]));
            var position = mesh.Positions.Span[at];

            if (!places.TryAdd(key: position, value: tuple) && (places[position] != tuple)) { split.Add(item: position); }
        }
        var (root, member, blended) = Paths(mesh: mesh);

        return (root, member, blended, split.Count);
    }
    // A skin's vertices by vertex-stage path: riding the draw's slot alone, riding one member alone, blending two.
    private static (int Root, int Member, int Blended) Paths(SdfMesh mesh) {
        var (root, member, blended) = (0, 0, 0);

        for (var vertex = 0; (vertex < mesh.VertexMembers.Length); vertex++) {
            if (!mesh.Influences.IsEmpty && mesh.Influences.Span[vertex].Blended) { blended++; } else if (mesh.VertexMembers.Span[vertex] == SdfMesh.NoMember) { root++; } else { member++; }
        }
        return (root, member, blended);
    }
    /// <summary>Bakes a nonrepresentable seam as rigid owner surfaces through the existing contour pipeline: each slot's
    /// own part (<see cref="SdfMeshSkinSource.MemberFields"/>) over the box its parts declare, at the group's detail or
    /// coarser where the lattice would pass <see cref="SplitSamples"/>.</summary>
    /// <param name="skin">The group's skin source.</param>
    /// <returns>The joined pieces, each vertex riding its piece's slot; <see langword="null"/> when no piece has a
    /// surface.</returns>
    public static SdfMesh? Split(SdfMeshSkinSource skin) {
        ArgumentNullException.ThrowIfNull(argument: skin);
        var group = skin.Group;
        var pieces = new List<SdfMesh>();
        var vertexMembers = new List<byte>();
        var members = group.Geometry.Poses.Where(predicate: p => (p.Key != group.Slot)).OrderBy(keySelector: p => p.Key).Select(selector: p => new SdfMeshMember((p.Key - group.Slot), p.Value.Position, p.Value.Orientation)).ToArray();
        var finest = ((group.Detail > 0f) ? MathF.Min(x: SdfMeshPathBake.MinimumCell, y: group.Detail) : SdfMeshPathBake.MinimumCell);

        foreach (var (slot, fieldProgram) in skin.MemberFields()) {
            using var field = new SdfMeshCpuField(program: fieldProgram);
            var (low, high) = skin.PartBox(slot: slot);
            var piece = SdfMeshPathBake.ContourLevel(field, low, high, SplitCell(low: low, high: high, finest: finest), 0f, group.Geometry.ContainsGlyph, false, null, SplitSamples, 0f, out _);

            if (piece is null) { continue; }
            pieces.Add(item: piece);
            var member = ((slot == group.Slot) ? SdfMesh.NoMember : (byte)Array.FindIndex(array: members, match: m => (m.SlotOffset == (slot - group.Slot))));

            vertexMembers.AddRange(collection: Enumerable.Repeat(member, piece.Positions.Length));
        }
        if (pieces.Count == 0) { return null; }
        var joined = SdfMeshChunks.Join(chunks: pieces);

        return new(joined.Positions, joined.Indices, joined.Normals, joined.TriangleMaterials, members, vertexMembers.ToArray());
    }
    /// <summary>Attaches propagated two-owner shares, one canonical tuple per exact position
    /// (<see cref="SdfMeshSkinTuple.Of"/>). A nonrepresentable group returns null and a reason for a rigid split.</summary>
    /// <param name="mesh">The group's contoured mesh, in its rest frame.</param>
    /// <param name="skin">The group's skin source.</param>
    /// <param name="refusal">Why the group cannot skin, or <see langword="null"/>.</param>
    /// <returns>The skin; <see langword="null"/> with <paramref name="refusal"/> set when the group cannot skin.</returns>
    public static SdfMesh? Build(SdfMesh mesh, SdfMeshSkinSource skin, out string? refusal) {
        ArgumentNullException.ThrowIfNull(argument: mesh);
        ArgumentNullException.ThrowIfNull(argument: skin);
        var group = skin.Group;
        var poses = group.Geometry.Poses;
        var slots = poses.Keys.Order().ToArray();
        var others = slots.Where(predicate: slot => (slot != group.Slot)).ToArray();

        if (others.Length > SdfMesh.MaxMembers) { throw new InvalidOperationException(message: "Skin member capacity exceeded."); }
        var members = others.Select(selector: slot => new SdfMeshMember((slot - group.Slot), poses[slot].Position, poses[slot].Orientation)).ToArray();
        var owners = skin.ShapeSlots().Select(selector: slot => Array.IndexOf(array: slots, value: slot)).ToArray();

        var rest = skin.Field();

        if (!SdfMeshAudit.TryEvaluator(rest, out var field, out refusal)) { return null; }
        var step = GradientStep(field: rest);
        var first = new byte[mesh.Positions.Length];
        var influences = new SdfMeshInfluence[first.Length];
        var places = new Dictionary<Vector3, SdfMeshSkinTuple>();
        var weights = new FixedQ4816[slots.Length];
        byte Member(int index) => ((slots[index] == group.Slot) ? SdfMesh.NoMember : (byte)Array.IndexOf(array: others, value: slots[index]));

        for (var vertex = 0; (vertex < first.Length); vertex++) {
            var point = mesh.Positions.Span[vertex];

            if (!places.TryGetValue(key: point, value: out var tuple)) {
                var fixedPoint = FixedPosition.FromLocal(local: FixedVector3.FromVector3(value: point));

                if (!field.ReadInfluences(position: fixedPoint, shapeOwners: owners, shares: weights)) { refusal = "field influence reading refused"; return null; }
                var (a, b, c) = Largest(slots: slots, weights: weights);

                if ((c >= 0) && (weights[c] > FixedQ4816.FromRawBits(value: 1024))) { refusal = "third owner share exceeds 1/64"; return null; }
                if (b < 0) { b = a; }
                // Slot order fixes which owner a blended tuple names first, independently of the changing share.
                if (slots[a] > slots[b]) { (a, b) = (b, a); }
                var total = (weights[a] + ((a == b) ? FixedQ4816.Zero : weights[b]));
                var share = (((a == b) || (total == FixedQ4816.Zero)) ? FixedQ4816.Zero : (weights[b] / total));
                var quantized = ((ushort)Math.Clamp((((share.Value * ushort.MaxValue) + 32768) / 65536), 0L, ushort.MaxValue));
                var gradient = FixedVector3.Zero;

                if (quantized is > 0 and < ushort.MaxValue) {
                    if (!field.ReadInfluenceGradient(fixedPoint, owners, slots.Length, a, b, step, out gradient)) { refusal = "share gradient left the field"; return null; }
                }
                tuple = SdfMeshSkinTuple.Of(first: Member(index: a), gradient: gradient.ToVector3(), second: Member(index: b), share: quantized);
                places.Add(key: point, value: tuple);
            }
            first[vertex] = tuple.First;
            influences[vertex] = tuple.Influence;
        }
        refusal = null;
        return new(mesh.Positions, mesh.Indices, mesh.Normals, mesh.TriangleMaterials, members, first, influences);
    }

    // The three largest owners by share, ties to the lower slot; -1 where fewer owners exist.
    private static (int A, int B, int C) Largest(int[] slots, FixedQ4816[] weights) {
        var (a, b, c) = (-1, -1, -1);

        for (var at = 0; (at < weights.Length); at++) {
            if (Before(at: at, than: a)) { (a, b, c) = (at, a, b); } else if (Before(at: at, than: b)) { (b, c) = (at, b); } else if (Before(at: at, than: c)) { c = at; }
        }
        return (a, b, c);

        bool Before(int at, int than) => ((than < 0) || (weights[at] > weights[than]) || ((weights[at] == weights[than]) && (slots[at] < slots[than])));
    }
    // The share gradient's central-difference step: a sixteenth of the group's narrowest smooth radius, so the stencil
    // stays inside the blend zone it differentiates, between 4 and 64 raws (1/16384 to 1/1024 unit).
    private static FixedQ4816 GradientStep(SdfProgram field) {
        var narrowest = float.PositiveInfinity;

        foreach (var instruction in field.Instructions) {
            if ((instruction.Op == SdfOp.ShapeBlend) && (instruction.Data1.X > 0f) && ((SdfBlendOp)instruction.Blend is SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)) {
                narrowest = MathF.Min(x: narrowest, y: instruction.Data1.X);
            }
        }
        var raws = (float.IsFinite(f: narrowest) ? ((narrowest / 16f) * 65536f) : 64f);

        return FixedQ4816.FromRawBits(value: (long)Math.Clamp(value: MathF.Round(x: raws), min: 4f, max: 64f));
    }
    // A cell no finer than `finest` whose lattice over the box, with the contour's two-cell margin, holds at most
    // SplitSamples.
    private static float SplitCell(Vector3 low, Vector3 high, float finest) {
        var cell = finest;

        for (var round = 0; (round < 64); round++) {
            var extent = ((high - low) + new Vector3(value: (4f * cell)));
            var count = ((((double)(MathF.Ceiling(x: (extent.X / cell)) + 1f)) * (MathF.Ceiling(x: (extent.Y / cell)) + 1f)) * (MathF.Ceiling(x: (extent.Z / cell)) + 1f));

            if (count <= SplitSamples) { break; }
            cell *= MathF.Cbrt(x: (float)(count / SplitSamples)) * 1.01f;
        }
        return cell;
    }
}
