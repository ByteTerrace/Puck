namespace Puck.Overlays;

/// <summary>
/// The color roles the packed overlay records index — resolved to actual RGBA values from the
/// <see cref="OverlayTokenBlock"/> storage slab inside the fragment shader, never from a hardcoded HLSL table. Each
/// value IS its color's index in the block (one <c>float4</c>-aligned <c>uint4</c> element per role). The core roles
/// come first; the component slots after them carry a component style's resolved color, which names a core role or
/// its own.
/// </summary>
public enum OverlayColorRole : uint {
    /// <summary>The primary text.</summary>
    TextPrimary = 0,
    /// <summary>The dimmed text.</summary>
    TextDim = 1,
    /// <summary>The muted text.</summary>
    TextMute = 2,
    /// <summary>The accent.</summary>
    Accent = 3,
    /// <summary>The positive status.</summary>
    Positive = 4,
    /// <summary>The warning status.</summary>
    Warning = 5,
    /// <summary>The danger status.</summary>
    Danger = 6,
    /// <summary>The ink drawn on the accent.</summary>
    AccentInk = 7,
    /// <summary>The quiet accent fill.</summary>
    AccentQuiet = 8,
    /// <summary>The deepest surface.</summary>
    SurfaceBase = 9,
    /// <summary>A raised surface.</summary>
    SurfaceRaised = 10,
    /// <summary>A recessed surface.</summary>
    SurfaceInset = 11,
    /// <summary>The hairline rule.</summary>
    LineHair = 12,
    /// <summary>The soft rule.</summary>
    LineSoft = 13,
    /// <summary>The panel scrim, its alpha baked in.</summary>
    ScrimPanel = 14,
    /// <summary>The strip scrim, its alpha baked in.</summary>
    ScrimStrip = 15,
    /// <summary>The chip scrim, its alpha baked in.</summary>
    ScrimChip = 16,
    /// <summary>The console style's echo color: an echoed input line and the prompt.</summary>
    ConsoleEcho = 17,
    /// <summary>The keycap style's badge backing.</summary>
    KeycapBadge = 18,
    /// <summary>The keycap style's badge ink.</summary>
    KeycapBadgeInk = 19,
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
/// enum order; role <c>r</c> occupies <c>uint4</c> element <c>r</c> exactly (scrims bake their alpha into
/// <c>.a</c>).</description></item>
/// <item><description>Words <c>[4×RoleCount, WordCount)</c> — the geometry scalars, indexed by
/// <see cref="Scalar"/>.</description></item>
/// </list>
/// KEEP IN SYNC with the HLSL accessors <c>OverlayTokenColor</c>/<c>OverlayTokenScalar</c> and the role and scalar
/// indices in <c>Assets/Shaders/overlay-common.hlsli</c> — this file and that one are the one layout contract.
/// </summary>
public static class OverlayTokenBlock {
    /// <summary>The geometry-scalar slots, indexed after the color table.</summary>
    public enum Scalar : int {
        /// <summary>The keycap corner radius, px.</summary>
        Radius1 = 0,
        /// <summary>The strip and chip corner radius, px.</summary>
        Radius2 = 1,
        /// <summary>The panel corner radius, px.</summary>
        Radius3 = 2,
        /// <summary>The hairline outline width, px.</summary>
        Hairline = 3,
        /// <summary>The glow halo's reach, px.</summary>
        GlowHaloBlur = 4,
        /// <summary>The glow ring's alpha.</summary>
        GlowRingAlpha = 5,
        /// <summary>The glow halo's alpha.</summary>
        GlowHaloAlpha = 6,
        /// <summary>The neutral glow ring's alpha.</summary>
        GlowNeutralRingAlpha = 7,
        /// <summary>The neutral glow halo's alpha.</summary>
        GlowNeutralHaloAlpha = 8,
        /// <summary>The anti-alias ramp width for hairline/rounded-rect edges, px (<see cref="DesignTokens.Icon.EdgeAaRamp"/>).</summary>
        EdgeAa = 9,
        /// <summary>A resting keycap plate's opacity.</summary>
        KeycapRestOpacity = 10,
        /// <summary>The procedural icon/glyph anti-alias ramp, in glyph-local units (<see cref="DesignTokens.Icon.AaRamp"/>).</summary>
        GlyphAa = 11,
        /// <summary>Half the keycap's reference height — the denominator that converts an absolute px token into a
        /// per-plate ratio (the keycap scales with each plate's own half-size).</summary>
        KeycapReferenceHalf = 12,
    }

    /// <summary>The number of color roles in the block.</summary>
    public const int RoleCount = 20;
    /// <summary>The number of geometry-scalar slots (the unused tail keeps the block <c>uint4</c>-aligned).</summary>
    public const int ScalarCount = 16;
    /// <summary>The slab's total size in 32-bit words (a multiple of 4 — the storage buffer is <c>uint4</c>-strided).</summary>
    public const int WordCount = ((RoleCount * 4) + ScalarCount);

    // A scrim's fill plus its own alpha, composed into one baked-alpha RgbaColor the way every other role already
    // bakes its alpha — the GPU-side role table carries no separate per-role alpha channel.
    private static RgbaColor Baked(OverlayThemeValues.Scrim scrim) => new(
        A: scrim.Alpha,
        B: scrim.Color.B,
        G: scrim.Color.G,
        R: scrim.Color.R
    );
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
        var styles = theme.Styles;

        foreach (var (role, value) in ((ReadOnlySpan<(OverlayColorRole, RgbaColor)>)[
            (OverlayColorRole.TextPrimary, color.TextPrimary),
            (OverlayColorRole.TextDim, color.TextDim),
            (OverlayColorRole.TextMute, color.TextMute),
            (OverlayColorRole.Accent, color.Accent),
            (OverlayColorRole.Positive, color.Positive),
            (OverlayColorRole.Warning, color.Warning),
            (OverlayColorRole.Danger, color.Danger),
            (OverlayColorRole.AccentInk, color.AccentInk),
            (OverlayColorRole.AccentQuiet, color.AccentQuiet),
            (OverlayColorRole.SurfaceBase, color.SurfaceBase),
            (OverlayColorRole.SurfaceRaised, color.SurfaceRaised),
            (OverlayColorRole.SurfaceInset, color.SurfaceInset),
            (OverlayColorRole.LineHair, color.LineHair),
            (OverlayColorRole.LineSoft, color.LineSoft),
            (OverlayColorRole.ScrimPanel, Baked(scrim: color.ScrimPanel)),
            (OverlayColorRole.ScrimStrip, Baked(scrim: color.ScrimStrip)),
            (OverlayColorRole.ScrimChip, Baked(scrim: color.ScrimChip)),
            (OverlayColorRole.ConsoleEcho, styles.Console.Echo),
            (OverlayColorRole.KeycapBadge, styles.Keycap.Badge),
            (OverlayColorRole.KeycapBadgeInk, styles.Keycap.BadgeInk),
        ])) {
            WriteColor(
                color: value,
                destination: destination,
                role: role
            );
        }

        foreach (var (scalar, value) in ((ReadOnlySpan<(Scalar, float)>)[
            (Scalar.Radius1, theme.Radius.Radius1),
            (Scalar.Radius2, theme.Radius.Radius2),
            (Scalar.Radius3, theme.Radius.Radius3),
            (Scalar.Hairline, theme.Stroke.Hairline),
            (Scalar.GlowHaloBlur, styles.Glow.HaloBlur),
            (Scalar.GlowRingAlpha, styles.Glow.RingAlpha),
            (Scalar.GlowHaloAlpha, styles.Glow.HaloAlpha),
            (Scalar.GlowNeutralRingAlpha, styles.Glow.NeutralRingAlpha),
            (Scalar.GlowNeutralHaloAlpha, styles.Glow.NeutralHaloAlpha),
            (Scalar.EdgeAa, DesignTokens.Icon.EdgeAaRamp),
            (Scalar.KeycapRestOpacity, styles.Keycap.RestOpacity),
            (Scalar.GlyphAa, DesignTokens.Icon.AaRamp),
            (Scalar.KeycapReferenceHalf, (styles.Keycap.ReferenceSize * 0.5f)),
        ])) {
            WriteScalar(
                destination: destination,
                scalar: scalar,
                value: value
            );
        }
    }
}
