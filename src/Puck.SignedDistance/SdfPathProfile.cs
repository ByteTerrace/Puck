using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>A line, quadratic/cubic Bezier, or circular arc ending at End. An arc uses ArcCenter and
/// Clockwise; Beziers use Control and optionally Control2. Controls are in the profile's local XY frame.</summary>
public sealed record SdfPathSegment(Vector2 End, Vector2? Control = null, Vector2? Control2 = null,
    Vector2? ArcCenter = null, bool Clockwise = false);
/// <summary>A continuous contour. Filled contours close implicitly; stroked contours remain open unless Closed.</summary>
public sealed record SdfPathContour(Vector2 Start, IReadOnlyList<SdfPathSegment> Segments, bool Closed = false);
/// <summary>Round-ended stroke radii. Smooth interpolates with smoothstep over [From, To]; otherwise interpolation
/// is linear over that interval. Parameterization gives every authored segment an equal part of [0, 1].</summary>
public sealed record SdfPathStroke(float RadiusStart, float RadiusEnd, bool Smooth = false, float From = 0f, float To = 1f);
/// <summary>Boundary deformation: target += Offset + Linear*d + Quadratic*d² + Cubic*d³, with the other
/// coordinate clamped to [From, To] as d. Target is 0 (X) or 1 (Y). Filled paths only; included in the
/// subdivision error bound, so clamped polynomial bends can shape an outline without a runtime warp.</summary>
public sealed record SdfPathShear(float Linear, float Quadratic = 0f, float Cubic = 0f, float Offset = 0f,
    float From = -1f, float To = 1f, int Target = 0);
/// <summary>A bounded presentation profile. Filled contours use even/odd fill, including holes. Curves are flattened
/// once at composition, with a local-space geometric error at most Tolerance (apart from floating-point rounding).
/// Strokes approximate the union of disks along the curve, including their varying radius, to the same tolerance.
/// The emitted polygon/rounded-segment field is 1-Lipschitz; no margin moves its zero surface. At most 128 edges
/// are emitted; exceeding that budget refuses instead of silently relaxing the requested tolerance.</summary>
public sealed record SdfPathProfile(IReadOnlyList<SdfPathContour> Contours, float Tolerance = 0.001f,
    SdfPathStroke? Stroke = null, SdfPathShear? Shear = null) {
    /// <summary>The bounded edge-table and per-ray loop ceiling.</summary>
    public const int MaxEdges = 128;
    /// <summary>Smallest emitted edge length in normalized profile space; protects GPU projection denominators.</summary>
    public const float MinEdgeLength = 0.000001f;

    /// <summary>Compiles this profile or throws ArgumentException for invalid or over-budget input. All points,
    /// including stroke extents, must fit the unit square. Scaling a shape multiplies this error by at most
    /// max(scale.x, scale.y). Compiled filled boundaries may nest but may not touch or cross.</summary>
    public SdfPathEdge[] Compile() => SdfPathCompiler.Compile(path: this);
}
/// <summary>One compiled boundary edge or the convex hull of two endpoint disks. Radius values are zero for fill.</summary>
public readonly record struct SdfPathEdge(Vector2 A, Vector2 B, float RadiusA = 0f, float RadiusB = 0f);
/// <summary>A path table owned by one instruction. The program snapshots Edges before validation and packing.</summary>
public sealed record SdfCompiledPath(int InstructionIndex, IReadOnlyList<SdfPathEdge> Edges);
