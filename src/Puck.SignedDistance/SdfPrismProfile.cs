using System.Numerics;

namespace Puck.SignedDistance;

/// <summary>The cross-section of a prism, in its local XY plane.</summary>
public enum SdfPrismProfileKind {
    /// <summary>A trapezoid controlled by the shape's taper.</summary>
    Trapezoid,
    /// <summary>A rectangle with independently controlled corner rounding.</summary>
    RoundedRectangle,
    /// <summary>A regular polygon, stretched by the authored X/Y scale.</summary>
    Polygon,
    /// <summary>An exact elliptical profile, extruded with flat front and rear caps.</summary>
    Ellipse,
    /// <summary>A rectangle with independently controlled 45-degree corner chamfers.</summary>
    ChamferedRectangle,
    /// <summary>An arbitrary convex polygon authored by <see cref="SdfPrismProfile.Vertices"/>, with
    /// <see cref="SdfPrismProfile.CornerRadius"/> reused as a uniform corner-rounding radius. The exact
    /// convex-polygon SDF, not the study's smoothed half-plane intersection.</summary>
    Convex,
}

/// <summary>Authored profile controls for a solid prism. The profile is extruded along local Z.</summary>
/// <param name="Kind">The profile family.</param>
/// <param name="CornerRadius">A fraction of the profile's smaller half-extent, in [0, 1]: RoundedRectangle's corner
/// radius, ChamferedRectangle's chamfer radius, or <see cref="SdfPrismProfileKind.Convex"/>'s uniform corner
/// rounding.</param>
/// <param name="Sides">Polygon side count, from 3 through 32.</param>
/// <param name="Vertices"><see cref="SdfPrismProfileKind.Convex"/> only: 3 to <see cref="MaxConvexVertices"/> local
/// XY points, clockwise (X right, Y up — each turn's 2D cross product of consecutive edges strictly negative), no
/// coincident or collinear vertices, and convex. Ignored by every other kind.</param>
public sealed record SdfPrismProfile(SdfPrismProfileKind Kind, float CornerRadius = 0.15f, int Sides = 6, IReadOnlyList<Vector2>? Vertices = null) {
    /// <summary>The fewest vertices a <see cref="SdfPrismProfileKind.Convex"/> profile may carry.</summary>
    public const int MinConvexVertices = 3;
    /// <summary>The most vertices a <see cref="SdfPrismProfileKind.Convex"/> profile may carry — the side table's
    /// per-shape budget (<see cref="SdfProgramBuilder.MaxConvexPolygonWordsPerShape"/> covers it) and the packed
    /// instruction's 4-bit vertex-count lane.</summary>
    public const int MaxConvexVertices = 8;

    /// <summary>Whether all controls are finite and within the supported intervals.</summary>
    /// <returns>True for a supported profile with valid controls.</returns>
    public bool IsValid() =>
        Enum.IsDefined(Kind) &&
        float.IsFinite(CornerRadius) && CornerRadius >= 0f && CornerRadius <= 1f &&
        Sides >= 3 && Sides <= 32 &&
        (Kind != SdfPrismProfileKind.Convex || IsValidConvexHull(Vertices));

    /// <summary>Whether <paramref name="vertices"/> is a well-formed clockwise convex polygon: 3 to
    /// <see cref="MaxConvexVertices"/> finite points inside the unit square (each coordinate in [-1, 1] — the
    /// profile is scaled by the shape's own XY scale like every other Prism profile, and the Prism's cull reach
    /// assumes exactly that frame), no two coincident, no three collinear, and every consecutive edge pair turning
    /// the same way (a strictly negative 2D cross product at every vertex).</summary>
    public static bool IsValidConvexHull(IReadOnlyList<Vector2>? vertices) {
        if ((vertices is null) || (vertices.Count < MinConvexVertices) || (vertices.Count > MaxConvexVertices)) {
            return false;
        }

        var count = vertices.Count;

        for (var i = 0; (i < count); i++) {
            var vertex = vertices[i];

            if (!float.IsFinite(vertex.X) || !float.IsFinite(vertex.Y) || (MathF.Abs(vertex.X) > 1f) || (MathF.Abs(vertex.Y) > 1f)) {
                return false;
            }
        }

        for (var i = 0; (i < count); i++) {
            var previous = vertices[((i - 1) + count) % count];
            var current = vertices[i];
            var next = vertices[(i + 1) % count];
            var edgeIn = (current - previous);
            var edgeOut = (next - current);
            var cross = ((edgeIn.X * edgeOut.Y) - (edgeIn.Y * edgeOut.X));

            // Strictly negative, never <=0: a zero cross is either a coincident vertex (a zero-length edge) or three
            // collinear points, both degenerate for the exact convex-polygon field below (a zero-length edge divides
            // by dot(e, e) == 0 in sdfConvexPolygon2D/its fixed-point mirror).
            if (!(cross < 0f)) {
                return false;
            }
        }

        return true;
    }
}
