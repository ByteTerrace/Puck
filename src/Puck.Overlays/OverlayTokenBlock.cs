namespace Puck.Overlays;

/// <summary>
/// The semantic color roles the packed overlay records index — resolved to actual RGBA values from the
/// <see cref="OverlayTokenBlock"/> storage slab inside the fragment shader, never from a hardcoded HLSL table.
/// Each value IS its color's index in the block (one <c>float4</c>-aligned <c>uint4</c> element per role).
/// </summary>
public enum OverlayColorRole : uint {
    TextPrimary = 0,
    TextDim = 1,
    TextMute = 2,
    Accent = 3,
    Positive = 4,
    Warning = 5,
    Danger = 6,
    Phosphor = 7,
    AccentInk = 8,
    SurfaceRaised = 9,
    SurfaceInset = 10,
    AccentQuiet = 11,
    PhosphorCyan = 12,
    SurfaceBase = 13,
    BadgeDark = 14,
    BadgeLight = 15,
    LineHair = 16,
    LineSoft = 17,
    ScrimPanel = 18,
    ScrimStrip = 19,
    ScrimChip = 20,
}

/// <summary>
/// The design-token slab uploaded ONCE into the front of the unified overlay's storage buffer — the buffer-fed
/// successor of the demo era's four hand-synced HLSL literal tables. Layout (all words, block at buffer word 0):
/// <list type="bullet">
/// <item><description>Words <c>[0, 4×RoleCount)</c> — one RGBA <c>float4</c> per <see cref="OverlayColorRole"/>, in
/// enum order; role <c>r</c> occupies <c>uint4</c> element <c>r</c> exactly (scrims/quiet roles bake their token
/// alpha into <c>.a</c>).</description></item>
/// <item><description>Words <c>[4×RoleCount, WordCount)</c> — the geometry scalars, indexed by
/// <see cref="Scalar"/>.</description></item>
/// </list>
/// KEEP IN SYNC with the HLSL accessors <c>OverlayTokenColor</c>/<c>OverlayTokenScalar</c> in
/// <c>Assets/Shaders/overlay-common.hlsli</c> — this file and those two functions are the ONE layout contract.
/// </summary>
public static class OverlayTokenBlock {
    /// <summary>The geometry-scalar slots, indexed after the color table.</summary>
    public enum Scalar : int {
        Radius1 = 0,
        Radius2 = 1,
        Radius3 = 2,
        EdgeHairlineWidth = 3,
        RingStatusWidth = 4,
        BloomHaloBlur = 5,
        BloomRingAlpha = 6,
        BloomHaloAlpha = 7,
        BloomNeutralRingAlpha = 8,
        BloomNeutralHaloAlpha = 9,
        /// <summary>The anti-alias ramp width for hairline/rounded-rect edges, px (<see cref="DesignTokens.Icon.EdgeAaRamp"/>).</summary>
        EdgeAa = 10,
        ChipRestOpacity = 11,
        /// <summary>The procedural icon/glyph stroke half-width, in glyph-local units (<see cref="DesignTokens.Icon.StrokeHalfWidth"/>).</summary>
        GlyphStroke = 12,
        /// <summary>The procedural icon/glyph anti-alias ramp, in glyph-local units (<see cref="DesignTokens.Icon.AaRamp"/>).</summary>
        GlyphAa = 13,
        /// <summary>Half the reference chip height — the denominator that converts an absolute px token into a
        /// per-chip ratio (the chip recipes scale with each slot's own plate half-size).</summary>
        ReferenceChipHalf = 14,
    }

    /// <summary>The number of color roles in the block.</summary>
    public const int RoleCount = 21;
    /// <summary>The number of geometry-scalar slots (one padding slot keeps the block <c>uint4</c>-aligned).</summary>
    public const int ScalarCount = 16;
    /// <summary>The slab's total size in 32-bit words (a multiple of 4 — the storage buffer is <c>uint4</c>-strided).</summary>
    public const int WordCount = ((RoleCount * 4) + ScalarCount);

    /// <summary>Serializes the token slab into the destination span (the storage buffer's front words).</summary>
    /// <param name="destination">The destination, at least <see cref="WordCount"/> words.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="WordCount"/>.</exception>
    public static void Write(Span<uint> destination) {
        if (destination.Length < WordCount) {
            throw new ArgumentException(message: $"The token block needs {WordCount} words; got {destination.Length}.", paramName: nameof(destination));
        }

        WriteColor(destination: destination, role: OverlayColorRole.TextPrimary, color: DesignTokens.Color.TextPrimary);
        WriteColor(destination: destination, role: OverlayColorRole.TextDim, color: DesignTokens.Color.TextDim);
        WriteColor(destination: destination, role: OverlayColorRole.TextMute, color: DesignTokens.Color.TextMute);
        WriteColor(destination: destination, role: OverlayColorRole.Accent, color: DesignTokens.Color.Accent);
        WriteColor(destination: destination, role: OverlayColorRole.Positive, color: DesignTokens.Color.Positive);
        WriteColor(destination: destination, role: OverlayColorRole.Warning, color: DesignTokens.Color.Warning);
        WriteColor(destination: destination, role: OverlayColorRole.Danger, color: DesignTokens.Color.Danger);
        WriteColor(destination: destination, role: OverlayColorRole.Phosphor, color: DesignTokens.Color.Phosphor);
        WriteColor(destination: destination, role: OverlayColorRole.AccentInk, color: DesignTokens.Color.AccentInk);
        WriteColor(destination: destination, role: OverlayColorRole.SurfaceRaised, color: DesignTokens.Color.SurfaceRaised);
        WriteColor(destination: destination, role: OverlayColorRole.SurfaceInset, color: DesignTokens.Color.SurfaceInset);
        WriteColor(destination: destination, role: OverlayColorRole.AccentQuiet, color: DesignTokens.Color.AccentQuiet);
        WriteColor(destination: destination, role: OverlayColorRole.PhosphorCyan, color: DesignTokens.Color.PhosphorCyan);
        WriteColor(destination: destination, role: OverlayColorRole.SurfaceBase, color: DesignTokens.Color.SurfaceBase);
        WriteColor(destination: destination, role: OverlayColorRole.BadgeDark, color: DesignTokens.Color.BadgeDark);
        WriteColor(destination: destination, role: OverlayColorRole.BadgeLight, color: DesignTokens.Color.BadgeLight);
        WriteColor(destination: destination, role: OverlayColorRole.LineHair, color: DesignTokens.Color.LineHair);
        WriteColor(destination: destination, role: OverlayColorRole.LineSoft, color: DesignTokens.Color.LineSoft);
        WriteColor(destination: destination, role: OverlayColorRole.ScrimPanel, color: DesignTokens.Color.ScrimPanel);
        WriteColor(destination: destination, role: OverlayColorRole.ScrimStrip, color: DesignTokens.Color.ScrimStrip);
        WriteColor(destination: destination, role: OverlayColorRole.ScrimChip, color: DesignTokens.Color.ScrimChip);

        WriteScalar(destination: destination, scalar: Scalar.Radius1, value: DesignTokens.Radius.Radius1);
        WriteScalar(destination: destination, scalar: Scalar.Radius2, value: DesignTokens.Radius.Radius2);
        WriteScalar(destination: destination, scalar: Scalar.Radius3, value: DesignTokens.Radius.Radius3);
        WriteScalar(destination: destination, scalar: Scalar.EdgeHairlineWidth, value: DesignTokens.Elevation.EdgeHairlineWidth);
        WriteScalar(destination: destination, scalar: Scalar.RingStatusWidth, value: DesignTokens.Elevation.RingStatusWidth);
        WriteScalar(destination: destination, scalar: Scalar.BloomHaloBlur, value: DesignTokens.Elevation.BloomHaloBlur);
        WriteScalar(destination: destination, scalar: Scalar.BloomRingAlpha, value: DesignTokens.Elevation.BloomRingAlpha);
        WriteScalar(destination: destination, scalar: Scalar.BloomHaloAlpha, value: DesignTokens.Elevation.BloomHaloAlpha);
        WriteScalar(destination: destination, scalar: Scalar.BloomNeutralRingAlpha, value: DesignTokens.Elevation.BloomNeutralRingAlpha);
        WriteScalar(destination: destination, scalar: Scalar.BloomNeutralHaloAlpha, value: DesignTokens.Elevation.BloomNeutralHaloAlpha);
        WriteScalar(destination: destination, scalar: Scalar.EdgeAa, value: DesignTokens.Icon.EdgeAaRamp);
        WriteScalar(destination: destination, scalar: Scalar.ChipRestOpacity, value: DesignTokens.Elevation.ChipRestOpacity);
        WriteScalar(destination: destination, scalar: Scalar.GlyphStroke, value: DesignTokens.Icon.StrokeHalfWidth);
        WriteScalar(destination: destination, scalar: Scalar.GlyphAa, value: DesignTokens.Icon.AaRamp);
        WriteScalar(destination: destination, scalar: Scalar.ReferenceChipHalf, value: (DesignTokens.Space.HeightChip * 0.5f));
    }

    private static void WriteColor(Span<uint> destination, OverlayColorRole role, RgbaColor color) {
        var offset = ((int)role * 4);

        destination[offset] = BitConverter.SingleToUInt32Bits(value: color.R);
        destination[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: color.G);
        destination[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: color.B);
        destination[(offset + 3)] = BitConverter.SingleToUInt32Bits(value: color.A);
    }
    private static void WriteScalar(Span<uint> destination, Scalar scalar, float value) {
        destination[((RoleCount * 4) + (int)scalar)] = BitConverter.SingleToUInt32Bits(value: value);
    }
}
