using System.Numerics;

using Puck.Maths;

namespace Puck.SignedDistance;

/// <summary>The shape a paint covers: one primitive of the program's own shape vocabulary, packed exactly as
/// <see cref="SdfProgramBuilder"/> packs it for the field (<see cref="Data0"/>, <see cref="Data1"/> and any side-table
/// <see cref="Table"/>), with the residual per-axis <see cref="Scale"/> its emission rode as a scale transform. The
/// views pass evaluates it through the same shape dispatch the field interpreter calls (<c>evaluateShape</c>), never
/// through the field.</summary>
/// <param name="Type">The packed shape; <see cref="SdfPaint.Admits"/> lists the ones a paint carries.</param>
/// <param name="Data0">The shape's first data vector, as the builder packs it.</param>
/// <param name="Data1">The shape's second data vector, as the builder packs it.</param>
/// <param name="Table">The shape's side-table values (a convex polygon's vertices, a sweep's curve), or empty.</param>
/// <param name="Scale">The residual per-axis scale the shape is evaluated under: the point divides by it and the
/// distance multiplies by its smallest component, as the field's scale transform does. One for a shape whose
/// dimensions carry its size.</param>
/// <param name="Bound">The box the shape lies in, in its local frame and the paint frame's units; unbounded for a plane.
/// The views pass evaluates the shape only where the hit lies within the box widened by the stroke and the pixel.</param>
public sealed record SdfPaintShape(SdfShapeType Type, Vector4 Data0, Vector4 Data1, IReadOnlyList<float> Table, Vector3 Scale, SdfSolidBounds Bound) {
    /// <summary>Captures the shape <see cref="SdfSolidGeometry.AppendScaledPrimitive"/> emits for a document
    /// primitive: the one emission path a creation's shapes take, so a paint covers exactly the solid a shape with the
    /// same fields would.</summary>
    /// <param name="type">The primitive.</param>
    /// <param name="scale">The per-axis scale, in the paint frame's units.</param>
    /// <param name="taper">Prism top-to-bottom width ratio.</param>
    /// <param name="profile">Optional Prism cross-section; a path profile is refused.</param>
    /// <param name="lift">Prism only: extrude or revolve.</param>
    /// <param name="rounding">The edge-rounding radius.</param>
    /// <param name="chamfer">The cap chamfer radius.</param>
    /// <param name="exponent">The superellipsoid exponent.</param>
    /// <param name="curve">A sweep's curve, already in the paint frame's units.</param>
    /// <returns>The captured shape.</returns>
    /// <exception cref="ArgumentException">The primitive emits something a paint cannot carry.</exception>
    public static SdfPaintShape Of(SdfSolidPrimitive type, Vector3 scale, float taper = 0.5f, SdfPrismProfile? profile = null, SdfLift lift = SdfLift.Extrude, float rounding = 0f, float chamfer = 0f, float exponent = SdfProgramBuilder.MinSuperellipsoidExponent, SdfSweepParameters? curve = null) {
        if (profile is { Kind: SdfPrismProfileKind.Path }) {
            throw new ArgumentException(message: "A paint cannot cover a Path profile.", paramName: nameof(profile));
        }

        var builder = new SdfProgramBuilder();

        _ = SdfSolidGeometry.AppendScaledPrimitive(
            chain: builder,
            chamfer: chamfer,
            curve: curve,
            exponent: exponent,
            lift: lift,
            material: 0,
            profile: profile,
            rounding: rounding,
            scale: scale,
            taper: taper,
            type: type
        );

        return builder.CapturePaintShape(bound: BoundOf(curve: curve, lift: lift, scale: scale, type: type));
    }
    // The primitive's local box: a sweep's control points widened by its thickest reach, a revolved prism's reach on
    // every axis, and otherwise the canonical bounds at the scale.
    private static SdfSolidBounds BoundOf(SdfSolidPrimitive type, Vector3 scale, SdfLift lift, SdfSweepParameters? curve) {
        var magnitude = Vector3.Max(value1: Vector3.Abs(value: scale), value2: new Vector3(value: SdfSolidGeometry.MinimumScale));

        if ((type == SdfSolidPrimitive.Sweep) && (curve is { } sweep)) {
            var low = Vector3.Min(value1: sweep.A, value2: Vector3.Min(value1: sweep.B, value2: sweep.C));
            var high = Vector3.Max(value1: sweep.A, value2: Vector3.Max(value1: sweep.B, value2: sweep.C));
            var thickness = new Vector3(value: (MathF.Max(x: sweep.RadiusStart, y: sweep.RadiusEnd) + MathF.Abs(x: sweep.Bulge) + sweep.StrandOffset));

            return new SdfSolidBounds(Center: (((low + high) * 0.5f) * magnitude.X), HalfExtents: ((((high - low) * 0.5f) + thickness) * magnitude.X));
        }
        if ((type == SdfSolidPrimitive.Prism) && (lift == SdfLift.Revolve)) {
            return new SdfSolidBounds(Center: Vector3.Zero, HalfExtents: new Vector3(value: SdfSolidGeometry.Reach(lift: lift, scale: scale, type: type)));
        }

        var canonical = SdfSolidGeometry.GetLocalBounds(type: type);

        return (canonical.IsUnbounded
            ? canonical
            : new SdfSolidBounds(Center: (canonical.Center * magnitude), HalfExtents: (canonical.HalfExtents * magnitude)));
    }
}

/// <summary>A paint: a <see cref="SdfPaintShape"/> placed in a frame, whose inside gives the surface of its material
/// <see cref="Color"/>, with an edge antialiased over the pixel's footprint. The views pass evaluates it at the hit
/// alone, so it never changes the field or the bake and its edge is as sharp as the pixel. With
/// <see cref="Stroke"/> zero the paint fills the shape; with a positive stroke it paints the band within that distance
/// of the shape's surface (a plane stroked at a half-width is a stripe, an ellipse prism stroked is a ring).</summary>
/// <param name="Shape">The shape the paint covers, in its own frame.</param>
/// <param name="Origin">The shape frame's origin in the paint's frame.</param>
/// <param name="Rotation">The shape frame's rotation in the paint's frame; any nonzero length.</param>
/// <param name="Stroke">Zero to fill the shape; otherwise the half-width of the band painted about its surface.</param>
/// <param name="Color">The paint's linear-RGB albedo.</param>
/// <param name="Slot">The dynamic transform slot whose frame the paint rides, or <see cref="WinningFrame"/> for the
/// frame of the slot that owns the hit (world space for static geometry), as an inset's.</param>
public sealed record SdfPaint(SdfPaintShape Shape, Vector3 Origin, Quaternion Rotation, float Stroke, Vector3 Color, int Slot = SdfPaint.WinningFrame) {
    /// <summary>The <see cref="Slot"/> of a paint in the frame of the slot that owns the hit.</summary>
    public const int WinningFrame = -1;

    /// <summary>Returns whether a paint can cover a packed shape: every primitive the shape dispatch evaluates from its
    /// data and a side table of at most <see cref="SdfProgram.PaintTableVectors"/> vectors. A path, a glyph, a sampled
    /// region and a screen slab are refused.</summary>
    /// <param name="type">The packed shape.</param>
    /// <returns><see langword="true"/> when a paint can carry it.</returns>
    public static bool Admits(SdfShapeType type) => type is not (SdfShapeType.Path or SdfShapeType.Glyph or SdfShapeType.SampledRegion or SdfShapeType.ScreenSlab);

    /// <summary>Returns whether a paint's values are admissible.</summary>
    /// <param name="paint">The paint.</param>
    /// <returns><see langword="true"/> when every value is finite and in range.</returns>
    public static bool IsValid(SdfPaint? paint) => (
        (paint is { Shape: { } shape }) &&
        Admits(type: shape.Type) &&
        Enum.IsDefined(value: shape.Type) &&
        (shape.Table is { Count: <= (SdfProgram.PaintTableVectors * 4) }) &&
        VectorFunctions.IsFinite(vector: shape.Scale) &&
        (shape.Scale.X > 0f) && (shape.Scale.Y > 0f) && (shape.Scale.Z > 0f) &&
        (shape.Bound.IsUnbounded || (VectorFunctions.IsFinite(vector: shape.Bound.Center) && VectorFunctions.IsFinite(vector: shape.Bound.HalfExtents) && (shape.Bound.HalfExtents.X >= 0f) && (shape.Bound.HalfExtents.Y >= 0f) && (shape.Bound.HalfExtents.Z >= 0f))) &&
        VectorFunctions.IsFinite(vector: paint.Origin) &&
        float.IsFinite(f: paint.Rotation.LengthSquared()) &&
        (paint.Rotation.LengthSquared() >= 1e-12f) &&
        float.IsFinite(f: paint.Stroke) && (paint.Stroke >= 0f) &&
        VectorFunctions.IsFinite(vector: paint.Color) &&
        (paint.Color.X >= 0f) && (paint.Color.Y >= 0f) && (paint.Color.Z >= 0f) &&
        (paint.Slot >= WinningFrame)
    );
}
