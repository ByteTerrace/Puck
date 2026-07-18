namespace Puck.Authoring;

/// <summary>
/// The primitives a player can place in a creation workbench — the SAME set a creator's ghost cycles through, in the
/// same order (the wire value IS the cycle index, so an avatar authored in a creator round-trips through a forge
/// without a mapping table). KEEP the order in lockstep with the creator's own primitive cycle.
/// </summary>
public enum AvatarPrimitive {
    Sphere,
    Box,
    Torus,
    Cylinder,
    Capsule,
    Ellipsoid,
    // A tapered capsule (a fat base narrowing to a rounded tip along +Y) — teeth, fangs, dorsal spikes, beaks, horns,
    // a wizard hat: the "pointy" primitive the six round/boxy ones could never make. Already a builder shape
    // (SdfShapeType.RoundCone); appended LAST so every avatar authored before it keeps its wire-value cycle index.
    RoundCone,
}
