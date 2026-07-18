using System.Numerics;

namespace Puck.Overlays;

/// <summary>
/// A single flat color in the design-token system: components in <c>[0, 1]</c> so a value plugs straight into a
/// push-constant or token-block float slot, or a <see cref="Vector3"/>/<see cref="Vector4"/> accessor for CPU-side math.
/// </summary>
/// <param name="R">The red channel, 0-1.</param>
/// <param name="G">The green channel, 0-1.</param>
/// <param name="B">The blue channel, 0-1.</param>
/// <param name="A">The alpha channel, 0-1.</param>
public readonly record struct RgbaColor(float R, float G, float B, float A) {
    /// <summary>Gets the color as an opaque <see cref="Vector3"/> (<see cref="A"/> dropped).</summary>
    public Vector3 Rgb => new(x: R, y: G, z: B);

    /// <summary>Gets the color as a <see cref="Vector4"/>.</summary>
    public Vector4 Rgba => new(x: R, y: G, z: B, w: A);

    /// <summary>Builds an opaque color from a 24-bit hex literal, spec notation <c>#RRGGBB</c> (e.g. <c>0x0E1013</c>).</summary>
    /// <param name="hexRgb">The 24-bit <c>0xRRGGBB</c> value.</param>
    /// <param name="alpha">The alpha channel, 0-1 (default fully opaque).</param>
    /// <returns>The color.</returns>
    public static RgbaColor FromHex(uint hexRgb, float alpha = 1f) => new(
        A: alpha,
        B: (((hexRgb >> 0) & 0xFFu) / 255f),
        G: (((hexRgb >> 8) & 0xFFu) / 255f),
        R: (((hexRgb >> 16) & 0xFFu) / 255f)
    );

    /// <summary>Builds a color from 0-255 channel bytes and an explicit alpha, spec notation <c>rgba(r,g,b,a)</c>.</summary>
    /// <param name="r">The red channel, 0-255.</param>
    /// <param name="g">The green channel, 0-255.</param>
    /// <param name="b">The blue channel, 0-255.</param>
    /// <param name="alpha">The alpha channel, 0-1.</param>
    /// <returns>The color.</returns>
    public static RgbaColor FromRgba(byte r, byte g, byte b, float alpha) => new(
        A: alpha,
        B: (b / 255f),
        G: (g / 255f),
        R: (r / 255f)
    );
}

/// <summary>
/// One bloom hue's ring + halo pair (the <c>bloom.*</c> tier-1 lit-state recipe; see
/// <see cref="DesignTokens.Elevation"/>). Composite per the spec: <c>bloom(hue) = 0 0 0 1px ring, 0 0 18px -3px halo</c>.
/// </summary>
/// <param name="Ring">The 1px lit ring color (<c>bloom.ring.alpha</c> baked into its own alpha, except neutral).</param>
/// <param name="Halo">The outer distance-falloff halo color (<c>bloom.halo.alpha</c> baked into its own alpha, except neutral).</param>
public readonly record struct BloomHue(RgbaColor Ring, RgbaColor Halo);

/// <summary>A CSS-style cubic-bezier easing curve's four control-point components.</summary>
/// <param name="X1">The first control point's x.</param>
/// <param name="Y1">The first control point's y.</param>
/// <param name="X2">The second control point's x.</param>
/// <param name="Y2">The second control point's y.</param>
public readonly record struct CubicBezier(float X1, float Y1, float X2, float Y2);

/// <summary>
/// The canonical Puck UI design tokens — docs/ui-design-tokens.md, "Instrument + grafts", FINAL — transcribed to C#
/// constants. This is the single source every 2D overlay surface reads instead of hand-picked literals, so the whole
/// UI shares one 4px grid, one radius/type scale, and one semantic palette. Lifted verbatim from the demo's
/// <c>Puck.Demo.Ui.DesignTokens</c> into the library home (the demo keeps its copy until it dies — the retirement
/// doctrine); unlike the demo era, the palette reaches the shaders through the <see cref="OverlayTokenBlock"/> storage
/// slab, never hand-mirrored HLSL literal tables. Every nested class mirrors one numbered section of the spec 1:1 —
/// read the spec for full rationale and the rendered reference capture.
/// </summary>
public static class DesignTokens {
    /// <summary>Section 1 — the strict 4px spacing grid and the grid-locked component heights (all multiples of 2).</summary>
    public static class Space {
        public const float HeightBadge = 24f;
        public const float HeightBindBar = 64f;
        public const float HeightChip = 40f;
        public const float HeightConsoleHead = 38f;
        public const float HeightModeRow = 30f;
        public const float HeightPromptRow = 34f;
        public const float HeightTrackerBar = 52f;
        public const float HeightTrackerCell = 26f;
        public const float Space0 = 0f;
        public const float Space1 = 4f;
        public const float Space2 = 8f;
        public const float Space3 = 12f;
        public const float Space4 = 16f;
        public const float Space5 = 20f;
        public const float Space6 = 24f;
        public const float Space8 = 32f;
    }

    /// <summary>
    /// Section 2 — the 3-step radius scale. Diegetic hardware (bezel/plate 5px, screen 2px) is a rendered world
    /// object and is EXEMPT from this scale.
    /// </summary>
    public static class Radius {
        public const float Radius1 = 3f;
        public const float Radius2 = 6f;
        public const float Radius3 = 9f;
    }

    /// <summary>
    /// Section 3 — the 5-step type scale. MSDF FLOOR RULE (Cabinet graft): primary chip labels are 12px; 11px is
    /// the absolute minimum anywhere (eyebrows/legends/badge glyphs/micro readouts). NOTHING renders at 10px or
    /// below — below 11px, MSDF glyph coverage degrades over the moving world. Weights are 400/500/600 only.
    /// </summary>
    public static class Type {
        /// <summary>The absolute floor: nothing in the UI renders at or below this size.</summary>
        public const float TypeAbsoluteFloorSize = 11f;
        public const float TypeBodyLine = 18f;
        public const float TypeBodySize = 13f;
        public const int TypeBodyWeight = 400;
        public const float TypeTitleLine = 18f;
        public const float TypeTitleSize = 16f;
        public const float TypeTitleTracking = 0.01f;
        public const int TypeTitleWeight = 600;

        /// <summary>Chip-label line-height equals <see cref="Space.HeightBadge"/> so the label baseline locks to the badge center.</summary>
        public const float TypeLabelSize = 12f;
        public const float TypeLabelLine = 24f;
        public const float TypeLabelTracking = 0.01f;
        public const int TypeLabelWeight = 500;

        /// <summary>Eyebrows/legends/tracker labels; UPPERCASE transform.</summary>
        public const float TypeMicroSize = 11f;
        public const float TypeMicroLine = 13f;
        public const float TypeMicroTracking = 0.08f;
        public const int TypeMicroWeight = 500;
        public const float TypeMonoLine = 18f;
        public const float TypeMonoSize = 12f;
        public const float TypeMonoTracking = 0.04f;
        public const int TypeMonoWeight = 400;

        /// <summary>The tracker position readout's mono size variant.</summary>
        public const float TypeMonoReadoutSize = 15f;
        /// <summary>The badge glyph's mono size variant.</summary>
        public const float TypeMonoBadgeSize = 11f;
    }

    /// <summary>
    /// Section 4 — the semantic color roles: near-neutral graphite surfaces, hairline outlines, and ONE electric
    /// accent used sparingly and semantically. Dark theme only; the lit world is the theme.
    /// </summary>
    public static class Color {
        // Surfaces (graphite, slightly cool-neutral).
        public static readonly RgbaColor SurfaceBase = RgbaColor.FromHex(hexRgb: 0x0E1013);
        public static readonly RgbaColor SurfacePanel = RgbaColor.FromHex(hexRgb: 0x15181C);
        public static readonly RgbaColor SurfaceRaised = RgbaColor.FromHex(hexRgb: 0x1D2126);
        public static readonly RgbaColor SurfaceInset = RgbaColor.FromHex(hexRgb: 0x0B0D0F);

        // Scrims — the opacity floats paint over the lit world. ScrimMinAlpha is the section-7 contract floor:
        // below 0.84 under text, the guaranteed-AA contrast story (over both a dark corner AND a lit CRT) breaks.
        public static readonly RgbaColor ScrimPanel = RgbaColor.FromRgba(r: 18, g: 21, b: 25, alpha: 0.90f);
        public static readonly RgbaColor ScrimStrip = RgbaColor.FromRgba(r: 18, g: 21, b: 25, alpha: 0.86f);
        public static readonly RgbaColor ScrimChip = RgbaColor.FromRgba(r: 23, g: 27, b: 31, alpha: 0.94f);

        public const float ScrimMinAlpha = 0.84f;

        // Outlines — hairline-first edge language (the primary edge language of the whole system).
        public static readonly RgbaColor LineHair = RgbaColor.FromRgba(r: 255, g: 255, b: 255, alpha: 0.09f);
        public static readonly RgbaColor LineSoft = RgbaColor.FromRgba(r: 255, g: 255, b: 255, alpha: 0.06f);
        public static readonly RgbaColor LineStrong = RgbaColor.FromRgba(r: 255, g: 255, b: 255, alpha: 0.16f);
        public static readonly RgbaColor LineInset = RgbaColor.FromRgba(r: 0, g: 0, b: 0, alpha: 0.55f);

        // Text.
        public static readonly RgbaColor TextPrimary = RgbaColor.FromHex(hexRgb: 0xEDEFF2);
        public static readonly RgbaColor TextDim = RgbaColor.FromHex(hexRgb: 0x9BA3AB);
        public static readonly RgbaColor TextMute = RgbaColor.FromHex(hexRgb: 0x5C646C);

        // Accent — ONE signal (electric amber-orange). Budget: one primary control per surface; never a border
        // run, never a background field.
        public static readonly RgbaColor Accent = RgbaColor.FromHex(hexRgb: 0xFF6A2B);
        public static readonly RgbaColor AccentQuiet = RgbaColor.FromRgba(r: 255, g: 106, b: 43, alpha: 0.14f);
        public static readonly RgbaColor AccentLine = RgbaColor.FromRgba(r: 255, g: 106, b: 43, alpha: 0.45f);
        public static readonly RgbaColor AccentInk = RgbaColor.FromHex(hexRgb: 0x160A04);

        // State semantics.
        public static readonly RgbaColor Positive = RgbaColor.FromHex(hexRgb: 0x5BC98C);
        public static readonly RgbaColor Warning = RgbaColor.FromHex(hexRgb: 0xE8B341);
        public static readonly RgbaColor Danger = RgbaColor.FromHex(hexRgb: 0xF2565B);

        // Phosphor — a MATERIAL, not chrome (diegetic + console echo only). It never paints a UI border, fill,
        // label, or accent: only (a) behind glass in the diegetic material, (b) the console's ECHOED INPUT LINE,
        // (c) the console status dot.
        public static readonly RgbaColor Phosphor = RgbaColor.FromHex(hexRgb: 0x5CFAA0);
        public static readonly RgbaColor PhosphorDim = RgbaColor.FromRgba(r: 92, g: 250, b: 160, alpha: 0.42f);
        public static readonly RgbaColor PhosphorCyan = RgbaColor.FromHex(hexRgb: 0x5EEBE0);

        // Badge ink pair — the gamepad-glyph badge's dark backing disc and light glyph (the binding-bar graft's two
        // non-role chip colors, promoted to named tokens so the icon element kind reads them from the token block).
        public static readonly RgbaColor BadgeDark = new(R: 0.05f, G: 0.05f, B: 0.07f, A: 1f);
        public static readonly RgbaColor BadgeLight = new(R: 0.96f, G: 0.96f, B: 0.98f, A: 1f);
    }

    /// <summary>
    /// Section 5 — the two-tier elevation rule. Tier 0 (RESTING) is a flat fill + hairline, no glow, ever. Tier 1
    /// (LIT: active / held / selected / transient) is an SDF distance-falloff bloom in the element's OWN semantic
    /// hue. An element is Tier 1 exactly while it is the context-primary action, physically held, the current
    /// selection, or a transient echo (toast) — there is no third tier.
    /// </summary>
    public static class Elevation {
        // Bloom geometry (one geometry, hue varies): an outer distance-falloff halo plus a 1px lit ring.
        public const float BloomHaloBlur = 18f;
        public const float BloomHaloAlpha = 0.42f;
        public const float BloomHaloSpread = -3f;
        public const float BloomRingAlpha = 0.55f;
        public const float BloomRingWidth = 1f;

        // Neutral (held chips with no semantic hue) blooms quieter BY DESIGN: pressed metal, not a signal.
        public const float BloomNeutralRingAlpha = 0.30f;
        public const float BloomNeutralHaloAlpha = 0.22f;

        // bloom.held.inset: the held chip's own inner glow token (distinct from the neutral inner glow baked into
        // PressHeldGlow* below, which composes press.held as a whole).
        public const float BloomHeldInsetBlur = 12f;
        public const float BloomHeldInsetAlpha = 0.50f;
        public const float BloomHeldInsetSpread = -3f;

        // Bloom hue table (derived from the roles — no new hues).
        public static readonly BloomHue BloomAccent = new(
            Halo: RgbaColor.FromRgba(r: 255, g: 106, b: 43, alpha: 0.42f),
            Ring: RgbaColor.FromRgba(r: 255, g: 106, b: 43, alpha: 0.55f)
        );
        public static readonly BloomHue BloomPositive = new(
            Halo: RgbaColor.FromRgba(r: 91, g: 201, b: 140, alpha: 0.42f),
            Ring: RgbaColor.FromRgba(r: 91, g: 201, b: 140, alpha: 0.55f)
        );
        public static readonly BloomHue BloomWarning = new(
            Halo: RgbaColor.FromRgba(r: 232, g: 179, b: 65, alpha: 0.42f),
            Ring: RgbaColor.FromRgba(r: 232, g: 179, b: 65, alpha: 0.55f)
        );
        public static readonly BloomHue BloomDanger = new(
            Halo: RgbaColor.FromRgba(r: 242, g: 86, b: 91, alpha: 0.42f),
            Ring: RgbaColor.FromRgba(r: 242, g: 86, b: 91, alpha: 0.55f)
        );
        public static readonly BloomHue BloomNeutral = new(
            Halo: RgbaColor.FromRgba(r: 237, g: 239, b: 242, alpha: 0.22f),
            Ring: RgbaColor.FromRgba(r: 237, g: 239, b: 242, alpha: 0.30f)
        );

        // press.held: inset shadow + inset glow + a 1px translate, applied to a physically-held chip.
        public const float PressHeldShadowOffsetY = 2f;
        public const float PressHeldShadowBlur = 6f;

        public static readonly RgbaColor PressHeldShadowColor = RgbaColor.FromRgba(r: 0, g: 0, b: 0, alpha: 0.60f);

        public const float PressHeldGlowBlur = 12f;
        public const float PressHeldGlowSpread = -3f;

        public static readonly RgbaColor PressHeldGlowColor = RgbaColor.FromRgba(r: 237, g: 239, b: 242, alpha: 0.24f);

        public const float PressHeldTranslateY = 1f;

        // shadow.seat / shadow.seat.strip: the seated drop-shadow under Tier-0 floats and strips.
        public const float ShadowSeatOffsetY = 18f;
        public const float ShadowSeatBlur = 44f;
        public const float ShadowSeatSpread = -18f;

        public static readonly RgbaColor ShadowSeatColor = RgbaColor.FromRgba(r: 0, g: 0, b: 0, alpha: 0.72f);

        public const float ShadowSeatStripSpread = -20f;

        public static readonly RgbaColor ShadowSeatStripColor = RgbaColor.FromRgba(r: 0, g: 0, b: 0, alpha: 0.75f);

        // ring.status — OPTIONAL second separation. When present it REPLACES the 1px bloom ring (never both);
        // dropping it only degrades separation redundancy, never legibility.
        public const float RingStatusWidth = 2f;
        public const float RingStatusAlpha = 0.60f;

        // catchlight — ONE token, optional/droppable (materiality, not contrast). Applied to Tier-0 floats and
        // strips only.
        public const float CatchlightOffsetY = 1f;

        public static readonly RgbaColor CatchlightColor = RgbaColor.FromRgba(r: 255, g: 255, b: 255, alpha: 0.05f);

        /// <summary>
        /// The edge-width law: every edge is a 1px hairline by default. Exactly three 2px signals exist —
        /// <see cref="RingStatusWidth"/>, the hub selection tick, and the toast state rail. Nothing is wider than 2px.
        /// </summary>
        public const float EdgeHairlineWidth = 1f;

        /// <summary>The REST-tier chip plate's translucency (the binding-bar graft's plate-darkness knob).</summary>
        public const float ChipRestOpacity = 0.62f;
    }

    /// <summary>
    /// Section 6 — the diegetic material's emboss/engrave physics (the WORLD-GEOMETRY tier; owned by a sibling
    /// work package, transcribed here only so the whole token set has one C# source). Law: raised fill is strictly
    /// brighter than the plate; engraved fill is strictly darker; the two shadows carry OPPOSITE polarity.
    /// </summary>
    public static class Diegetic {
        public static readonly RgbaColor PlateTop = RgbaColor.FromHex(hexRgb: 0x2C2F33);
        public static readonly RgbaColor PlateMid = RgbaColor.FromHex(hexRgb: 0x24272B);
        public static readonly RgbaColor PlateBottom = RgbaColor.FromHex(hexRgb: 0x1C1F22);
        public static readonly RgbaColor PlateStripeColor = RgbaColor.FromRgba(r: 255, g: 255, b: 255, alpha: 0.018f);
        public static readonly RgbaColor EmbossFill = RgbaColor.FromHex(hexRgb: 0xDFE6E1);

        public const float EmbossShadowDropAlpha = 0.80f;
        public const float EmbossShadowDropBlur = 2f;
        public const float EmbossShadowDropOffsetY = 2f;
        public const float EmbossShadowLitAlpha = 0.30f;
        public const float EmbossShadowLitOffsetY = -1f;

        public static readonly RgbaColor EngraveFill = RgbaColor.FromHex(hexRgb: 0x14171A);

        public const float EngraveShadowLipAlpha = 0.16f;
        public const float EngraveShadowLipOffsetY = 1f;
        public const float EngraveShadowRecessAlpha = 0.85f;
        public const float EngraveShadowRecessBlur = 1f;
        public const float EngraveShadowRecessOffsetY = -1f;

        // The CRT quote (the other half of the diegetic swatch): screen well radial gradient, phosphor text glow, bezel.
        public static readonly RgbaColor ScreenWellOuter = RgbaColor.FromHex(hexRgb: 0x0C221A);
        public static readonly RgbaColor ScreenWellInner = RgbaColor.FromHex(hexRgb: 0x05100C);

        public const float PhosphorGlowBlur = 6f;

        public static readonly RgbaColor BezelOuter = RgbaColor.FromHex(hexRgb: 0x23282B);
        public static readonly RgbaColor BezelInner = RgbaColor.FromHex(hexRgb: 0x14181A);
        public static readonly RgbaColor BezelEdge = RgbaColor.FromHex(hexRgb: 0x05070A);
    }

    /// <summary>
    /// Section 8 — motion. Delight is color/depth/typography, never motion: text never translates, only
    /// opacity/color/glow tween. Caps: interactions ≤ 180ms, panel transitions ≤ 320ms.
    /// </summary>
    public static class Motion {
        public const float DurFast = 120f;
        public const float DurMed = 180f;
        public const float DurPanel = 280f;

        public static readonly CubicBezier EaseStd = new(X1: 0.2f, Y1: 0f, X2: 0f, Y2: 1f);
        public static readonly CubicBezier EaseOut = new(X1: 0.4f, Y1: 0f, X2: 1f, Y2: 1f);
        /// <summary>The prompt caret's blink PERIOD, ms — <c>steps(1)</c>: a hard on/off toggle, never a fade.</summary>
        public const float CaretBlink = 1080f;
    }
}
