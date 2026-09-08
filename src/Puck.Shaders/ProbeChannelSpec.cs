namespace Puck.Shaders;

/// <summary>One named channel of a <see cref="ProbeKindManifest"/> — the declared range and neutral value a
/// <c>ProbeReading</c> channel at this ordinal carries, in the kind's declaration order.</summary>
/// <param name="Name">The channel's name, unique within the kind.</param>
/// <param name="Min">The inclusive minimum.</param>
/// <param name="Max">The inclusive maximum.</param>
/// <param name="Neutral">The value an absent or expired reading resolves to; must lie in <c>[Min, Max]</c>.</param>
/// <param name="Description">The channel's description.</param>
public sealed record ProbeChannelSpec(
    string Name,
    double Min,
    double Max,
    double Neutral,
    string? Description = null
);
