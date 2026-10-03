namespace Puck.SdfVm;

/// <summary>The mutually exclusive secondary-shadow decisions, in work-detail row order.</summary>
public enum SdfShadowDecision {
    /// <summary>The jitter index selects this pixel's quarter-grid march.</summary>
    Interleaved,
    /// <summary>The slot has no reusable owner, changes owner or participates in a handoff.</summary>
    Ownership,
    /// <summary>The light turns outside its penumbra anchor.</summary>
    LightMotion,
    /// <summary>A gathered occluder's dynamic row changes.</summary>
    OccluderMotion,
    /// <summary>The preceding sample fails identity or five-percent depth validation.</summary>
    Receiver,
    /// <summary>The secondary visibility is reprojected.</summary>
    Reprojected,
}

/// <summary>The shadow pass's work-detail rows, one per <see cref="SdfShadowDecision"/> in its order.</summary>
public static class SdfShadowDecisions {
    /// <summary>Gets the row labels the shadow pass counts each secondary pixel's decision into.</summary>
    public static IReadOnlyList<string> Labels { get; } = ["interleaved", "ownership", "light-motion", "occluder-motion", "receiver", "reprojected"];
}