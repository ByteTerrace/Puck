namespace Puck.SignedDistance;

/// <summary>The bounded High-tier near-field replacement of one incoming diffuse sample.</summary>
public static class SdfIndirectNearLayout {
    /// <summary>The shared field-query allowance for transport, gradients, launch and connectivity.</summary>
    public const int Steps = 12;
    /// <summary>The render-pixel parity phases; each frame admits one pixel in each two-by-two block.</summary>
    public const int Phases = 4;
    /// <summary>The finest-level interval in world units. Its clear endpoint continues the original cached direction.</summary>
    public const float Reach = 0.5f;
}

/// <summary>The actual selected receiver's near-field outcome, retained with its ordinary indirect pick.</summary>
public enum SdfIndirectNearOutcome {
    /// <summary>The tier, method, source guard or interleaving phase admitted no near sample.</summary>
    NotAttempted,
    /// <summary>The bounded attempt could not answer and retained the whole cache fallback.</summary>
    Unresolved,
    /// <summary>A diffuse surface hit replaced the incoming sample.</summary>
    Hit,
    /// <summary>A certified local clear interval continued the original direction in the completed finest bank.</summary>
    Continuation,
}