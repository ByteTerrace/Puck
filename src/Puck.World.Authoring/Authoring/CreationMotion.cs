using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>One blend of a pooled group that joins shapes moving independently (<see cref="CreationMotion.MovingBlends"/>).</summary>
/// <param name="Shape">The index of the shape whose blend joins the group.</param>
/// <param name="Other">The index of an earlier shape of the group that moves apart from it.</param>
/// <param name="Group">The group's id.</param>
/// <param name="Blend">The blend <paramref name="Shape"/> joins the group by.</param>
/// <param name="Operand">Whether either shape rides an operand owner (<see cref="CreationMotion.OperandShapes(CreationRig)"/>):
/// a cutter the group's bake reads where it stands, so the seam neither skins nor creases.</param>
public readonly record struct CreationMovingBlend(int Shape, int Other, int Group, SdfBlendOp Blend, bool Operand = false);
/// <summary>
/// What a creation's moving shapes do to its pooled blend groups, read off its compiled rig (<see cref="CreationRig"/>):
/// which seams join shapes that move apart, how far each can open, and which cutters move alone. The pooled motion plan
/// uses this reading to admit bounded rest-pose skins or partition a group into rigid owner fields. No presentation
/// motion is applied to simulation state.
/// </summary>
public static partial class CreationMotion {
    // A rig is immutable, so what it implies is read once: each moving blend's envelope walks boxes and pivots, and a
    // diagnostic describing every blend asks again for each.
    private sealed class Derived {
        public IReadOnlyList<CreationMovingBlend>? MovingBlends;
        public HashSet<int>? CreasedGroups;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<CreationRig, Derived> Memo = new();

    /// <summary>Resolves each shape's <see cref="ShapeDocument.Parent"/> to the index of the earlier shape it names, or
    /// -1 for a shape on the creation root. The canonicalizer refuses a parent that is missing or declared later.</summary>
    /// <param name="shapes">The creation's shapes.</param>
    /// <param name="parents">The parent of each shape, one entry a shape, at least as many as the shapes it resolves; a
    /// shape past its length is not resolved.</param>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> is <see langword="null"/>.</exception>
    public static void ResolveParents(IReadOnlyList<ShapeDocument> shapes, Span<int> parents) {
        ArgumentNullException.ThrowIfNull(argument: shapes);

        parents.Fill(value: -1);
        for (var child = 0; (child < Math.Min(val1: shapes.Count, val2: parents.Length)); child++) {
            if (shapes[child].Parent is not { } parent) { continue; }
            for (var candidate = 0; (candidate < child); candidate++) {
                if (string.Equals(a: shapes[candidate].Name?.Value, b: parent, comparisonType: StringComparison.Ordinal)) {
                    parents[child] = candidate;
                    break;
                }
            }
        }
    }
    /// <summary>Returns every blend of an animated creation's pooled groups that joins shapes moving independently: a
    /// shape whose blend is not <see cref="SdfBlendOp.Union"/> joining a group an earlier member of which inherits
    /// another shape's motion (<see cref="CreationRig.Owner"/>), or which a timeline frame poses apart from it. Every
    /// shape of a creation that does not animate joins by no such blend, and an ungrouped shape's blend is
    /// <see cref="UngroupedBlends"/>'s.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <returns>The blends, each joining shape paired with every earlier member it moves apart from, in shape
    /// order; empty when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<CreationMovingBlend> MovingBlends(CreationRig rig) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var derived = Memo.GetOrCreateValue(key: rig);

        return (derived.MovingBlends ??= FindMovingBlends(rig: rig));
    }
    private static IReadOnlyList<CreationMovingBlend> FindMovingBlends(CreationRig rig) {
        var shapes = (rig.Document.Shapes ?? []);

        if ((shapes.Count == 0) || !rig.Animates) { return []; }

        var found = new List<CreationMovingBlend>();
        var owners = rig.Owners;
        var operands = OperandShapes(keys: OwnerKeys(rig: rig), shapes: shapes);

        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var group = (shape.Group ?? 0);
            var blend = (shape.Blend ?? SdfBlendOp.Union);

            if ((group == 0) || (blend == SdfBlendOp.Union)) { continue; }
            for (var earlier = 0; (earlier < index); earlier++) {
                if (((shapes[earlier].Group ?? 0) == group) && OwnersMoveApart(rig: rig, first: owners[earlier], second: owners[index])) {
                    found.Add(item: new CreationMovingBlend(Blend: blend, Group: group, Operand: (operands[index] || operands[earlier]), Other: earlier, Shape: index));
                }
            }
        }

        return found;
    }
    /// <summary>Returns which shapes ride an operand owner: a motion owner other than the creation root
    /// (<see cref="CreationRig.Owner"/>) every shape of which carves (<see cref="SdfBoundAlgebra.Carves"/>). Such a cutter
    /// moves against the surface it cuts without a surface of its own, so the pooled bake reads it where it stands inside
    /// the group of what it cuts, as an operand (<see cref="CreationMotionPlan"/>), and plans that group again as it
    /// moves.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <returns>One entry a shape; all <see langword="false"/> for a creation whose shapes no writer moves apart.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static bool[] OperandShapes(CreationRig rig) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        return OperandShapes(keys: OwnerKeys(rig: rig), shapes: (rig.Document.Shapes ?? []));
    }
    /// <summary>Returns which shapes ride an operand owner, given each shape's owner as a key: shapes of one key move
    /// together, and key zero is the creation root, which is never an operand owner. A key is an operand owner when every
    /// shape it carries carves (<see cref="SdfBoundAlgebra.Carves"/>).</summary>
    /// <param name="shapes">The creation's shapes.</param>
    /// <param name="keys">Each shape's owner key, one a shape: zero for the root.</param>
    /// <returns>One entry a shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> is <see langword="null"/>.</exception>
    public static bool[] OperandShapes(IReadOnlyList<ShapeDocument> shapes, ReadOnlySpan<int> keys) {
        ArgumentNullException.ThrowIfNull(argument: shapes);

        var surfaced = new HashSet<int> { 0 };

        for (var index = 0; (index < shapes.Count); index++) {
            if (!SdfBoundAlgebra.Carves(blend: (shapes[index].Blend ?? SdfBlendOp.Union))) { _ = surfaced.Add(item: keys[index]); }
        }

        var operands = new bool[shapes.Count];

        for (var index = 0; (index < shapes.Count); index++) { operands[index] = !surfaced.Contains(item: keys[index]); }
        return operands;
    }
    /// <summary>Returns the groups an operand cuts that hold the surfaces of more than one owner key: each such group bakes
    /// one piece a surface owner (<see cref="CreationMotionPlan"/>), riding that owner's slot with the operand cut into
    /// it, because a group that reads an operand carries no skin.</summary>
    /// <param name="shapes">The creation's shapes.</param>
    /// <param name="keys">Each shape's owner key, one a shape, as <see cref="OperandShapes(IReadOnlyList{ShapeDocument}, ReadOnlySpan{int})"/> reads them.</param>
    /// <param name="operands">Which shapes ride an operand owner, one entry a shape.</param>
    /// <returns>The groups.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="shapes"/> or <paramref name="operands"/> is
    /// <see langword="null"/>.</exception>
    public static HashSet<int> SharedOperandGroups(IReadOnlyList<ShapeDocument> shapes, ReadOnlySpan<int> keys, bool[] operands) {
        ArgumentNullException.ThrowIfNull(argument: shapes);
        ArgumentNullException.ThrowIfNull(argument: operands);

        var cut = new HashSet<int>();
        var surfaces = new Dictionary<int, int>();

        for (var index = 0; (index < shapes.Count); index++) {
            var group = (shapes[index].Group ?? 0);

            if (group == 0) { continue; }
            if (operands[index]) {
                _ = cut.Add(item: group);
            } else if (surfaces.TryGetValue(key: group, value: out var key) && (key != keys[index])) {
                surfaces[group] = int.MinValue;
            } else if (!surfaces.ContainsKey(key: group)) {
                surfaces[group] = keys[index];
            }
        }
        cut.RemoveWhere(match: group => (surfaces.GetValueOrDefault(key: group, defaultValue: 0) != int.MinValue));
        return cut;
    }
    // Owner keys from a rig's owners: the root's -1 becomes zero.
    private static int[] OwnerKeys(CreationRig rig) {
        var owners = rig.Owners;
        var keys = new int[owners.Length];

        for (var index = 0; (index < owners.Length); index++) { keys[index] = (owners[index] + 1); }
        return keys;
    }
    /// <summary>Returns every ungrouped shape of an animated creation whose blend is not <see cref="SdfBlendOp.Union"/>
    /// and which follows another shape. The pooled bake gives each ungrouped shape a group of its own, so it joins its
    /// neighbours as crossing meshes, a hard union, whatever blend it declares; a static placement of the same creation
    /// bakes its shapes as one union and draws the blend. A group (<see cref="ShapeDocument.Group"/>) is the partition
    /// that keeps a pooled blend, which the author chooses: grouping moving parts stretches their seam
    /// (<see cref="MovingBlends"/>), so the bake never joins ungrouped shapes on its own.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <returns>The shapes' indices, in shape order; empty when there is none or the creation does not animate.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<int> UngroupedBlends(CreationRig rig) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var shapes = (rig.Document.Shapes ?? []);

        if ((shapes.Count < 2) || !rig.Animates || (rig.Document.Noise is not null)) { return []; }

        var found = new List<int>();

        for (var index = 1; (index < shapes.Count); index++) {
            if (((shapes[index].Group ?? 0) == 0) && ((shapes[index].Blend ?? SdfBlendOp.Union) != SdfBlendOp.Union)) {
                found.Add(item: index);
            }
        }

        return found;
    }
    /// <summary>Describes an ungrouped blend (<see cref="UngroupedBlends"/>) for a diagnostic: the shape by name, its
    /// blend, and what the pooled bake makes of it.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="shape">The shape's index, one <see cref="UngroupedBlends"/> returned.</param>
    /// <returns>One sentence, starting lowercase so a caller can lead it with the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static string DescribeUngrouped(CreationDocument document, int shape) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return $"blends ungrouped shape '{NameOf(document: document, index: shape)}' into the shapes before it by {(document.Shapes![shape].Blend ?? SdfBlendOp.Union)}: an animated creation bakes each ungrouped shape alone, so it draws as a hard union; give it and the shapes it blends into one group.";
    }
    /// <summary>Returns whether a moving blend's group bakes as rigid pieces, a crease at the seam: the blend or another
    /// moving blend of its group is outside its envelope (<see cref="Envelope"/>), so the pooled emitter partitions the
    /// whole group by motion owner (<see cref="CreationMotionPlan"/>). A blend whose group skins draws its seam smoothly
    /// and warrants no diagnostic.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <param name="blend">One of the rig's moving blends (<see cref="MovingBlends"/>).</param>
    /// <returns><see langword="true"/> when the group creases.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static bool Creases(CreationRig rig, CreationMovingBlend blend) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var derived = Memo.GetOrCreateValue(key: rig);

        return (derived.CreasedGroups ??= FindCreasedGroups(rig: rig)).Contains(item: blend.Group);
    }
    /// <summary>Returns the groups whose moving blends crease (<see cref="Creases"/>): each group holding a moving blend
    /// outside its envelope that no operand takes part in, and each group an operand cuts that holds more than one
    /// owner's surfaces (<see cref="SharedOperandGroups"/>).</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <returns>The groups, a set the caller owns.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static HashSet<int> CreasedGroups(CreationRig rig) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var derived = Memo.GetOrCreateValue(key: rig);

        return [.. (derived.CreasedGroups ??= FindCreasedGroups(rig: rig))];
    }
    private static HashSet<int> FindCreasedGroups(CreationRig rig) {
        var shapes = (rig.Document.Shapes ?? []);
        HashSet<int> creased = [.. MovingBlends(rig: rig).Where(predicate: blend => (!blend.Operand && !Envelope(blend: blend, rig: rig).Admitted)).Select(selector: static blend => blend.Group)];
        var keys = OwnerKeys(rig: rig);

        creased.UnionWith(other: SharedOperandGroups(keys: keys, operands: OperandShapes(keys: keys, shapes: shapes), shapes: shapes));
        return creased;
    }
    /// <summary>Describes a moving blend for a diagnostic: the two shapes by name, what moves each, the group and the
    /// blend, and what the bake makes of it.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <param name="blend">One of the rig's moving blends (<see cref="MovingBlends"/>).</param>
    /// <returns>One sentence, starting lowercase so a caller can lead it with the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static string Describe(CreationRig rig, CreationMovingBlend blend) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var document = rig.Document;

        if (blend.Operand) {
            var cutter = (OperandShapes(rig: rig)[blend.Shape] ? blend.Shape : blend.Other);

            return $"joins shape '{NameOf(document: document, index: blend.Shape)}' to group {blend.Group}'s shape '{NameOf(document: document, index: blend.Other)}' by {blend.Blend}: cutter '{NameOf(document: document, index: cutter)}' moves alone and cuts as an operand; the group bakes again each time it moves a step of the group's tier cell, so the cut trails it by at most one step, and contact still reads the cut at rest.";
        }

        var envelope = Envelope(blend: blend, rig: rig);
        var outcome = (envelope.Admitted ? "bakes as a rest-pose skin" : ((envelope.Refusal is not null) ? "bakes as a crease; a smooth skin requires bounded motion and a CPU-readable field" : $"bakes as a crease; widen smooth to at least {(2f * envelope.Displacement):G6} or reduce the motion to keep the blend"));

        if (envelope.Admitted && Creases(blend: blend, rig: rig)) {
            outcome = (OperandShapes(rig: rig).Where((operand, index) => (operand && (document.Shapes![index].Group == blend.Group))).Any()
                ? "bakes as a crease because a moving cutter cuts this group, which then carries no skin"
                : "bakes as a crease because another seam in this group is outside its envelope");
        }

        return $"joins shape '{NameOf(document: document, index: blend.Shape)}' to group {blend.Group}'s shape '{NameOf(document: document, index: blend.Other)}' by {blend.Blend}: theta={float.RadiansToDegrees(radians: envelope.Rotation):G6} deg (limit 60), D={envelope.Displacement:G6} (limit {(envelope.Smooth / 2f):G6}); {outcome}{((envelope.Refusal is { } reason) ? $" ({reason})" : string.Empty)}.";
    }
    /// <summary>Describes a motion a driver authors outside its mask (<see cref="CreationRig.Unplayed"/>), naming the
    /// driver, the member and the shape.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <param name="unplayed">One of the rig's unplayed motions.</param>
    /// <returns>The sentence, after the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static string DescribeUnplayed(CreationRig rig, CreationRigUnplayed unplayed) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        return $"driver '{rig.Drivers[unplayed.Driver].Document.Name}' authors {unplayed.Member} on shape '{NameOf(document: rig.Document, index: unplayed.Shape)}', which its parts do not cover, so it never plays; add a part that covers the shape, or drop the motion.";
    }
    /// <summary>Describes a later effector that writes a shape an earlier effector's tip hangs from
    /// (<see cref="CreationRig.Contests"/>), naming both and the shape.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <param name="contest">One of the rig's contests.</param>
    /// <returns>The sentence, after the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static string DescribeContest(CreationRig rig, CreationRigContest contest) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        return $"effector '{rig.Effectors[contest.Later].Document.Name}' writes shape '{NameOf(document: rig.Document, index: contest.Shape)}', which earlier effector '{rig.Effectors[contest.Earlier].Document.Name}''s tip hangs from; it solves last and holds its own target, so it can carry the earlier tip off its target.";
    }
    /// <summary>Returns a shape's name, or <c>#index</c> for an unnamed one.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="index">The shape's index.</param>
    /// <returns>The name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static string NameOf(CreationDocument document, int index) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return (document.Shapes![index].Name?.Value ?? $"#{index}");
    }

    // Whether two motion owners (CreationRig.Owner; -1 for the creation root) move against each other. Motion their
    // common ancestor carries moves both alike, so only the shapes on each side below it count: any writer there other
    // than a frame moves them apart, and otherwise every timeline frame must carry both sides by one rigid motion.
    private static bool OwnersMoveApart(CreationRig rig, int first, int second) {
        if (first == second) { return false; }
        var document = rig.Document;
        var (firstPath, secondPath) = Paths(first: first, parents: rig.Parents, second: second);

        if (firstPath.Concat(second: secondPath).Any(predicate: index => ((rig.Writers(shape: index) & ~CreationRigWriters.Frame) != CreationRigWriters.None))) { return true; }
        const float Tolerance = 1.0e-4f;

        foreach (var frame in (document.Frames ?? [])) {
            var (firstTurn, firstShift) = PathFrameMotion(document: document, frame: frame, path: firstPath);
            var (secondTurn, secondShift) = PathFrameMotion(document: document, frame: frame, path: secondPath);

            if ((MathF.Abs(x: Quaternion.Dot(quaternion1: firstTurn, quaternion2: secondTurn)) < (1f - Tolerance)) || (Vector3.Distance(value1: firstShift, value2: secondShift) > Tolerance)) {
                return true;
            }
        }

        return false;
    }
    // Each shape's own chain up to, not including, the two shapes' nearest common ancestor (-1 for the creation root),
    // nearest the shape first; a root-riding shape's chain is empty.
    private static (List<int> First, List<int> Second) Paths(ReadOnlySpan<int> parents, int first, int second) {
        var above = new HashSet<int>();

        for (var at = first; (at >= 0); at = parents[at]) { _ = above.Add(item: at); }
        var common = second;

        while ((common >= 0) && !above.Contains(item: common)) { common = parents[common]; }
        var firstPath = new List<int>();
        var secondPath = new List<int>();

        for (var at = first; ((at >= 0) && (at != common)); at = parents[at]) { firstPath.Add(item: at); }
        for (var at = second; ((at >= 0) && (at != common)); at = parents[at]) { secondPath.Add(item: at); }

        return (firstPath, secondPath);
    }
    // The rigid motion a frame carries the end of a chain by, the chain's own frame motions composed from the shape nearest
    // the chain's start outward: each parent's motion carries the child already moved.
    private static (Quaternion Turn, Vector3 Shift) PathFrameMotion(CreationDocument document, FrameDocument frame, List<int> path) {
        var (turn, shift) = (Quaternion.Identity, Vector3.Zero);

        foreach (var index in path) {
            var (outerTurn, outerShift) = FrameMotion(document: document, frame: frame, index: index);

            (turn, shift) = (Quaternion.Normalize(value: (outerTurn * turn)), (Vector3.Transform(rotation: outerTurn, value: shift) + outerShift));
        }

        return (turn, shift);
    }
    // The rigid motion a frame carries a shape by from its authored pose: the turn, then the shift after it.
    private static (Quaternion Turn, Vector3 Shift) FrameMotion(CreationDocument document, FrameDocument frame, int index) {
        var shape = document.Shapes![index];

        foreach (var transform in (frame.Transforms ?? [])) {
            if (transform.Id != shape.Id) { continue; }

            var turn = Quaternion.Normalize(value: (transform.Rotation.Value * Quaternion.Inverse(value: shape.Rotation.Value)));

            return (turn, (transform.Position.Value - Vector3.Transform(rotation: turn, value: shape.Position.Value)));
        }

        return (Quaternion.Identity, Vector3.Zero);
    }
}
