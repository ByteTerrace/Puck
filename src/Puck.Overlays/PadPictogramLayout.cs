namespace Puck.Overlays;

/// <summary>Sizing options for <see cref="PadPictogramLayout"/>.</summary>
/// <param name="ButtonSize">The button pitch — one compass step, in the caller's units.</param>
/// <param name="CenterGap">The extra half-gap between the two clusters, in the caller's units.</param>
/// <param name="GlyphOffsetRatio">The glyph-badge nudge as a fraction of <paramref name="ButtonSize"/>.</param>
public readonly record struct PadPictogramOptions(float ButtonSize, float CenterGap, float GlyphOffsetRatio);

/// <summary>One resolved pictogram slot: the button center and its glyph-badge offset, both relative to the
/// pictogram's midpoint, Y positive upward.</summary>
/// <param name="X">The button-center X.</param>
/// <param name="YUp">The button-center Y, positive up.</param>
/// <param name="GlyphX">The glyph-badge X offset from the button center.</param>
/// <param name="GlyphYUp">The glyph-badge Y offset from the button center, positive up.</param>
public readonly record struct PadPictogramSlot(float X, float YUp, float GlyphX, float GlyphYUp);

/// <summary>Lays out a two-cluster gamepad pictogram — the d-pad and face-button diamonds of one controller —
/// as pure geometry: each cluster is a compass diamond plus a diagonal and a center slot, the second cluster is
/// the arithmetic mirror of the first, and every button's glyph badge nudges along that button's own compass
/// vector so the badge direction and the button position share one source of truth.</summary>
/// <remarks>Slot compass is cluster-local and the mirror flips it on screen: the left cluster (indices 0..5)
/// renders its compass-west slot nearest the pictogram midpoint and its compass-east slot farthest left. A
/// consumer mapping physical controls to slots accounts for that flip (or feeds the left cluster pre-flipped
/// slot indices); the primitive stays pure.</remarks>
public static class PadPictogramLayout {
    /// <summary>The number of slots in one cluster.</summary>
    public const int SlotsPerCluster = 6;

    /// <summary>The total slot count across both clusters.</summary>
    public const int SlotCount = (SlotsPerCluster * 2);

    // The compass diamond's X midpoint — glyph badges nudge relative to it.
    private const int DiamondCenterX = 3;

    // The six cluster slots as geometry data, index-aligned across both spans:
    //   0 north (3, 1) · 1 west (2, 0) · 2 south (3, -1) · 3 east (4, 0) · 4 diagonal (4, 1) · 5 center (3, 0)
    private static ReadOnlySpan<sbyte> SlotCompassX => [3, 2, 3, 4, 4, 3];
    private static ReadOnlySpan<sbyte> SlotCompassY => [1, 0, -1, 0, 1, 0];

    /// <summary>Resolves one slot of the pictogram.</summary>
    /// <param name="index">The slot index: 0..5 is the left cluster (mirrored), 6..11 the right.</param>
    /// <param name="options">The pictogram sizing.</param>
    /// <returns>The resolved button center and glyph-badge offset.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0..11.</exception>
    public static PadPictogramSlot Resolve(int index, in PadPictogramOptions options) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: index, other: SlotCount);

        var (cluster, slot) = Math.DivRem(left: index, right: SlotsPerCluster);
        var sign = ((cluster << 1) - 1);
        var xMultiplier = SlotCompassX[index: slot];
        var yMultiplier = SlotCompassY[index: slot];
        var size = options.ButtonSize;
        var badge = (size * options.GlyphOffsetRatio);

        return new PadPictogramSlot(
            GlyphX: (sign * ((xMultiplier - DiamondCenterX) * badge)),
            GlyphYUp: (yMultiplier * badge),
            X: (sign * ((xMultiplier * size) + options.CenterGap)),
            YUp: (yMultiplier * size)
        );
    }
}
