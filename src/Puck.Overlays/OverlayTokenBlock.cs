namespace Puck.Overlays;

/// <summary>
/// The semantic color roles the packed overlay records index — resolved to actual RGBA values from the
/// <see cref="OverlayTokenBlock"/> storage slab inside the fragment shader, never from a hardcoded HLSL table.
/// Each value IS its color's index in the block (one <c>float4</c>-aligned <c>uint4</c> element per role).
/// </summary>
public enum OverlayColorRole : uint {
    TextPrimary = 0,
    TextDim = 1,
    /// <summary>The muted text tone — CPU-selected; no dedicated HLSL macro.</summary>
    TextMute = 2,
    Accent = 3,
    Positive = 4,
    Warning = 5,
    Danger = 6,
    /// <summary>The phosphor accent tone — CPU-selected; no dedicated HLSL macro.</summary>
    Phosphor = 7,
    AccentInk = 8,
    SurfaceRaised = 9,
    /// <summary>The inset-surface tone — CPU-selected; no dedicated HLSL macro.</summary>
    SurfaceInset = 10,
    AccentQuiet = 11,
    /// <summary>The cyan phosphor accent tone — CPU-selected; no dedicated HLSL macro.</summary>
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
/// The single GPU token slab, uploaded once into the front of the unified overlay's storage buffer. Layout (all
/// words, block at buffer word 0):
/// <list type="bullet">
/// <item><description>Words <c>[0, 4×RoleCount)</c> — one RGBA <c>float4</c> per <see cref="OverlayColorRole"/>, in
/// enum order; role <c>r</c> occupies <c>uint4</c> element <c>r</c> exactly (scrims/quiet roles bake their token
/// alpha into <c>.a</c>).</description></item>
/// <item><description>Words <c>[4×RoleCount, WordCount)</c> — the geometry scalars, indexed by
/// <see cref="Scalar"/>.</description></item>
/// </list>
/// KEEP IN SYNC with the HLSL accessors <c>OverlayTokenColor</c>/<c>OverlayTokenScalar</c> in
/// <c>Assets/Shaders/overlay-common.hlsli</c> — this file and those two functions are the one layout contract.
/// </summary>
public static class OverlayTokenBlock {
    /// <summary>The geometry-scalar slots, indexed after the color table.</summary>
    public enum Scalar : int {
        /// <summary>The panel corner radius, px.</summary>
        Radius1 = 0,
        /// <summary>The strip corner radius, px.</summary>
        Radius2 = 1,
        /// <summary>The chip corner radius, px.</summary>
        Radius3 = 2,
        /// <summary>The hairline outline width, px.</summary>
        EdgeHairlineWidth = 3,
        /// <summary>The status ring stroke width, px.</summary>
        RingStatusWidth = 4,
        /// <summary>The bloom halo's Gaussian blur radius, px.</summary>
        BloomHaloBlur = 5,
        /// <summary>The Tier-1 status ring's bloom alpha.</summary>
        BloomRingAlpha = 6,
        /// <summary>The Tier-1 status halo's bloom alpha.</summary>
        BloomHaloAlpha = 7,
        /// <summary>The neutral-tier status ring's bloom alpha.</summary>
        BloomNeutralRingAlpha = 8,
        /// <summary>The neutral-tier status halo's bloom alpha.</summary>
        BloomNeutralHaloAlpha = 9,
        /// <summary>The anti-alias ramp width for hairline/rounded-rect edges, px (<see cref="DesignTokens.Icon.EdgeAaRamp"/>).</summary>
        EdgeAa = 10,
        /// <summary>The REST-tier chip plate's translucency.</summary>
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

    private static void WriteColor(Span<uint> destination, OverlayColorRole role, RgbaColor color) {
        var offset = (((int)role) * 4);

        destination[offset] = BitConverter.SingleToUInt32Bits(value: color.R);
        destination[(offset + 1)] = BitConverter.SingleToUInt32Bits(value: color.G);
        destination[(offset + 2)] = BitConverter.SingleToUInt32Bits(value: color.B);
        destination[(offset + 3)] = BitConverter.SingleToUInt32Bits(value: color.A);
    }
    private static void WriteScalar(Span<uint> destination, Scalar scalar, float value) {
        destination[((RoleCount * 4) + ((int)scalar))] = BitConverter.SingleToUInt32Bits(value: value);
    }

    /// <summary>Serializes the token slab into the destination span (the storage buffer's front words).</summary>
    /// <param name="destination">The destination, at least <see cref="WordCount"/> words.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="WordCount"/>.</exception>
    public static void Write(Span<uint> destination) {
        if (destination.Length < WordCount) {
            throw new ArgumentException(
                message: $"The token block needs {WordCount} words; got {destination.Length}.",
                paramName: nameof(destination)
            );
        }

        WriteColor(
            color: DesignTokens.Color.TextPrimary,
            destination: destination,
            role: OverlayColorRole.TextPrimary
        );
        WriteColor(
            color: DesignTokens.Color.TextDim,
            destination: destination,
            role: OverlayColorRole.TextDim
        );
        WriteColor(
            color: DesignTokens.Color.TextMute,
            destination: destination,
            role: OverlayColorRole.TextMute
        );
        WriteColor(
            color: DesignTokens.Color.Accent,
            destination: destination,
            role: OverlayColorRole.Accent
        );
        WriteColor(
            color: DesignTokens.Color.Positive,
            destination: destination,
            role: OverlayColorRole.Positive
        );
        WriteColor(
            color: DesignTokens.Color.Warning,
            destination: destination,
            role: OverlayColorRole.Warning
        );
        WriteColor(
            color: DesignTokens.Color.Danger,
            destination: destination,
            role: OverlayColorRole.Danger
        );
        WriteColor(
            color: DesignTokens.Color.Phosphor,
            destination: destination,
            role: OverlayColorRole.Phosphor
        );
        WriteColor(
            color: DesignTokens.Color.AccentInk,
            destination: destination,
            role: OverlayColorRole.AccentInk
        );
        WriteColor(
            color: DesignTokens.Color.SurfaceRaised,
            destination: destination,
            role: OverlayColorRole.SurfaceRaised
        );
        WriteColor(
            color: DesignTokens.Color.SurfaceInset,
            destination: destination,
            role: OverlayColorRole.SurfaceInset
        );
        WriteColor(
            color: DesignTokens.Color.AccentQuiet,
            destination: destination,
            role: OverlayColorRole.AccentQuiet
        );
        WriteColor(
            color: DesignTokens.Color.PhosphorCyan,
            destination: destination,
            role: OverlayColorRole.PhosphorCyan
        );
        WriteColor(
            color: DesignTokens.Color.SurfaceBase,
            destination: destination,
            role: OverlayColorRole.SurfaceBase
        );
        WriteColor(
            color: DesignTokens.Color.BadgeDark,
            destination: destination,
            role: OverlayColorRole.BadgeDark
        );
        WriteColor(
            color: DesignTokens.Color.BadgeLight,
            destination: destination,
            role: OverlayColorRole.BadgeLight
        );
        WriteColor(
            color: DesignTokens.Color.LineHair,
            destination: destination,
            role: OverlayColorRole.LineHair
        );
        WriteColor(
            color: DesignTokens.Color.LineSoft,
            destination: destination,
            role: OverlayColorRole.LineSoft
        );
        WriteColor(
            color: DesignTokens.Color.ScrimPanel,
            destination: destination,
            role: OverlayColorRole.ScrimPanel
        );
        WriteColor(
            color: DesignTokens.Color.ScrimStrip,
            destination: destination,
            role: OverlayColorRole.ScrimStrip
        );
        WriteColor(
            color: DesignTokens.Color.ScrimChip,
            destination: destination,
            role: OverlayColorRole.ScrimChip
        );

        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius1,
            value: DesignTokens.Radius.Radius1
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius2,
            value: DesignTokens.Radius.Radius2
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius3,
            value: DesignTokens.Radius.Radius3
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.EdgeHairlineWidth,
            value: DesignTokens.Elevation.EdgeHairlineWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.RingStatusWidth,
            value: DesignTokens.Elevation.RingStatusWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomHaloBlur,
            value: DesignTokens.Elevation.BloomHaloBlur
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomRingAlpha,
            value: DesignTokens.Elevation.BloomRingAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomHaloAlpha,
            value: DesignTokens.Elevation.BloomHaloAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomNeutralRingAlpha,
            value: DesignTokens.Elevation.BloomNeutralRingAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomNeutralHaloAlpha,
            value: DesignTokens.Elevation.BloomNeutralHaloAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.EdgeAa,
            value: DesignTokens.Icon.EdgeAaRamp
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.ChipRestOpacity,
            value: DesignTokens.Elevation.ChipRestOpacity
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.GlyphStroke,
            value: DesignTokens.Icon.StrokeHalfWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.GlyphAa,
            value: DesignTokens.Icon.AaRamp
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.ReferenceChipHalf,
            value: (DesignTokens.Space.HeightChip * 0.5f)
        );
    }
}
