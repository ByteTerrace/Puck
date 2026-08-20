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
    /// <summary>The ring-element-only sentinel: reads a raw RGB triple packed into the record's own reserved words
    /// instead of indexing this slab (see <see cref="OverlayFrameBuilder.WriteRing(float, float, float, RgbaColor, float)"/>)
    /// — a marker's authored, possibly state-bound ring color. Illegitimate on every other element/panel kind.</summary>
    Custom = 255,
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
        /// <summary>The procedural icon/glyph stroke half-width, in glyph-local units (<see cref="OverlayThemeValues.IconSet.StrokeHalfWidth"/>).</summary>
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

    // A scrim's fill plus its own alpha, composed into one baked-alpha RgbaColor the way every other role already
    // bakes its alpha — the GPU-side role table carries no separate per-role alpha channel.
    private static RgbaColor Baked(OverlayThemeValues.Scrim scrim) => new(
        A: scrim.Alpha,
        B: scrim.Color.B,
        G: scrim.Color.G,
        R: scrim.Color.R
    );

    /// <summary>Serializes the token slab into the destination span (the storage buffer's front words) from a
    /// resolved theme.</summary>
    /// <param name="destination">The destination, at least <see cref="WordCount"/> words.</param>
    /// <param name="theme">The resolved theme to serialize.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is shorter than <see cref="WordCount"/>.</exception>
    public static void Write(Span<uint> destination, in OverlayThemeValues theme) {
        if (destination.Length < WordCount) {
            throw new ArgumentException(
                message: $"The token block needs {WordCount} words; got {destination.Length}.",
                paramName: nameof(destination)
            );
        }

        var color = theme.Color;

        WriteColor(
            color: color.TextPrimary,
            destination: destination,
            role: OverlayColorRole.TextPrimary
        );
        WriteColor(
            color: color.TextDim,
            destination: destination,
            role: OverlayColorRole.TextDim
        );
        WriteColor(
            color: color.TextMute,
            destination: destination,
            role: OverlayColorRole.TextMute
        );
        WriteColor(
            color: color.Accent,
            destination: destination,
            role: OverlayColorRole.Accent
        );
        WriteColor(
            color: color.Positive,
            destination: destination,
            role: OverlayColorRole.Positive
        );
        WriteColor(
            color: color.Warning,
            destination: destination,
            role: OverlayColorRole.Warning
        );
        WriteColor(
            color: color.Danger,
            destination: destination,
            role: OverlayColorRole.Danger
        );
        WriteColor(
            color: color.Phosphor,
            destination: destination,
            role: OverlayColorRole.Phosphor
        );
        WriteColor(
            color: color.AccentInk,
            destination: destination,
            role: OverlayColorRole.AccentInk
        );
        WriteColor(
            color: color.SurfaceRaised,
            destination: destination,
            role: OverlayColorRole.SurfaceRaised
        );
        WriteColor(
            color: color.SurfaceInset,
            destination: destination,
            role: OverlayColorRole.SurfaceInset
        );
        WriteColor(
            color: color.AccentQuiet,
            destination: destination,
            role: OverlayColorRole.AccentQuiet
        );
        WriteColor(
            color: color.PhosphorCyan,
            destination: destination,
            role: OverlayColorRole.PhosphorCyan
        );
        WriteColor(
            color: color.SurfaceBase,
            destination: destination,
            role: OverlayColorRole.SurfaceBase
        );
        WriteColor(
            color: color.BadgeDark,
            destination: destination,
            role: OverlayColorRole.BadgeDark
        );
        WriteColor(
            color: color.BadgeLight,
            destination: destination,
            role: OverlayColorRole.BadgeLight
        );
        WriteColor(
            color: color.LineHair,
            destination: destination,
            role: OverlayColorRole.LineHair
        );
        WriteColor(
            color: color.LineSoft,
            destination: destination,
            role: OverlayColorRole.LineSoft
        );
        WriteColor(
            color: Baked(scrim: color.ScrimPanel),
            destination: destination,
            role: OverlayColorRole.ScrimPanel
        );
        WriteColor(
            color: Baked(scrim: color.ScrimStrip),
            destination: destination,
            role: OverlayColorRole.ScrimStrip
        );
        WriteColor(
            color: Baked(scrim: color.ScrimChip),
            destination: destination,
            role: OverlayColorRole.ScrimChip
        );

        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius1,
            value: theme.Radius.Radius1
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius2,
            value: theme.Radius.Radius2
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.Radius3,
            value: theme.Radius.Radius3
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.EdgeHairlineWidth,
            value: theme.Elevation.EdgeHairlineWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.RingStatusWidth,
            value: theme.Elevation.RingStatusWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomHaloBlur,
            value: theme.Elevation.BloomHaloBlur
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomRingAlpha,
            value: theme.Elevation.BloomRingAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomHaloAlpha,
            value: theme.Elevation.BloomHaloAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomNeutralRingAlpha,
            value: theme.Elevation.BloomNeutralRingAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.BloomNeutralHaloAlpha,
            value: theme.Elevation.BloomNeutralHaloAlpha
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.EdgeAa,
            value: DesignTokens.Icon.EdgeAaRamp
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.ChipRestOpacity,
            value: theme.Elevation.ChipRestOpacity
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.GlyphStroke,
            value: theme.Icon.StrokeHalfWidth
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.GlyphAa,
            value: DesignTokens.Icon.AaRamp
        );
        WriteScalar(
            destination: destination,
            scalar: Scalar.ReferenceChipHalf,
            value: (theme.Space.HeightChip * 0.5f)
        );
    }
}
