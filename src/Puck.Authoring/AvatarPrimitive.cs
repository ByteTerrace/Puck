namespace Puck.Authoring;

/// <summary>
/// The primitive kinds a player can place in a creation workbench, in wire order: each enum value IS its persisted
/// index — append only, never reorder.
/// </summary>
public enum AvatarPrimitive {
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
    /// <summary>A tapered capsule (a fat base narrowing to a rounded tip along +Y) — teeth, fangs, dorsal spikes,
    /// beaks, horns, a wizard hat: the "pointy" primitive the six round/boxy ones could never make. Already a
    /// builder shape (<see cref="Puck.SdfVm.SdfShapeType.RoundCone"/>); appended LAST so every avatar authored
    /// before it keeps its wire-value index.</summary>
    RoundCone,
}
