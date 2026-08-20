using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>One of the two settled cubic-bezier easings a theme's motion section authors.</summary>
/// <param name="X1">The first control point's x.</param>
/// <param name="Y1">The first control point's y.</param>
/// <param name="X2">The second control point's x.</param>
/// <param name="Y2">The second control point's y.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public readonly record struct WorldThemeCubicBezier(float X1, float Y1, float X2, float Y2) {
    /// <summary>Gets the inert absence — the identity curve's four zeroed control points.</summary>
    public static WorldThemeCubicBezier Absent { get; } = new(
        X1: 0f, Y1: 0f, X2: 0f, Y2: 0f
    );
}
/// <summary>One elevation bloom hue's lit ring + outer halo pair (see <c>Puck.Overlays.DesignTokens.Elevation</c>
/// for the composite rule). Each color carries its own baked alpha (<c>#RRGGBBAA</c>).</summary>
/// <param name="Ring">The 1px lit ring color.</param>
/// <param name="Halo">The outer distance-falloff halo color.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeBloomHue(BindableColor Ring, BindableColor Halo) {
    /// <summary>Gets the inert absence — a fully transparent ring and halo.</summary>
    public static WorldThemeBloomHue Absent { get; } = new(
        Halo: new BindableColor(Raw: "#00000000"),
        Ring: new BindableColor(Raw: "#00000000")
    );
}
/// <summary>One scrim's fill color plus its own alpha, split apart so a world can retheme opacity independent of
/// hue — the two knobs a scrim (a translucent panel/strip/chip backing) actually varies. <see cref="Alpha"/> is
/// clamped to <see cref="WorldThemeCapacity.ScrimMinAlpha"/> at resolve time when it is a state binding (see
/// <see cref="WorldDefinitionValidator"/>'s theme validation for the literal-authoring floor).</summary>
/// <param name="Color">The scrim's opaque fill color.</param>
/// <param name="Alpha">The scrim's opacity, in <c>[0, 1]</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeScrim(BindableColor Color, BindableScalar Alpha) {
    /// <summary>Gets the inert absence — a transparent black scrim.</summary>
    public static WorldThemeScrim Absent { get; } = new(
        Alpha: new BindableScalar(literal: 0f),
        Color: new BindableColor(Raw: "#000000")
    );
}
/// <summary>The theme's semantic color roles — the authored twin of <c>Puck.Overlays.DesignTokens.Color</c>.
/// Every field is a <see cref="BindableColor"/>: a hex literal, or a <c>state.&lt;row&gt;[.&lt;key&gt;]</c> binding.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeColor(
    BindableColor SurfaceBase,
    BindableColor SurfacePanel,
    BindableColor SurfaceRaised,
    BindableColor SurfaceInset,
    WorldThemeScrim ScrimPanel,
    WorldThemeScrim ScrimStrip,
    WorldThemeScrim ScrimChip,
    BindableColor LineHair,
    BindableColor LineSoft,
    BindableColor LineStrong,
    BindableColor LineInset,
    BindableColor TextPrimary,
    BindableColor TextDim,
    BindableColor TextMute,
    BindableColor Accent,
    BindableColor AccentQuiet,
    BindableColor AccentLine,
    BindableColor AccentInk,
    BindableColor Positive,
    BindableColor Warning,
    BindableColor Danger,
    BindableColor Phosphor,
    BindableColor PhosphorDim,
    BindableColor PhosphorCyan,
    BindableColor BadgeDark,
    BindableColor BadgeLight
) {
    /// <summary>Gets the inert absence — every surface, line, and text role transparent black; every scrim
    /// transparent.</summary>
    public static WorldThemeColor Absent { get; } = new(
        Accent: Zero, AccentInk: Zero, AccentLine: Zero, AccentQuiet: Zero,
        BadgeDark: Zero, BadgeLight: Zero, Danger: Zero, LineHair: Zero, LineInset: Zero,
        LineSoft: Zero, LineStrong: Zero, Phosphor: Zero, PhosphorCyan: Zero, PhosphorDim: Zero,
        Positive: Zero, ScrimChip: WorldThemeScrim.Absent, ScrimPanel: WorldThemeScrim.Absent,
        ScrimStrip: WorldThemeScrim.Absent, SurfaceBase: Zero, SurfaceInset: Zero, SurfacePanel: Zero,
        SurfaceRaised: Zero, TextDim: Zero, TextMute: Zero, TextPrimary: Zero, Warning: Zero
    );

    private static BindableColor Zero { get; } = new(Raw: "#00000000");
}
/// <summary>The theme's 4px spacing grid and grid-locked component heights — the authored twin of
/// <c>Puck.Overlays.DesignTokens.Space</c>. Every field is a plain float, px.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeSpace(
    float Space0,
    float Space1,
    float Space2,
    float Space3,
    float Space4,
    float Space5,
    float Space6,
    float Space8,
    float HeightBadge,
    float HeightBindBar,
    float HeightChip,
    float HeightConsoleHead,
    float HeightModeRow,
    float HeightPromptRow,
    float HeightTrackerBar,
    float HeightTrackerCell
) {
    /// <summary>Gets the inert absence — every grid step and component height zero.</summary>
    public static WorldThemeSpace Absent { get; } = new(
        HeightBadge: 0f, HeightBindBar: 0f, HeightChip: 0f, HeightConsoleHead: 0f, HeightModeRow: 0f,
        HeightPromptRow: 0f, HeightTrackerBar: 0f, HeightTrackerCell: 0f, Space0: 0f, Space1: 0f, Space2: 0f,
        Space3: 0f, Space4: 0f, Space5: 0f, Space6: 0f, Space8: 0f
    );
}
/// <summary>The theme's 3-step radius scale — the authored twin of <c>Puck.Overlays.DesignTokens.Radius</c>.
/// Every field is a plain float, px.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeRadius(float Radius1, float Radius2, float Radius3) {
    /// <summary>Gets the inert absence — every radius step zero.</summary>
    public static WorldThemeRadius Absent { get; } = new(
        Radius1: 0f, Radius2: 0f, Radius3: 0f
    );
}
/// <summary>The theme's 5-step type scale — the authored twin of <c>Puck.Overlays.DesignTokens.Type</c>.
/// Every size/line field is a plain float, px; every weight field is a plain int; every tracking field is a plain
/// float, em. <see cref="WorldThemeCapacity.TypeAbsoluteFloorSize"/> is enforced at the validator, never authored
/// here — a literal size below it refuses at boot, a bound one clamps at resolve.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeType(
    float BodySize,
    float BodyLine,
    int BodyWeight,
    float LabelSize,
    float LabelLine,
    float LabelTracking,
    int LabelWeight,
    float MicroSize,
    float MicroLine,
    float MicroTracking,
    int MicroWeight,
    float MonoSize,
    float MonoLine,
    float MonoTracking,
    int MonoWeight,
    float MonoBadgeSize,
    float MonoReadoutSize,
    float TitleSize,
    float TitleLine,
    float TitleTracking,
    int TitleWeight
) {
    /// <summary>Gets the inert absence — every size/line/tracking zero, every weight zero.</summary>
    public static WorldThemeType Absent { get; } = new(
        BodyLine: 0f, BodySize: 0f, BodyWeight: 0, LabelLine: 0f, LabelSize: 0f, LabelTracking: 0f,
        LabelWeight: 0, MicroLine: 0f, MicroSize: 0f, MicroTracking: 0f, MicroWeight: 0, MonoBadgeSize: 0f,
        MonoLine: 0f, MonoReadoutSize: 0f, MonoSize: 0f, MonoTracking: 0f, MonoWeight: 0, TitleLine: 0f,
        TitleSize: 0f, TitleTracking: 0f, TitleWeight: 0
    );
}
/// <summary>The theme's two-tier elevation recipe — the authored twin of
/// <c>Puck.Overlays.DesignTokens.Elevation</c>. Bloom/press/shadow/catchlight geometry scalars are plain
/// floats; the bloom alphas are <see cref="BindableScalar"/> (the one elevation knob worth live dynamism — a
/// lit-state pulse); the hue table and the press/shadow/catchlight colors are <see cref="BindableColor"/> with baked
/// alpha.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeElevation(
    float BloomHaloBlur,
    float BloomHaloSpread,
    BindableScalar BloomHaloAlpha,
    float BloomRingWidth,
    BindableScalar BloomRingAlpha,
    BindableScalar BloomNeutralHaloAlpha,
    BindableScalar BloomNeutralRingAlpha,
    float BloomHeldInsetBlur,
    float BloomHeldInsetSpread,
    BindableScalar BloomHeldInsetAlpha,
    WorldThemeBloomHue BloomAccent,
    WorldThemeBloomHue BloomPositive,
    WorldThemeBloomHue BloomWarning,
    WorldThemeBloomHue BloomDanger,
    WorldThemeBloomHue BloomNeutral,
    float PressHeldGlowBlur,
    float PressHeldGlowSpread,
    BindableColor PressHeldGlowColor,
    float PressHeldShadowBlur,
    float PressHeldShadowOffsetY,
    float PressHeldTranslateY,
    BindableColor PressHeldShadowColor,
    float ShadowSeatBlur,
    float ShadowSeatOffsetY,
    float ShadowSeatSpread,
    float ShadowSeatStripSpread,
    BindableColor ShadowSeatColor,
    BindableColor ShadowSeatStripColor,
    float CatchlightOffsetY,
    BindableColor CatchlightColor,
    float ChipRestOpacity,
    float EdgeHairlineWidth,
    float RingStatusWidth,
    float RingStatusAlpha
) {
    /// <summary>Gets the inert absence — every geometry scalar and alpha zero, every color transparent.</summary>
    public static WorldThemeElevation Absent { get; } = new(
        BloomAccent: WorldThemeBloomHue.Absent, BloomDanger: WorldThemeBloomHue.Absent,
        BloomHaloAlpha: Zero, BloomHaloBlur: 0f, BloomHaloSpread: 0f, BloomHeldInsetAlpha: Zero,
        BloomHeldInsetBlur: 0f, BloomHeldInsetSpread: 0f, BloomNeutral: WorldThemeBloomHue.Absent,
        BloomNeutralHaloAlpha: Zero, BloomNeutralRingAlpha: Zero, BloomPositive: WorldThemeBloomHue.Absent,
        BloomRingAlpha: Zero, BloomRingWidth: 0f, BloomWarning: WorldThemeBloomHue.Absent,
        CatchlightColor: ZeroColor, CatchlightOffsetY: 0f, ChipRestOpacity: 0f, EdgeHairlineWidth: 0f,
        PressHeldGlowBlur: 0f, PressHeldGlowColor: ZeroColor, PressHeldGlowSpread: 0f, PressHeldShadowBlur: 0f,
        PressHeldShadowColor: ZeroColor, PressHeldShadowOffsetY: 0f, PressHeldTranslateY: 0f,
        RingStatusAlpha: 0f, RingStatusWidth: 0f, ShadowSeatBlur: 0f, ShadowSeatColor: ZeroColor,
        ShadowSeatOffsetY: 0f, ShadowSeatSpread: 0f, ShadowSeatStripColor: ZeroColor, ShadowSeatStripSpread: 0f
    );

    private static BindableScalar Zero { get; } = new(literal: 0f);
    private static BindableColor ZeroColor { get; } = new(Raw: "#00000000");
}
/// <summary>The theme's diegetic material recipe (the world-geometry emboss/engrave physics plus the CRT quote) —
/// the authored twin of <c>Puck.Overlays.DesignTokens.Diegetic</c>. Shadow scalars are plain floats; every
/// color is <see cref="BindableColor"/>.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeDiegetic(
    BindableColor PlateTop,
    BindableColor PlateMid,
    BindableColor PlateBottom,
    BindableColor PlateStripeColor,
    BindableColor EmbossFill,
    BindableColor EngraveFill,
    float EmbossShadowDropAlpha,
    float EmbossShadowDropBlur,
    float EmbossShadowDropOffsetY,
    float EmbossShadowLitAlpha,
    float EmbossShadowLitOffsetY,
    float EngraveShadowLipAlpha,
    float EngraveShadowLipOffsetY,
    float EngraveShadowRecessAlpha,
    float EngraveShadowRecessBlur,
    float EngraveShadowRecessOffsetY,
    BindableColor ScreenWellOuter,
    BindableColor ScreenWellInner,
    BindableColor BezelOuter,
    BindableColor BezelInner,
    BindableColor BezelEdge,
    float PhosphorGlowBlur
) {
    /// <summary>Gets the inert absence — every color transparent black, every shadow scalar zero.</summary>
    public static WorldThemeDiegetic Absent { get; } = new(
        BezelEdge: Zero, BezelInner: Zero, BezelOuter: Zero, EmbossFill: Zero, EmbossShadowDropAlpha: 0f,
        EmbossShadowDropBlur: 0f, EmbossShadowDropOffsetY: 0f, EmbossShadowLitAlpha: 0f,
        EmbossShadowLitOffsetY: 0f, EngraveFill: Zero, EngraveShadowLipAlpha: 0f, EngraveShadowLipOffsetY: 0f,
        EngraveShadowRecessAlpha: 0f, EngraveShadowRecessBlur: 0f, EngraveShadowRecessOffsetY: 0f,
        PhosphorGlowBlur: 0f, PlateBottom: Zero, PlateMid: Zero, PlateStripeColor: Zero, PlateTop: Zero,
        ScreenWellInner: Zero, ScreenWellOuter: Zero
    );

    private static BindableColor Zero { get; } = new(Raw: "#00000000");
}
/// <summary>The theme's motion recipe — the authored twin of <c>Puck.Overlays.DesignTokens.Motion</c>.
/// Durations are plain floats, ms.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeMotion(
    float CaretBlink,
    float DurFast,
    float DurMed,
    float DurPanel,
    WorldThemeCubicBezier EaseStd,
    WorldThemeCubicBezier EaseOut
) {
    /// <summary>Gets the inert absence — every duration zero, both easings the identity curve.</summary>
    public static WorldThemeMotion Absent { get; } = new(
        CaretBlink: 0f, DurFast: 0f, DurMed: 0f, DurPanel: 0f,
        EaseOut: WorldThemeCubicBezier.Absent, EaseStd: WorldThemeCubicBezier.Absent
    );
}
/// <summary>The theme's procedural icon feel — the authored twin of the non-AA-ramp half of
/// <c>Puck.Overlays.DesignTokens.Icon</c> (the anti-alias ramps stay engine-side rendering-correctness
/// constants, never authored).</summary>
/// <param name="StrokeHalfWidth">The hairline stroke half-width every procedural glyph/icon draws with, in
/// glyph-local units.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeIcon(float StrokeHalfWidth) {
    /// <summary>Gets the inert absence — a zero-width stroke.</summary>
    public static WorldThemeIcon Absent { get; } = new(StrokeHalfWidth: 0f);
}
/// <summary>
/// The <c>theme</c> document section: the baked "Instrument + grafts" design system
/// (<c>Puck.Overlays.DesignTokens</c>), promoted to document data. A keyless section — one authored theme per
/// document, structured into the same categories the engine constant table used to hold.
/// </summary>
/// <param name="Color">The semantic color roles.</param>
/// <param name="Space">The spacing grid and component heights.</param>
/// <param name="Radius">The radius scale.</param>
/// <param name="Type">The type scale.</param>
/// <param name="Elevation">The two-tier elevation recipe.</param>
/// <param name="Diegetic">The diegetic material recipe.</param>
/// <param name="Motion">The motion recipe.</param>
/// <param name="Icon">The procedural icon feel.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldThemeSection(
    WorldThemeColor Color,
    WorldThemeSpace Space,
    WorldThemeRadius Radius,
    WorldThemeType Type,
    WorldThemeElevation Elevation,
    WorldThemeDiegetic Diegetic,
    WorldThemeMotion Motion,
    WorldThemeIcon Icon
) {
    /// <summary>Gets the inert absence — a fully zeroed token block (no authored theme, no chrome). The engine holds
    /// no theme of its own: the standard "Instrument + grafts" recipe is AUTHORED, in
    /// <c>Assets/worlds/standard.world.json</c>, and a world inherits it by naming that document as its basis.</summary>
    public static WorldThemeSection Absent { get; } = new(
        Color: WorldThemeColor.Absent, Diegetic: WorldThemeDiegetic.Absent, Elevation: WorldThemeElevation.Absent,
        Icon: WorldThemeIcon.Absent, Motion: WorldThemeMotion.Absent, Radius: WorldThemeRadius.Absent,
        Space: WorldThemeSpace.Absent, Type: WorldThemeType.Absent
    );
}
/// <summary>The theme's two engine-side perceptual floors — never authored, always enforced. A literal value below
/// either refuses at boot; a state-bound value clamps to it at resolve time instead (a live cell write cannot be
/// refused, so the floor still holds by construction).</summary>
public static class WorldThemeCapacity {
    /// <summary>The scrim opacity floor — the guaranteed-AA contrast contract under a scrim, over both a dark corner
    /// and a lit CRT.</summary>
    public const float ScrimMinAlpha = 0.84f;
    /// <summary>The type-size floor, px — below it, MSDF glyph coverage degrades over a moving world.</summary>
    public const float TypeAbsoluteFloorSize = 11f;
}
