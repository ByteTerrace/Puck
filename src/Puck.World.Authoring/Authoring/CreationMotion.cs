using System.Numerics;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>One blend of a pooled group that joins shapes moving independently (<see cref="CreationMotion.MovingBlends"/>).</summary>
/// <param name="Shape">The index of the shape whose blend joins the group.</param>
/// <param name="Other">The index of an earlier shape of the group that moves apart from it.</param>
/// <param name="Group">The group's id.</param>
/// <param name="Blend">The blend <paramref name="Shape"/> joins the group by.</param>
public readonly record struct CreationMovingBlend(int Shape, int Other, int Group, SdfBlendOp Blend);
/// <summary>
/// What moves a creation's shapes, read off its document: whether it animates at all, and which shape's motion each
/// shape inherits. An animated creation's shapes each ride a slot of their own in the stamp pool, and a pooled blend
/// group bakes as one mesh whose vertices each ride one shape's slot, so a blend between shapes that move apart keeps
/// the seam it baked with until the shapes settle (<c>Puck.SdfVm.SdfMeshSettle</c>).
/// </summary>
public static class CreationMotion {
    /// <summary>Returns whether a creation animates: a driver or an inverse-kinematics effector moves it, or a timeline
    /// frame poses a shape away from its authored position or rotation (a state-bound value always differs from a
    /// literal). A creation whose frames carry no transforms, or only its shapes' authored poses, moves nothing.</summary>
    /// <param name="document">The creation document.</param>
    /// <returns><see langword="true"/> when the creation's drivers, effectors or frames move a shape.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static bool Animates(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        if ((document.Drivers is { Count: > 0 }) || (document.Effectors is { Count: > 0 })) { return true; }

        var shapes = (document.Shapes ?? []);

        for (var index = 0; (index < shapes.Count); index++) {
            if (FramesMove(document: document, index: index)) { return true; }
        }

        return false;
    }
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
    /// <summary>Returns the shape whose own motion a shape inherits: the nearest of the shape and its parents that swings,
    /// slides or lies on an effector's chain, or -1 when only the creation root moves it. Two shapes of one owner keep
    /// their places relative to each other under every driver and effector.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="parents">Each shape's parent (<see cref="ResolveParents"/>).</param>
    /// <param name="index">The shape's index.</param>
    /// <returns>The owner's index, or -1 for the creation root.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static int Owner(CreationDocument document, ReadOnlySpan<int> parents, int index) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var shapes = document.Shapes!;

        while (index >= 0) {
            var shape = shapes[index];

            if ((shape.Swings is { Count: > 0 }) || (shape.Slides is { Count: > 0 })) { return index; }
            foreach (var effector in (document.Effectors ?? [])) {
                if ((shape.Name is { } name) && effector.Chain.Contains(value: name.Value)) { return index; }
            }
            index = parents[index];
        }

        return -1;
    }
    /// <summary>Returns every blend of an animated creation's pooled groups that joins shapes moving independently: a
    /// shape whose blend is not <see cref="SdfBlendOp.Union"/> joining a group an earlier member of which inherits
    /// another shape's motion (<see cref="Owner"/>), or which a timeline frame poses apart from it. Ungrouped shapes, and
    /// every shape of a creation that does not animate, join by no such blend.</summary>
    /// <param name="document">The creation document.</param>
    /// <returns>The blends, one per joining shape naming the first earlier member it moves apart from, in shape
    /// order; empty when there is none.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<CreationMovingBlend> MovingBlends(CreationDocument document) {
        ArgumentNullException.ThrowIfNull(argument: document);

        var shapes = (document.Shapes ?? []);

        if ((shapes.Count == 0) || !Animates(document: document)) { return []; }

        var parents = new int[shapes.Count];
        var owners = new int[shapes.Count];
        var found = new List<CreationMovingBlend>();

        ResolveParents(parents: parents, shapes: shapes);
        for (var index = 0; (index < shapes.Count); index++) {
            owners[index] = Owner(document: document, index: index, parents: parents);
        }
        for (var index = 0; (index < shapes.Count); index++) {
            var shape = shapes[index];
            var group = (shape.Group ?? 0);
            var blend = (shape.Blend ?? SdfBlendOp.Union);

            if ((group == 0) || (blend == SdfBlendOp.Union)) { continue; }
            for (var earlier = 0; (earlier < index); earlier++) {
                if (((shapes[earlier].Group ?? 0) == group) && ((owners[earlier] != owners[index]) || !FramesAlike(document: document, first: earlier, second: index))) {
                    found.Add(item: new CreationMovingBlend(Blend: blend, Group: group, Other: earlier, Shape: index));
                    break;
                }
            }
        }

        return found;
    }
    /// <summary>Describes a moving blend for a diagnostic: the two shapes by name, what moves each, the group and the
    /// blend, and what the bake makes of it.</summary>
    /// <param name="document">The creation document.</param>
    /// <param name="blend">One of the document's moving blends (<see cref="MovingBlends"/>).</param>
    /// <returns>One sentence, starting lowercase so a caller can lead it with the creation's name.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is <see langword="null"/>.</exception>
    public static string Describe(CreationDocument document, CreationMovingBlend blend) {
        ArgumentNullException.ThrowIfNull(argument: document);

        return $"joins shape '{NameOf(document: document, index: blend.Shape)}', which {MotionOf(document: document, index: blend.Shape)}, to group {blend.Group}'s shape '{NameOf(document: document, index: blend.Other)}', which {MotionOf(document: document, index: blend.Other)}, by {blend.Blend}: the group bakes one mesh at the pose its program arrives in, so the seam stretches as they move apart until their relative pose settles and the group bakes again there.";
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

    // What moves a shape, as a diagnostic phrase: a timeline frame, its own motion, the shape it inherits motion from,
    // or the creation root.
    private static string MotionOf(CreationDocument document, int index) {
        var shapes = document.Shapes!;

        if (FramesMove(document: document, index: index)) { return "is posed by a timeline frame"; }

        var parents = new int[shapes.Count];

        ResolveParents(parents: parents, shapes: shapes);

        var owner = Owner(document: document, index: index, parents: parents);

        return ((owner < 0)
            ? "rides the creation root"
            : ((owner == index) ? "moves by its own swings, slides or effector" : $"moves with '{NameOf(document: document, index: owner)}'"));
    }
    // Whether every timeline frame moves two shapes by one rigid motion from their authored poses, so the frames keep
    // their places relative to each other; a shape a frame does not pose holds its authored pose there.
    private static bool FramesAlike(CreationDocument document, int first, int second) {
        const float Tolerance = 1.0e-4f;

        foreach (var frame in (document.Frames ?? [])) {
            var (firstTurn, firstShift) = FrameMotion(document: document, frame: frame, index: first);
            var (secondTurn, secondShift) = FrameMotion(document: document, frame: frame, index: second);

            if ((MathF.Abs(x: Quaternion.Dot(quaternion1: firstTurn, quaternion2: secondTurn)) < (1f - Tolerance)) || (Vector3.Distance(value1: firstShift, value2: secondShift) > Tolerance)) {
                return false;
            }
        }

        return true;
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
    // Whether a timeline frame poses a shape away from its authored position or rotation.
    private static bool FramesMove(CreationDocument document, int index) {
        var shape = document.Shapes![index];

        foreach (var frame in (document.Frames ?? [])) {
            foreach (var transform in (frame.Transforms ?? [])) {
                if ((transform.Id == shape.Id) && (!Equals(objA: shape.Position, objB: transform.Position) || !Equals(objA: shape.Rotation, objB: transform.Rotation))) {
                    return true;
                }
            }
        }

        return false;
    }
}
