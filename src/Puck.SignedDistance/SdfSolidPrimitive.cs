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
}
