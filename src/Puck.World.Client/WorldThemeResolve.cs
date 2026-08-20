using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>
/// Resolves the document's authored <c>theme</c> section (<see cref="WorldDefinition.Theme"/>) against live state
/// into the mechanism-side <see cref="OverlayThemeValues"/> Puck.Overlays reads — the theme's counterpart to
/// <see cref="WorldRenderCycleTrack"/>, at the identical cadence: recomputed only when the definition revision
/// moves (a <c>state.&lt;row&gt;</c> binding's live value moves with it, since a state write bumps the revision the
/// same way any other mutation does). <see cref="WorldThemeCapacity.ScrimMinAlpha"/> clamps every resolved scrim
/// alpha here, unconditionally — a no-op for an already-validated literal, the actual floor enforcement for a state
/// binding the validator could not check at boot.
/// </summary>
public sealed class WorldThemeResolve {
    private int m_revision = -1;
    private OverlayThemeValues m_resolved;

    /// <summary>Resolves (or returns the cached resolve of) the theme for a definition revision.</summary>
    /// <param name="definition">The live document.</param>
    /// <param name="revision">The definition's current revision.</param>
    /// <param name="tick">The tick a bound cell's value is read as of.</param>
    /// <returns>The resolved theme.</returns>
    public OverlayThemeValues Resolve(WorldDefinition definition, int revision, ulong tick) {
        if (revision != m_revision) {
            m_revision = revision;
            m_resolved = ResolveCore(
                definition: definition,
                tick: tick
            );
        }

        return m_resolved;
    }

    private static RgbaColor ResolveColor(BindableColor color, WorldDefinition definition, ulong tick) {
        var resolved = color.Resolve(
            definition: definition,
            fallback: default,
            tick: tick
        );

        return new RgbaColor(
            A: resolved.W,
            B: resolved.Z,
            G: resolved.Y,
            R: resolved.X
        );
    }
    private static OverlayThemeValues.Scrim ResolveScrim(WorldThemeScrim scrim, WorldDefinition definition, ulong tick) {
        var alpha = scrim.Alpha.Resolve(
            definition: definition,
            fallback: 0f,
            tick: tick
        );

        return new OverlayThemeValues.Scrim(
            Alpha: MathF.Max(
                x: alpha,
                y: WorldThemeCapacity.ScrimMinAlpha
            ),
            Color: ResolveColor(
                color: scrim.Color,
                definition: definition,
                tick: tick
            )
        );
    }
    private static float ResolveScalar(BindableScalar scalar, WorldDefinition definition, ulong tick) => scalar.Resolve(
        definition: definition,
        fallback: 0f,
        tick: tick
    );
    private static BloomHue ResolveBloomHue(WorldThemeBloomHue hue, WorldDefinition definition, ulong tick) => new(
        Halo: ResolveColor(
            color: hue.Halo,
            definition: definition,
            tick: tick
        ),
        Ring: ResolveColor(
            color: hue.Ring,
            definition: definition,
            tick: tick
        )
    );
    private static CubicBezier ResolveBezier(WorldThemeCubicBezier bezier) => new(
        X1: bezier.X1,
        Y1: bezier.Y1,
        X2: bezier.X2,
        Y2: bezier.Y2
    );
    private static OverlayThemeValues.ColorSet ResolveColor(WorldThemeColor color, WorldDefinition definition, ulong tick) => new(
        Accent: ResolveColor(color: color.Accent, definition: definition, tick: tick),
        AccentInk: ResolveColor(color: color.AccentInk, definition: definition, tick: tick),
        AccentLine: ResolveColor(color: color.AccentLine, definition: definition, tick: tick),
        AccentQuiet: ResolveColor(color: color.AccentQuiet, definition: definition, tick: tick),
        BadgeDark: ResolveColor(color: color.BadgeDark, definition: definition, tick: tick),
        BadgeLight: ResolveColor(color: color.BadgeLight, definition: definition, tick: tick),
        Danger: ResolveColor(color: color.Danger, definition: definition, tick: tick),
        LineHair: ResolveColor(color: color.LineHair, definition: definition, tick: tick),
        LineInset: ResolveColor(color: color.LineInset, definition: definition, tick: tick),
        LineSoft: ResolveColor(color: color.LineSoft, definition: definition, tick: tick),
        LineStrong: ResolveColor(color: color.LineStrong, definition: definition, tick: tick),
        Phosphor: ResolveColor(color: color.Phosphor, definition: definition, tick: tick),
        PhosphorCyan: ResolveColor(color: color.PhosphorCyan, definition: definition, tick: tick),
        PhosphorDim: ResolveColor(color: color.PhosphorDim, definition: definition, tick: tick),
        Positive: ResolveColor(color: color.Positive, definition: definition, tick: tick),
        ScrimChip: ResolveScrim(scrim: color.ScrimChip, definition: definition, tick: tick),
        ScrimPanel: ResolveScrim(scrim: color.ScrimPanel, definition: definition, tick: tick),
        ScrimStrip: ResolveScrim(scrim: color.ScrimStrip, definition: definition, tick: tick),
        SurfaceBase: ResolveColor(color: color.SurfaceBase, definition: definition, tick: tick),
        SurfaceInset: ResolveColor(color: color.SurfaceInset, definition: definition, tick: tick),
        SurfacePanel: ResolveColor(color: color.SurfacePanel, definition: definition, tick: tick),
        SurfaceRaised: ResolveColor(color: color.SurfaceRaised, definition: definition, tick: tick),
        TextDim: ResolveColor(color: color.TextDim, definition: definition, tick: tick),
        TextMute: ResolveColor(color: color.TextMute, definition: definition, tick: tick),
        TextPrimary: ResolveColor(color: color.TextPrimary, definition: definition, tick: tick),
        Warning: ResolveColor(color: color.Warning, definition: definition, tick: tick)
    );
    private static OverlayThemeValues.SpaceSet ResolveSpace(WorldThemeSpace space) => new(
        HeightBadge: space.HeightBadge,
        HeightBindBar: space.HeightBindBar,
        HeightChip: space.HeightChip,
        HeightConsoleHead: space.HeightConsoleHead,
        HeightModeRow: space.HeightModeRow,
        HeightPromptRow: space.HeightPromptRow,
        HeightTrackerBar: space.HeightTrackerBar,
        HeightTrackerCell: space.HeightTrackerCell,
        Space0: space.Space0,
        Space1: space.Space1,
        Space2: space.Space2,
        Space3: space.Space3,
        Space4: space.Space4,
        Space5: space.Space5,
        Space6: space.Space6,
        Space8: space.Space8
    );
    private static OverlayThemeValues.RadiusSet ResolveRadius(WorldThemeRadius radius) => new(
        Radius1: radius.Radius1,
        Radius2: radius.Radius2,
        Radius3: radius.Radius3
    );
    private static OverlayThemeValues.TypeSet ResolveType(WorldThemeType type) => new(
        BodyLine: type.BodyLine,
        BodySize: type.BodySize,
        BodyWeight: type.BodyWeight,
        LabelLine: type.LabelLine,
        LabelSize: type.LabelSize,
        LabelTracking: type.LabelTracking,
        LabelWeight: type.LabelWeight,
        MicroLine: type.MicroLine,
        MicroSize: type.MicroSize,
        MicroTracking: type.MicroTracking,
        MicroWeight: type.MicroWeight,
        MonoBadgeSize: type.MonoBadgeSize,
        MonoLine: type.MonoLine,
        MonoReadoutSize: type.MonoReadoutSize,
        MonoSize: type.MonoSize,
        MonoTracking: type.MonoTracking,
        MonoWeight: type.MonoWeight,
        TitleLine: type.TitleLine,
        TitleSize: type.TitleSize,
        TitleTracking: type.TitleTracking,
        TitleWeight: type.TitleWeight
    );
    private static OverlayThemeValues.ElevationSet ResolveElevation(WorldThemeElevation elevation, WorldDefinition definition, ulong tick) => new(
        BloomAccent: ResolveBloomHue(hue: elevation.BloomAccent, definition: definition, tick: tick),
        BloomDanger: ResolveBloomHue(hue: elevation.BloomDanger, definition: definition, tick: tick),
        BloomHaloAlpha: ResolveScalar(scalar: elevation.BloomHaloAlpha, definition: definition, tick: tick),
        BloomHaloBlur: elevation.BloomHaloBlur,
        BloomHaloSpread: elevation.BloomHaloSpread,
        BloomHeldInsetAlpha: ResolveScalar(scalar: elevation.BloomHeldInsetAlpha, definition: definition, tick: tick),
        BloomHeldInsetBlur: elevation.BloomHeldInsetBlur,
        BloomHeldInsetSpread: elevation.BloomHeldInsetSpread,
        BloomNeutral: ResolveBloomHue(hue: elevation.BloomNeutral, definition: definition, tick: tick),
        BloomNeutralHaloAlpha: ResolveScalar(scalar: elevation.BloomNeutralHaloAlpha, definition: definition, tick: tick),
        BloomNeutralRingAlpha: ResolveScalar(scalar: elevation.BloomNeutralRingAlpha, definition: definition, tick: tick),
        BloomPositive: ResolveBloomHue(hue: elevation.BloomPositive, definition: definition, tick: tick),
        BloomRingAlpha: ResolveScalar(scalar: elevation.BloomRingAlpha, definition: definition, tick: tick),
        BloomRingWidth: elevation.BloomRingWidth,
        BloomWarning: ResolveBloomHue(hue: elevation.BloomWarning, definition: definition, tick: tick),
        CatchlightColor: ResolveColor(color: elevation.CatchlightColor, definition: definition, tick: tick),
        CatchlightOffsetY: elevation.CatchlightOffsetY,
        ChipRestOpacity: elevation.ChipRestOpacity,
        EdgeHairlineWidth: elevation.EdgeHairlineWidth,
        PressHeldGlowBlur: elevation.PressHeldGlowBlur,
        PressHeldGlowColor: ResolveColor(color: elevation.PressHeldGlowColor, definition: definition, tick: tick),
        PressHeldGlowSpread: elevation.PressHeldGlowSpread,
        PressHeldShadowBlur: elevation.PressHeldShadowBlur,
        PressHeldShadowColor: ResolveColor(color: elevation.PressHeldShadowColor, definition: definition, tick: tick),
        PressHeldShadowOffsetY: elevation.PressHeldShadowOffsetY,
        PressHeldTranslateY: elevation.PressHeldTranslateY,
        RingStatusAlpha: elevation.RingStatusAlpha,
        RingStatusWidth: elevation.RingStatusWidth,
        ShadowSeatBlur: elevation.ShadowSeatBlur,
        ShadowSeatColor: ResolveColor(color: elevation.ShadowSeatColor, definition: definition, tick: tick),
        ShadowSeatOffsetY: elevation.ShadowSeatOffsetY,
        ShadowSeatSpread: elevation.ShadowSeatSpread,
        ShadowSeatStripColor: ResolveColor(color: elevation.ShadowSeatStripColor, definition: definition, tick: tick),
        ShadowSeatStripSpread: elevation.ShadowSeatStripSpread
    );
    private static OverlayThemeValues.DiegeticSet ResolveDiegetic(WorldThemeDiegetic diegetic, WorldDefinition definition, ulong tick) => new(
        BezelEdge: ResolveColor(color: diegetic.BezelEdge, definition: definition, tick: tick),
        BezelInner: ResolveColor(color: diegetic.BezelInner, definition: definition, tick: tick),
        BezelOuter: ResolveColor(color: diegetic.BezelOuter, definition: definition, tick: tick),
        EmbossFill: ResolveColor(color: diegetic.EmbossFill, definition: definition, tick: tick),
        EmbossShadowDropAlpha: diegetic.EmbossShadowDropAlpha,
        EmbossShadowDropBlur: diegetic.EmbossShadowDropBlur,
        EmbossShadowDropOffsetY: diegetic.EmbossShadowDropOffsetY,
        EmbossShadowLitAlpha: diegetic.EmbossShadowLitAlpha,
        EmbossShadowLitOffsetY: diegetic.EmbossShadowLitOffsetY,
        EngraveFill: ResolveColor(color: diegetic.EngraveFill, definition: definition, tick: tick),
        EngraveShadowLipAlpha: diegetic.EngraveShadowLipAlpha,
        EngraveShadowLipOffsetY: diegetic.EngraveShadowLipOffsetY,
        EngraveShadowRecessAlpha: diegetic.EngraveShadowRecessAlpha,
        EngraveShadowRecessBlur: diegetic.EngraveShadowRecessBlur,
        EngraveShadowRecessOffsetY: diegetic.EngraveShadowRecessOffsetY,
        PhosphorGlowBlur: diegetic.PhosphorGlowBlur,
        PlateBottom: ResolveColor(color: diegetic.PlateBottom, definition: definition, tick: tick),
        PlateMid: ResolveColor(color: diegetic.PlateMid, definition: definition, tick: tick),
        PlateStripeColor: ResolveColor(color: diegetic.PlateStripeColor, definition: definition, tick: tick),
        PlateTop: ResolveColor(color: diegetic.PlateTop, definition: definition, tick: tick),
        ScreenWellInner: ResolveColor(color: diegetic.ScreenWellInner, definition: definition, tick: tick),
        ScreenWellOuter: ResolveColor(color: diegetic.ScreenWellOuter, definition: definition, tick: tick)
    );
    private static OverlayThemeValues.MotionSet ResolveMotion(WorldThemeMotion motion) => new(
        CaretBlink: motion.CaretBlink,
        DurFast: motion.DurFast,
        DurMed: motion.DurMed,
        DurPanel: motion.DurPanel,
        EaseOut: ResolveBezier(bezier: motion.EaseOut),
        EaseStd: ResolveBezier(bezier: motion.EaseStd)
    );
    private static OverlayThemeValues.IconSet ResolveIcon(WorldThemeIcon icon) => new(StrokeHalfWidth: icon.StrokeHalfWidth);
    private static OverlayThemeValues ResolveCore(WorldDefinition definition, ulong tick) {
        var theme = definition.Theme;

        return new OverlayThemeValues(
            Color: ResolveColor(color: theme.Color, definition: definition, tick: tick),
            Diegetic: ResolveDiegetic(diegetic: theme.Diegetic, definition: definition, tick: tick),
            Elevation: ResolveElevation(elevation: theme.Elevation, definition: definition, tick: tick),
            Icon: ResolveIcon(icon: theme.Icon),
            Motion: ResolveMotion(motion: theme.Motion),
            Radius: ResolveRadius(radius: theme.Radius),
            Space: ResolveSpace(space: theme.Space),
            Type: ResolveType(type: theme.Type)
        );
    }
}
