using System.Numerics;
using Puck.Maths;
using Puck.SignedDistance;

namespace Puck.World.Authoring;

/// <summary>The document-only bound for a seam's relative rigid motion.</summary>
/// <param name="Rotation">Maximum relative rotation, radians.</param>
/// <param name="Displacement">Maximum separation at the seam, in creation units.</param>
/// <param name="Smooth">The seam's authored smooth radius.</param>
/// <param name="Refusal">Why no finite representable skin is admitted, or null.</param>
public readonly record struct CreationBlendEnvelope(float Rotation, float Displacement, float Smooth, string? Refusal) {
    /// <summary>Gets whether the seam fits the rotation and displacement bounds.</summary>
    public bool Admitted => ((Refusal is null) && (Rotation <= (MathF.PI / 3f)) && (Displacement <= (Smooth / 2f)));
}
public static partial class CreationMotion {
    /// <summary>Bounds a moving blend from its rig's writers alone: each term in full about its own pivot, and the frames'
    /// poses. A writer with no bound (an effector's solve or aligned tip, a part follower, an unbounded term, a frame pose
    /// bound to state) or an unsupported seam admits no skin.</summary>
    /// <param name="rig">The creation's rig.</param>
    /// <param name="blend">One of the rig's moving blends (<see cref="MovingBlends"/>).</param>
    /// <returns>The envelope.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rig"/> is <see langword="null"/>.</exception>
    public static CreationBlendEnvelope Envelope(CreationRig rig, CreationMovingBlend blend) {
        ArgumentNullException.ThrowIfNull(argument: rig);

        var document = rig.Document;
        var shapes = document.Shapes!;
        var k = (shapes[blend.Shape].Smooth ?? 0f);

        CreationBlendEnvelope Refuse(string why) => new(Displacement: float.PositiveInfinity, Refusal: why, Rotation: float.PositiveInfinity, Smooth: k);

        if (blend.Blend is not (SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)) { return Refuse(why: "blend has no continuous share"); }
        if (!(k > 0f)) { return Refuse(why: "smooth radius is zero"); }
        if ((document.TextRuns ?? []).Any(predicate: static run => run.Engraves)) { return Refuse(why: "CPU field does not read glyphs; engraving follows rigid owner surfaces"); }
        if (shapes.Any(predicate: s => ((s.Group == blend.Group) && (s.Profile is { Kind: SdfPrismProfileKind.Polygon })))) { return Refuse(why: "CPU field does not read regular polygons"); }
        var (firstPath, secondPath) = Paths(parents: rig.Parents, first: blend.Shape, second: blend.Other);
        var chain = firstPath.Concat(second: secondPath).ToArray();

        // State-bound authored geometry has no document-only rest or seam box.
        foreach (var index in chain.Append(element: blend.Shape).Append(element: blend.Other)) {
            var shape = shapes[index];

            if ((shape.Position.Reference is not null) || (shape.Rotation.Reference is not null) || (shape.Domain is { Count: > 0 })) { return Refuse(why: "rest frame or domain is not bounded"); }
        }
        var identity = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null);
        var a = CreationStampEmitter.ShapeStampBox(document: document, shapeIndex: blend.Shape, transform: identity);
        var b = CreationStampEmitter.ShapeStampBox(document: document, shapeIndex: blend.Other, transform: identity);

        if ((a is null) || (b is null)) { return Refuse(why: "seam has no finite part box"); }
        var low = (Vector3.Max(value1: a.Value.Low, value2: b.Value.Low) - new Vector3(value: k));
        var high = (Vector3.Min(value1: a.Value.High, value2: b.Value.High) + new Vector3(value: k));
        // Disjoint inflated boxes have no seam to skin. Their shapes may share a group without joining here.
        if (Vector3.Min(value1: (high - low), value2: Vector3.Zero) != Vector3.Zero) { return new(Displacement: 0f, Refusal: null, Rotation: 0f, Smooth: k); }
        var theta = 0f;
        var displacement = 0f;

        foreach (var index in chain) {
            var shape = shapes[index];
            var writers = rig.Writers(shape: index);

            if ((writers & (CreationRigWriters.Solve | CreationRigWriters.Align)) != 0) { return Refuse(why: "effector motion is unbounded"); }
            if ((writers & CreationRigWriters.Follower) != 0) { return Refuse(why: "follower motion is unbounded"); }
            foreach (var term in rig.Terms(shape: index)) {
                if (!term.Bounded) { return Refuse(why: ((term.Kind == CreationRigTermKind.Turn) ? "swing is unbounded" : "slide is unbounded")); }
            }
            if ((document.Frames ?? []).Any(predicate: frame => (frame.Transforms ?? []).Any(predicate: t => ((t.Id == shape.Id) && ((t.Position.Reference is not null) || (t.Rotation.Reference is not null)))))) { return Refuse(why: "timeline pose is state bound"); }
        }
        // Frames: each frame carries the two sides by their chains' composed motions; the seam box's points separate by at
        // most the largest corner's separation, since the separation is affine in the point.
        foreach (var frame in (document.Frames ?? [])) {
            var (firstTurn, firstShift) = PathFrameMotion(document: document, frame: frame, path: firstPath);
            var (secondTurn, secondShift) = PathFrameMotion(document: document, frame: frame, path: secondPath);
            var relative = Quaternion.Normalize(value: (firstTurn * Quaternion.Conjugate(value: secondTurn)));
            var separation = 0f;

            for (var corner = 0; (corner < 8); corner++) {
                var p = new Vector3(x: (((corner & 1) == 0) ? low.X : high.X), y: (((corner & 2) == 0) ? low.Y : high.Y), z: (((corner & 4) == 0) ? low.Z : high.Z));

                separation = MathF.Max(x: separation, y: Vector3.Distance(value1: (Vector3.Transform(rotation: firstTurn, value: p) + firstShift), value2: (Vector3.Transform(rotation: secondTurn, value: p) + secondShift)));
            }
            theta = MathF.Max(x: theta, y: VectorFunctions.TurnAngle(turn: relative));
            displacement = MathF.Max(x: displacement, y: separation);
        }
        // Slides move the seam by their limits. Each turn turns the seam box, already moved by every motion counted
        // before it, about its own pivot, so its reach grows by that displacement: a sound bound for pivots that differ.
        foreach (var index in chain) {
            foreach (var term in rig.Terms(shape: index)) {
                if (term.Kind == CreationRigTermKind.Slide) { displacement += term.Limit; }
            }
        }
        foreach (var index in chain) {
            foreach (var term in rig.Terms(shape: index)) {
                if (term.Kind != CreationRigTermKind.Turn) { continue; }
                var angle = term.Limit;
                var pivot = term.Pivot!.Value;
                var reach = (Vector3.Max(value1: Vector3.Abs(value: (low - pivot)), value2: Vector3.Abs(value: (high - pivot))).Length() + displacement);

                theta += angle;
                displacement += ((2f * reach) * MathF.Sin(x: (angle / 2f)));
            }
        }

        return new(Displacement: displacement, Refusal: null, Rotation: theta, Smooth: k);
    }
}
