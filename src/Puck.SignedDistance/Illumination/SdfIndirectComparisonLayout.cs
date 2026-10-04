namespace Puck.SignedDistance;

/// <summary>The finite work bounds of the two indirect comparison methods. The shader generator emits these values;
/// the alternatives share the residency cache fallback and the existing temporal history.</summary>
public static class SdfIndirectComparisonLayout {
    /// <summary>The equal-weight cosine samples a comparison pixel takes.</summary>
    public const int Rays = 4;
    /// <summary>The projection samples along one screen-space ray.</summary>
    public const int ScreenSteps = 12;
    /// <summary>The field evaluations available to one cone, including its hit gradient and sign witness.
    /// Directional-light shadow fallbacks have their own existing bounds; the view counts their queries and
    /// field steps in its reserved indirect row.</summary>
    public const int ConeSteps = 24;
    /// <summary>The existing temporal phases interleaving the four render-pixel parity classes.</summary>
    public const int Phases = 4;
    /// <summary>The local comparison interval, in world units. Its clear end continues with the cache rather than
    /// declaring an unproved exit to the sky.</summary>
    public const float Reach = 4f;
    /// <summary>The cone radius per unit of travelled distance.</summary>
    public const float ConeSlope = 0.25f;
}
