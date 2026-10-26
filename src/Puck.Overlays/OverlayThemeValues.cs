namespace Puck.Overlays;

/// <summary>
/// The resolved theme values every overlay CPU writer reads — the mechanism-side mirror of the document's authored
/// <c>theme</c> section (<c>Puck.World.WorldThemeSection</c>), which Puck.Overlays cannot reference (the layering runs
/// one way: the document authors MEANING, Puck.Overlays draws it — see <see cref="OverlayColorRole"/>'s and
/// <see cref="OverlayThemeStore"/>'s remarks). The composition root resolves every authored bindable color/scalar and
/// every style's role reference against live document state into this plain, already-resolved struct and republishes
/// it through <see cref="OverlayThemeStore"/> — nothing in this project parses a binding token or reads live state
/// itself. Each nested type mirrors one section of the authored theme.
/// </summary>
public readonly record struct OverlayThemeValues(
    OverlayThemeValues.ColorSet Color,
    OverlayThemeValues.SpaceSet Space,
    OverlayThemeValues.RadiusSet Radius,
    OverlayThemeValues.TypeSet Type,
    OverlayThemeValues.StrokeSet Stroke,
    OverlayThemeValues.MotionSet Motion,
    OverlayThemeValues.IconSet Icon,
    OverlayThemeValues.StyleSet Styles
) {
    /// <summary>One scrim's fill color plus its own opacity.</summary>
    public readonly record struct Scrim(RgbaColor Color, float Alpha);
    /// <summary>The core color roles.</summary>
    public readonly record struct ColorSet(
        RgbaColor SurfaceBase,
        RgbaColor SurfaceRaised,
        RgbaColor SurfaceInset,
        Scrim ScrimPanel,
        Scrim ScrimStrip,
        Scrim ScrimChip,
        RgbaColor LineHair,
        RgbaColor LineSoft,
        RgbaColor TextPrimary,
        RgbaColor TextDim,
        RgbaColor TextMute,
        RgbaColor Accent,
        RgbaColor AccentQuiet,
        RgbaColor AccentInk,
        RgbaColor Positive,
        RgbaColor Warning,
        RgbaColor Danger
    );
    /// <summary>The spacing steps, px.</summary>
    public readonly record struct SpaceSet(float Space2, float Space3, float Space8);
    /// <summary>The 3-step radius scale, px.</summary>
    public readonly record struct RadiusSet(float Radius1, float Radius2, float Radius3);
    /// <summary>The type sizes, px.</summary>
    public readonly record struct TypeSet(float MicroSize, float MonoSize);
    /// <summary>The stroke widths, px.</summary>
    public readonly record struct StrokeSet(float Hairline);
    /// <summary>The durations, ms.</summary>
    public readonly record struct MotionSet(float DurMed);
    /// <summary>The procedural icon feel that IS authored (the AA ramps stay <see cref="DesignTokens.Icon"/>
    /// rendering-correctness constants, never authored).</summary>
    public readonly record struct IconSet(float StrokeHalfWidth);
    /// <summary>The lit-state glow: a ring at a shape's edge plus a halo outside it.</summary>
    /// <param name="HaloBlur">The halo's reach, px.</param>
    /// <param name="RingAlpha">The ring's alpha.</param>
    /// <param name="HaloAlpha">The halo's alpha.</param>
    /// <param name="NeutralRingAlpha">The neutral tier's ring alpha.</param>
    /// <param name="NeutralHaloAlpha">The neutral tier's halo alpha.</param>
    public readonly record struct GlowStyle(float HaloBlur, float RingAlpha, float HaloAlpha, float NeutralRingAlpha, float NeutralHaloAlpha);
    /// <summary>The keycap (the icon plate with its physical-button badge).</summary>
    /// <param name="ReferenceSize">The plate height the px tokens are measured at, px.</param>
    /// <param name="RestOpacity">A resting plate's opacity.</param>
    /// <param name="Badge">The badge disc's backing.</param>
    /// <param name="BadgeInk">The badge's glyph.</param>
    /// <param name="ToggleMarch">The seconds one march of a latched toggle's border takes; 0 draws it still.</param>
    public readonly record struct KeycapStyle(float ReferenceSize, float RestOpacity, RgbaColor Badge, RgbaColor BadgeInk, float ToggleMarch);
    /// <summary>The binding bar's opacities.</summary>
    public readonly record struct BarStyle(float LabelAlpha, float HintAlpha, float QuietAlpha);
    /// <summary>The console panel.</summary>
    /// <param name="Echo">The color of an echoed input line and of the prompt.</param>
    /// <param name="HeadHeight">The title band's height, px.</param>
    /// <param name="SelectionAlpha">The selected input's highlight opacity.</param>
    public readonly record struct ConsoleStyle(RgbaColor Echo, float HeadHeight, float SelectionAlpha);
    /// <summary>The drawn cursor: ring opacity, center-dot ratio and ceiling (px), hover-label gap (px).</summary>
    public readonly record struct CursorStyle(float Alpha, float DotRatio, float DotMaxHalf, float LabelGap);
    /// <summary>The radial wheel: ring, active-ring and label opacities, and the hub-label gap (px).</summary>
    public readonly record struct WheelStyle(float RingAlpha, float ActiveRingAlpha, float LabelAlpha, float HubLabelGap);
    /// <summary>The toast: least height, status-square side and status-rail width, px.</summary>
    public readonly record struct ToastStyle(float MinHeight, float IconSize, float RailWidth);
    /// <summary>The authored HUD panels' own chrome.</summary>
    public readonly record struct HudStyle(float TrackAlpha);
    /// <summary>The component styles.</summary>
    public readonly record struct StyleSet(
        GlowStyle Glow,
        KeycapStyle Keycap,
        BarStyle Bar,
        ConsoleStyle Console,
        CursorStyle Cursor,
        WheelStyle Wheel,
        ToastStyle Toast,
        HudStyle Hud
    );

    /// <summary>Gets the inert zero theme — every color transparent black, every scalar zero: what a writer reads
    /// before the composition root publishes the resolved theme. The default value of this struct already IS this;
    /// this property only names the concept at call sites.</summary>
    public static OverlayThemeValues Zero { get; } = default;
}
