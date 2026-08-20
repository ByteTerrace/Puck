namespace Puck.Overlays;

/// <summary>
/// The resolved theme values every overlay CPU writer reads — the mechanism-side mirror of the document's authored
/// <c>theme</c> section (<c>Puck.World.Schema.WorldThemeSection</c>), which Puck.Overlays cannot reference (the
/// layering runs one way: the document authors MEANING, Puck.Overlays draws it — see
/// <see cref="OverlayColorRole"/>'s and <see cref="OverlayThemeStore"/>'s remarks). The composition root resolves
/// every authored bindable color/scalar against live document state into this plain, already-resolved struct and
/// republishes it through <see cref="OverlayThemeStore"/> — nothing in this project parses a binding token or reads
/// live state itself. Every nested type mirrors one former <c>DesignTokens</c> section 1:1.
/// </summary>
public readonly record struct OverlayThemeValues(
    OverlayThemeValues.ColorSet Color,
    OverlayThemeValues.SpaceSet Space,
    OverlayThemeValues.RadiusSet Radius,
    OverlayThemeValues.TypeSet Type,
    OverlayThemeValues.ElevationSet Elevation,
    OverlayThemeValues.DiegeticSet Diegetic,
    OverlayThemeValues.MotionSet Motion,
    OverlayThemeValues.IconSet Icon,
    OverlayThemeValues.ChromeSet Chrome
) {
    /// <summary>One scrim's fill color plus its own opacity.</summary>
    public readonly record struct Scrim(RgbaColor Color, float Alpha);
    /// <summary>The semantic color roles.</summary>
    public readonly record struct ColorSet(
        RgbaColor SurfaceBase,
        RgbaColor SurfacePanel,
        RgbaColor SurfaceRaised,
        RgbaColor SurfaceInset,
        Scrim ScrimPanel,
        Scrim ScrimStrip,
        Scrim ScrimChip,
        RgbaColor LineHair,
        RgbaColor LineSoft,
        RgbaColor LineStrong,
        RgbaColor LineInset,
        RgbaColor TextPrimary,
        RgbaColor TextDim,
        RgbaColor TextMute,
        RgbaColor Accent,
        RgbaColor AccentQuiet,
        RgbaColor AccentLine,
        RgbaColor AccentInk,
        RgbaColor Positive,
        RgbaColor Warning,
        RgbaColor Danger,
        RgbaColor Phosphor,
        RgbaColor PhosphorDim,
        RgbaColor PhosphorCyan,
        RgbaColor BadgeDark,
        RgbaColor BadgeLight
    );
    /// <summary>The 4px spacing grid and grid-locked component heights, px.</summary>
    public readonly record struct SpaceSet(
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
    );
    /// <summary>The 3-step radius scale, px.</summary>
    public readonly record struct RadiusSet(float Radius1, float Radius2, float Radius3);
    /// <summary>The 5-step type scale — sizes/lines px, weights integer, trackings em.</summary>
    public readonly record struct TypeSet(
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
    );
    /// <summary>The two-tier elevation recipe.</summary>
    public readonly record struct ElevationSet(
        float BloomHaloBlur,
        float BloomHaloSpread,
        float BloomHaloAlpha,
        float BloomRingWidth,
        float BloomRingAlpha,
        float BloomNeutralHaloAlpha,
        float BloomNeutralRingAlpha,
        float BloomHeldInsetBlur,
        float BloomHeldInsetSpread,
        float BloomHeldInsetAlpha,
        BloomHue BloomAccent,
        BloomHue BloomPositive,
        BloomHue BloomWarning,
        BloomHue BloomDanger,
        BloomHue BloomNeutral,
        float PressHeldGlowBlur,
        float PressHeldGlowSpread,
        RgbaColor PressHeldGlowColor,
        float PressHeldShadowBlur,
        float PressHeldShadowOffsetY,
        float PressHeldTranslateY,
        RgbaColor PressHeldShadowColor,
        float ShadowSeatBlur,
        float ShadowSeatOffsetY,
        float ShadowSeatSpread,
        float ShadowSeatStripSpread,
        RgbaColor ShadowSeatColor,
        RgbaColor ShadowSeatStripColor,
        float CatchlightOffsetY,
        RgbaColor CatchlightColor,
        float ChipRestOpacity,
        float EdgeHairlineWidth,
        float RingStatusWidth,
        float RingStatusAlpha
    );
    /// <summary>The diegetic material recipe (the world-geometry emboss/engrave physics plus the CRT quote).</summary>
    public readonly record struct DiegeticSet(
        RgbaColor PlateTop,
        RgbaColor PlateMid,
        RgbaColor PlateBottom,
        RgbaColor PlateStripeColor,
        RgbaColor EmbossFill,
        RgbaColor EngraveFill,
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
        RgbaColor ScreenWellOuter,
        RgbaColor ScreenWellInner,
        RgbaColor BezelOuter,
        RgbaColor BezelInner,
        RgbaColor BezelEdge,
        float PhosphorGlowBlur
    );
    /// <summary>The motion recipe — durations ms, easings the CSS cubic-bezier control points.</summary>
    public readonly record struct MotionSet(
        float CaretBlink,
        float DurFast,
        float DurMed,
        float DurPanel,
        CubicBezier EaseStd,
        CubicBezier EaseOut
    );
    /// <summary>The procedural icon feel that IS authored (the AA ramps stay <see cref="DesignTokens.Icon"/>
    /// rendering-correctness constants, never authored).</summary>
    public readonly record struct IconSet(float StrokeHalfWidth);
    /// <summary>The CPU writers' own chrome — the quiet/lit opacities and the px geometry each writer used to hold
    /// as a private constant. Alphas are <c>[0, 1]</c>, <c>*Half</c>/<c>*Gap</c>/<c>*Offset</c> are px, and
    /// <c>*Ratio</c> is a multiple of the shape it names.</summary>
    /// <param name="DimQuietAlpha">The one quiet-dim opacity every writer reaches for: an unbound bar slot, an
    /// unheld modifier indicator, a gauge track, a console selection band.</param>
    /// <param name="BarLabelAlpha">The binding bar's page-name opacity.</param>
    /// <param name="BarHintAlpha">The binding bar's chord-hint opacity.</param>
    /// <param name="CursorAlpha">The drawn cursor's ring opacity.</param>
    /// <param name="CursorDotRatio">The cursor's center-dot half-extent, as a fraction of the ring radius.</param>
    /// <param name="CursorDotMaxHalf">The cursor center dot's half-extent ceiling, px.</param>
    /// <param name="CursorLabelGap">The hover label's clearance outside the ring, px.</param>
    /// <param name="WheelRingAlpha">A non-active wheel ring's stroke and label opacity.</param>
    /// <param name="WheelActiveRingAlpha">The active wheel ring's stroke opacity.</param>
    /// <param name="WheelActiveRingOffset">The second (heavier-shell) stroke's radial offset, px.</param>
    /// <param name="WheelLabelAlpha">The hub dot's, marker's, and active-ring labels' opacity.</param>
    /// <param name="WheelHubDotHalf">The wheel hub dot's half-extent, px.</param>
    /// <param name="WheelMarkerHalf">The hovered-sector marker dot's half-extent, px.</param>
    /// <param name="WheelMarkerGapRatio">The marker's clearance outside the ring, as a multiple of the cell
    /// height.</param>
    /// <param name="WheelHubLabelGap">The active-ring label's clearance below the hub dot, px.</param>
    public readonly record struct ChromeSet(
        float DimQuietAlpha,
        float BarLabelAlpha,
        float BarHintAlpha,
        float CursorAlpha,
        float CursorDotRatio,
        float CursorDotMaxHalf,
        float CursorLabelGap,
        float WheelRingAlpha,
        float WheelActiveRingAlpha,
        float WheelActiveRingOffset,
        float WheelLabelAlpha,
        float WheelHubDotHalf,
        float WheelMarkerHalf,
        float WheelMarkerGapRatio,
        float WheelHubLabelGap
    );

    /// <summary>Gets the inert zero theme (no authored theme, no chrome) — every color transparent black, every
    /// scalar zero. The default value of this struct already IS this, since every nested field type zero-defaults;
    /// this property exists only to name the concept at call sites.</summary>
    public static OverlayThemeValues Zero { get; } = default;
}
