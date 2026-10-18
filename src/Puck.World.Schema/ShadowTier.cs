using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The engine-wide shadow tier: whether the shadow cascades render, and at what resolution. Each step down
/// halves every cascade's texels along each side, a quarter of its memory and fill, so its shadows soften and lose
/// their finest contacts over the same reach.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShadowTier>))]
public enum ShadowTier {
    /// <summary>No shadows: no cascade renders or is held, and every light goes unshadowed.</summary>
    Off,

    /// <summary>Cascades at a quarter of the full resolution, 512 texels a side.</summary>
    Low,

    /// <summary>Cascades at half the full resolution, 1024 texels a side.</summary>
    Medium,

    /// <summary>Cascades at the full resolution, 2048 texels a side.</summary>
    High,
}
