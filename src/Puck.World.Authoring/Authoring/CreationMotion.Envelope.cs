using System.Numerics;
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
    /// <summary>Bounds a moving blend from authored motion alone. Unbounded or unsupported inputs admit no skin.</summary>
    public static CreationBlendEnvelope Envelope(CreationDocument document, CreationMovingBlend blend) {
        var shapes = document.Shapes!;
        var k = (shapes[blend.Shape].Smooth ?? 0f);

        CreationBlendEnvelope Refuse(string why) => new(Displacement: float.PositiveInfinity, Refusal: why, Rotation: float.PositiveInfinity, Smooth: k);

        if (blend.Blend is not (SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)) { return Refuse(why: "blend has no continuous share"); }
        if (!(k > 0f)) { return Refuse(why: "smooth radius is zero"); }
        if ((document.TextRuns ?? []).Any(predicate: run => (run.Mode == TextRunDocument.ModeEngrave))) { return Refuse(why: "CPU field does not read glyphs; engraving follows rigid owner surfaces"); }
        if (shapes.Any(predicate: s => ((s.Group == blend.Group) && (s.Profile is { Kind: SdfPrismProfileKind.Polygon })))) { return Refuse(why: "CPU field does not read regular polygons"); }
        var parents = new int[shapes.Count];

        ResolveParents(parents: parents, shapes: shapes);
        var first = new HashSet<int>();

        for (var at = blend.Shape; (at >= 0); at = parents[at]) { first.Add(item: at); }
        var common = -1;

        for (var at = blend.Other; (at >= 0); at = parents[at]) {
            if (first.Contains(item: at)) { common = at; break; }
        }
        var chain = new List<int>();

        for (var at = blend.Shape; (at != common); at = parents[at]) { chain.Add(item: at); }
        for (var at = blend.Other; (at != common); at = parents[at]) { chain.Add(item: at); }

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
        var slides = 0f;
        var radius = 0f;
        var frameShift = 0f;

        foreach (var index in chain) {
            var shape = shapes[index];

            if ((document.Effectors ?? []).Any(predicate: e => ((shape.Name is { } name) && e.Chain.Contains(value: name.Value)))) { return Refuse(why: "effector motion is unbounded"); }
            foreach (var swing in (shape.Swings ?? [])) {
                if ((swing.Amplitude.Reference is not null) || (swing.Pivot.Reference is not null) || (swing.Axis.Reference is not null) || !BoundedWave(wave: swing.Wave)) { return Refuse(why: "swing is unbounded"); }
                theta += MathF.Abs(x: swing.Amplitude.Value);
                var reach = Vector3.Max(value1: Vector3.Abs(value: (low - swing.Pivot.Value)), value2: Vector3.Abs(value: (high - swing.Pivot.Value))).Length();

                radius = MathF.Max(x: radius, y: reach);
            }
            foreach (var slide in (shape.Slides ?? [])) {
                if ((slide.Amplitude.Reference is not null) || (slide.Axis.Reference is not null) || !BoundedWave(wave: slide.Wave)) { return Refuse(why: "slide is unbounded"); }
                slides += MathF.Abs(x: slide.Amplitude.Value);
            }
            var frameAngle = 0f;
            var shift = 0f;

            foreach (var frame in (document.Frames ?? [])) {
                if ((frame.Transforms ?? []).Any(predicate: t => ((t.Id == shape.Id) && ((t.Position.Reference is not null) || (t.Rotation.Reference is not null))))) { return Refuse(why: "timeline pose is state bound"); }
                var (turn, translation) = FrameMotion(document: document, frame: frame, index: index);
                frameAngle = MathF.Max(x: frameAngle, y: (2f * MathF.Acos(x: Math.Clamp(MathF.Abs(x: turn.W), 0f, 1f))));
                shift = MathF.Max(x: shift, y: translation.Length());
                radius = MathF.Max(x: radius, y: Vector3.Max(value1: Vector3.Abs(value: low), value2: Vector3.Abs(value: high)).Length());
            }
            theta += frameAngle;
            frameShift += shift;
        }
        var displacement = ((slides + frameShift) + ((2f * radius) * MathF.Sin(x: (MathF.Min(x: theta, y: MathF.PI) / 2f))));

        return new(Displacement: displacement, Refusal: null, Rotation: theta, Smooth: k);
    }

    // Curve waveforms require the containing world's curve, absent from a creation-only proof. Driver weights,
    // including weightBy, are clamped to [0,1] by WorldGaitDrivers before ComposeDelta consumes them.
    private static bool BoundedWave(string? wave) => (wave is null or CreationWave.Sine or CreationWave.HalfSine or CreationWave.Constant);
}
