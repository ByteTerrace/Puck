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
    public bool Admitted => (Refusal is null) && (Rotation <= (MathF.PI / 3f)) && (Displacement <= (Smooth / 2f));
}

public static partial class CreationMotion {
    /// <summary>Bounds a moving blend from authored motion alone. Unbounded or unsupported inputs admit no skin.</summary>
    public static CreationBlendEnvelope Envelope(CreationDocument document, CreationMovingBlend blend) {
        var shapes = document.Shapes!;
        var k = (shapes[blend.Shape].Smooth ?? 0f);
        CreationBlendEnvelope Refuse(string why) => new(float.PositiveInfinity, float.PositiveInfinity, k, why);

        if (blend.Blend is not (SdfBlendOp.SmoothUnion or SdfBlendOp.SmoothIntersection or SdfBlendOp.SmoothSubtraction)) { return Refuse("blend has no continuous share"); }
        if (!(k > 0f)) { return Refuse("smooth radius is zero"); }
        if (shapes.Any(s => (s.Group == blend.Group) && (s.Profile is { Kind: SdfPrismProfileKind.Polygon }))) { return Refuse("CPU field does not read regular polygons"); }
        var parents = new int[shapes.Count];
        ResolveParents(shapes, parents);
        var first = new HashSet<int>();
        for (var at = blend.Shape; at >= 0; at = parents[at]) { first.Add(at); }
        var common = -1;
        for (var at = blend.Other; at >= 0; at = parents[at]) {
            if (first.Contains(at)) { common = at; break; }
        }
        var chain = new List<int>();
        for (var at = blend.Shape; at != common; at = parents[at]) { chain.Add(at); }
        for (var at = blend.Other; at != common; at = parents[at]) { chain.Add(at); }

        // State-bound authored geometry has no document-only rest or seam box.
        foreach (var index in chain.Append(blend.Shape).Append(blend.Other)) {
            var shape = shapes[index];
            if ((shape.Position.Reference is not null) || (shape.Rotation.Reference is not null) || (shape.Domain is { Count: > 0 })) { return Refuse("rest frame or domain is not bounded"); }
        }
        var identity = new CreationStampTransform(Vector3.Zero, Quaternion.Identity, 1f, null);
        var a = CreationStampEmitter.ShapeStampBox(document, blend.Shape, identity);
        var b = CreationStampEmitter.ShapeStampBox(document, blend.Other, identity);
        if ((a is null) || (b is null)) { return Refuse("seam has no finite part box"); }
        var low = Vector3.Max(a.Value.Low, b.Value.Low) - new Vector3(k);
        var high = Vector3.Min(a.Value.High, b.Value.High) + new Vector3(k);
        // Disjoint inflated boxes have no seam to skin. Their shapes may share a group without joining here.
        if (Vector3.Min(high - low, Vector3.Zero) != Vector3.Zero) { return new(0f, 0f, k, null); }
        var theta = 0f;
        var slides = 0f;
        var radius = 0f;
        var frameShift = 0f;
        foreach (var index in chain) {
            var shape = shapes[index];
            if ((document.Effectors ?? []).Any(e => (shape.Name is { } name) && e.Chain.Contains(name.Value))) { return Refuse("effector motion is unbounded"); }
            foreach (var swing in (shape.Swings ?? [])) {
                if ((swing.Amplitude.Reference is not null) || (swing.Pivot.Reference is not null) || !BoundedWave(swing.Wave)) { return Refuse("swing is unbounded"); }
                theta += MathF.Abs(swing.Amplitude.Value);
                var reach = Vector3.Max(Vector3.Abs(low - swing.Pivot.Value), Vector3.Abs(high - swing.Pivot.Value)).Length();
                radius = MathF.Max(radius, reach);
            }
            foreach (var slide in (shape.Slides ?? [])) {
                if ((slide.Amplitude.Reference is not null) || !BoundedWave(slide.Wave)) { return Refuse("slide is unbounded"); }
                slides += MathF.Abs(slide.Amplitude.Value);
            }
            var frameAngle = 0f;
            var shift = 0f;
            foreach (var frame in (document.Frames ?? [])) {
                if ((frame.Transforms ?? []).Any(t => (t.Id == shape.Id) && ((t.Position.Reference is not null) || (t.Rotation.Reference is not null)))) { return Refuse("timeline pose is state bound"); }
                var (turn, translation) = FrameMotion(document, frame, index);
                frameAngle = MathF.Max(frameAngle, 2f * MathF.Acos(Math.Clamp(MathF.Abs(turn.W), 0f, 1f)));
                shift = MathF.Max(shift, translation.Length());
                radius = MathF.Max(radius, Vector3.Max(Vector3.Abs(low), Vector3.Abs(high)).Length());
            }
            theta += frameAngle;
            frameShift += shift;
        }
        var displacement = slides + frameShift + (2f * radius * MathF.Sin(MathF.Min(theta, MathF.PI) / 2f));
        return new(theta, displacement, k, null);
    }

    // Curve waveforms require the containing world's curve, absent from a creation-only proof. Driver weights,
    // including weightBy, are clamped to [0,1] by WorldGaitDrivers before ComposeDelta consumes them.
    private static bool BoundedWave(string? wave) => (wave is null or CreationWave.Sine or CreationWave.HalfSine or CreationWave.Constant);
}
