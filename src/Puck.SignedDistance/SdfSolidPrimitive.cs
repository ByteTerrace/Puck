namespace Puck.SignedDistance;

/// <summary>
/// The solid primitive vocabulary — the shapes carrying a unit-size law (<see cref="SdfSolidGeometry"/>), a finite
/// local bound, and one rigid-copy spelling every consumer reads the same way: the renderer emitting a program, the
/// contact field evaluating one, and an analytic collider compiler reading world-axis extents off it.
/// </summary>
/// <remarks>A narrower set than <see cref="SdfShapeType"/>, which enumerates every shape the ISA can evaluate (2D
/// lifts, glyphs, screen slabs, sampled regions). A member here is a closed solid a body can stand on and be pushed
/// out of; membership is the claim that the marched field and the analytic bound mean the same volume. Values are the
/// wire order an authoring document persists: append only, never reorder.</remarks>
public enum SdfSolidPrimitive {
    /// <summary>A sphere.</summary>
    Sphere,
    /// <summary>A box.</summary>
    Box,
    /// <summary>A torus.</summary>
    Torus,
    /// <summary>A cylinder.</summary>
    Cylinder,
    /// <summary>A capsule.</summary>
    Capsule,
    /// <summary>An ellipsoid.</summary>
    Ellipsoid,
    /// <summary>A tapered capsule — a fat base narrowing to a rounded tip along +Y (teeth, horns, spikes).</summary>
    RoundCone,
    /// <summary>An infinite plane bounding a solid half-space; local +Y is the outward normal.</summary>
    Plane,
    /// <summary>A sharp circular cone with a flat base, centered along local Y.</summary>
    Cone,
    /// <summary>A bounded profile in local XY extruded along Z. The default trapezoid has unit bottom
    /// half-width, half-height and depth, with an authored top-to-bottom width ratio (default one half).
    /// <see cref="SdfPrismProfile"/> selects rounded rectangles, polygons, ellipses, or a convex vertex list instead.</summary>
    Prism,
    /// <summary>A generalized ellipsoid: <c>q = pow(abs(p)/r, e); d = (pow(q.x+q.y+q.z, 1/e) - 1) * min(r)</c>, unit
    /// radii (1,1,1) and an authored exponent <c>e</c> in [<see cref="SdfProgramBuilder.MinSuperellipsoidExponent"/>,
    /// <see cref="SdfProgramBuilder.MaxSuperellipsoidExponent"/>] (the document's <c>exponent</c> field carries it;
    /// unauthored is the minimum, the ellipsoid limit — the two spellings agree bit-for-bit at that exponent). A
    /// squircle/rounded-cube family: the minimum exponent is a plain ellipsoid, larger values round toward a box.</summary>
    Superellipsoid,
    /// <summary>A quadratic Bezier curve swept with a tapering, optionally bulging radius, optionally as 1-4 helical
    /// strands — see <see cref="SdfShapeType.Sweep"/>. NOT a closed solid a body can stand on in the sense the rest
    /// of this enum's members are: its field is "exact enough", not exact, and carries no
    /// <see cref="SdfSolidGeometry.Reach(SdfSolidPrimitive, System.Numerics.Vector3, SdfLift)"/> unit-scale law —
    /// its control points and radii already carry creation-unit dimensions directly (see
    /// <see cref="SdfSolidGeometry.SweepReach"/>). The authoring layer admits it only with a curve facet naming its
    /// control points and radii.</summary>
    Sweep,
}
