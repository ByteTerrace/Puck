namespace Puck.SignedDistance;

/// <summary>The per-view indirect-light method used by the counted comparison. The ordinary path uses the
/// residency cache; the alternatives share its fallback and allocate no separate lighting history.</summary>
public enum SdfIndirectMethod : uint {
    /// <summary>The residency's certified irradiance cache.</summary>
    Cache = 0,
    /// <summary>Current visibility samples with explicit diffuse shading and the cache for unavailable samples.</summary>
    Screen = 1,
    /// <summary>Bounded field cones with one explicit diffuse bounce and the cache for unresolved samples.</summary>
    Cone = 2,
}
