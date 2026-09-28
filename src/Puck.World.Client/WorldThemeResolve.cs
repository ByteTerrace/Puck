using System.Numerics;
using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>
/// Resolves the document's authored <c>theme</c> section (<see cref="WorldDefinition.Theme"/>) against live state
/// into the mechanism-side <see cref="OverlayThemeValues"/> Puck.Overlays reads — the theme's counterpart to
/// <see cref="WorldRenderCycleTrack"/>: recomputed only when the definition revision moves or a
/// <see cref="WorldStateMirror"/> slot one of its own <c>state.&lt;row&gt;</c> tokens reads changes, never for a slot
/// some other consumer binds.
/// <see cref="WorldThemeCapacity.ScrimMinAlpha"/> clamps every resolved scrim alpha here, unconditionally — a no-op
/// for an already-validated literal, the actual floor enforcement for a state binding the validator could not check
/// at boot.
/// </summary>
public sealed class WorldThemeResolve {
    private ThemeReads? m_reads;
    private int m_generation;
    private OverlayThemeValues m_resolved;
    private int m_resolvedAt;
    private int m_resolutions;
    private int m_revision = -1;

    // The mirror reads one resolve makes, noting every slot a bound token reads so the next frame can ask whether any
    // of them moved.
    private sealed class ThemeReads(WorldStateMirror mirror) {
        public List<int> Bound { get; } = [];
        public WorldStateMirror Mirror { get; } = mirror;

        public Vector4 Color(in BindableColor color, Vector4 fallback) {
            Note(
                binding: color.State,
                conversion: WorldStateConversion.Color
            );

            return Mirror.Color(
                color: in color,
                fallback: fallback
            );
        }
        public float Scalar(in BindableScalar scalar, float fallback) {
            Note(
                binding: scalar.State,
                conversion: WorldStateConversion.Number
            );

            return Mirror.Scalar(
                fallback: fallback,
                scalar: in scalar
            );
        }

        private void Note(StateBinding? binding, WorldStateConversion conversion) {
            if (binding is { } bound) {
                Bound.Add(item: Mirror.SlotOf(
                    binding: in bound,
                    conversion: conversion
                ));
            }
        }
    }

    private static CubicBezier ResolveBezier(WorldThemeCubicBezier bezier) => new(
        X1: bezier.X1,
        Y1: bezier.Y1,
        X2: bezier.X2,
        Y2: bezier.Y2
    );
    private static BloomHue ResolveBloomHue(WorldThemeBloomHue hue, ThemeReads mirror) => new(
        Halo: ResolveColor(
            color: hue.Halo,
            mirror: mirror
        ),
        Ring: ResolveColor(
            color: hue.Ring,
            mirror: mirror
        )
    );
    private static OverlayThemeValues.ChromeSet ResolveChrome(WorldThemeChrome chrome) => new(
        BarHintAlpha: chrome.BarHintAlpha,
        BarLabelAlpha: chrome.BarLabelAlpha,
        CursorAlpha: chrome.CursorAlpha,
        CursorDotMaxHalf: chrome.CursorDotMaxHalf,
        CursorDotRatio: chrome.CursorDotRatio,
        CursorLabelGap: chrome.CursorLabelGap,
        DimQuietAlpha: chrome.DimQuietAlpha,
        WheelActiveRingAlpha: chrome.WheelActiveRingAlpha,
        WheelActiveRingOffset: chrome.WheelActiveRingOffset,
        WheelHubDotHalf: chrome.WheelHubDotHalf,
        WheelHubLabelGap: chrome.WheelHubLabelGap,
        WheelLabelAlpha: chrome.WheelLabelAlpha,
        WheelMarkerGapRatio: chrome.WheelMarkerGapRatio,
        WheelMarkerHalf: chrome.WheelMarkerHalf,
        WheelRingAlpha: chrome.WheelRingAlpha
    );
    private static RgbaColor ResolveColor(BindableColor color, ThemeReads mirror) {
        var resolved = mirror.Color(
            color: color,
            fallback: default
        );

        return new RgbaColor(
            A: resolved.W,
            B: resolved.Z,
            G: resolved.Y,
            R: resolved.X
        );
    }
    private static OverlayThemeValues.ColorSet ResolveColor(WorldThemeColor color, ThemeReads mirror) => new(
        Accent: ResolveColor(
            color: color.Accent,
            mirror: mirror
        ),
        AccentInk: ResolveColor(
            color: color.AccentInk,
            mirror: mirror
        ),
        AccentLine: ResolveColor(
            color: color.AccentLine,
            mirror: mirror
        ),
        AccentQuiet: ResolveColor(
            color: color.AccentQuiet,
            mirror: mirror
        ),
        BadgeDark: ResolveColor(
            color: color.BadgeDark,
            mirror: mirror
        ),
        BadgeLight: ResolveColor(
            color: color.BadgeLight,
            mirror: mirror
        ),
        Danger: ResolveColor(
            color: color.Danger,
            mirror: mirror
        ),
        LineHair: ResolveColor(
            color: color.LineHair,
            mirror: mirror
        ),
        LineInset: ResolveColor(
            color: color.LineInset,
            mirror: mirror
        ),
        LineSoft: ResolveColor(
            color: color.LineSoft,
            mirror: mirror
        ),
        LineStrong: ResolveColor(
            color: color.LineStrong,
            mirror: mirror
        ),
        Phosphor: ResolveColor(
            color: color.Phosphor,
            mirror: mirror
        ),
        PhosphorCyan: ResolveColor(
            color: color.PhosphorCyan,
            mirror: mirror
        ),
        PhosphorDim: ResolveColor(
            color: color.PhosphorDim,
            mirror: mirror
        ),
        Positive: ResolveColor(
            color: color.Positive,
            mirror: mirror
        ),
        ScrimChip: ResolveScrim(
            scrim: color.ScrimChip,
            mirror: mirror
        ),
        ScrimPanel: ResolveScrim(
            scrim: color.ScrimPanel,
            mirror: mirror
        ),
        ScrimStrip: ResolveScrim(
            scrim: color.ScrimStrip,
            mirror: mirror
        ),
        SurfaceBase: ResolveColor(
            color: color.SurfaceBase,
            mirror: mirror
        ),
        SurfaceInset: ResolveColor(
            color: color.SurfaceInset,
            mirror: mirror
        ),
        SurfacePanel: ResolveColor(
            color: color.SurfacePanel,
            mirror: mirror
        ),
        SurfaceRaised: ResolveColor(
            color: color.SurfaceRaised,
            mirror: mirror
        ),
        TextDim: ResolveColor(
            color: color.TextDim,
            mirror: mirror
        ),
        TextMute: ResolveColor(
            color: color.TextMute,
            mirror: mirror
        ),
        TextPrimary: ResolveColor(
            color: color.TextPrimary,
            mirror: mirror
        ),
        Warning: ResolveColor(
            color: color.Warning,
            mirror: mirror
        )
    );
    private static OverlayThemeValues ResolveCore(WorldDefinition definition, ThemeReads mirror) {
        var theme = definition.Theme;

        return new OverlayThemeValues(
            Chrome: ResolveChrome(chrome: theme.Chrome),
            Color: ResolveColor(
                color: theme.Color,
                mirror: mirror
            ),
            Diegetic: ResolveDiegetic(
                diegetic: theme.Diegetic,
                mirror: mirror
            ),
            Elevation: ResolveElevation(
                elevation: theme.Elevation,
                mirror: mirror
            ),
            Icon: ResolveIcon(icon: theme.Icon),
            Motion: ResolveMotion(motion: theme.Motion),
            Radius: ResolveRadius(radius: theme.Radius),
            Space: ResolveSpace(space: theme.Space),
            Type: ResolveType(type: theme.Type)
        );
    }
    private static OverlayThemeValues.DiegeticSet ResolveDiegetic(WorldThemeDiegetic diegetic, ThemeReads mirror) => new(
        BezelEdge: ResolveColor(
            color: diegetic.BezelEdge,
            mirror: mirror
        ),
        BezelInner: ResolveColor(
            color: diegetic.BezelInner,
            mirror: mirror
        ),
        BezelOuter: ResolveColor(
            color: diegetic.BezelOuter,
            mirror: mirror
        ),
        EmbossFill: ResolveColor(
            color: diegetic.EmbossFill,
            mirror: mirror
        ),
        EmbossShadowDropAlpha: diegetic.EmbossShadowDropAlpha,
        EmbossShadowDropBlur: diegetic.EmbossShadowDropBlur,
        EmbossShadowDropOffsetY: diegetic.EmbossShadowDropOffsetY,
        EmbossShadowLitAlpha: diegetic.EmbossShadowLitAlpha,
        EmbossShadowLitOffsetY: diegetic.EmbossShadowLitOffsetY,
        EngraveFill: ResolveColor(
            color: diegetic.EngraveFill,
            mirror: mirror
        ),
        EngraveShadowLipAlpha: diegetic.EngraveShadowLipAlpha,
        EngraveShadowLipOffsetY: diegetic.EngraveShadowLipOffsetY,
        EngraveShadowRecessAlpha: diegetic.EngraveShadowRecessAlpha,
        EngraveShadowRecessBlur: diegetic.EngraveShadowRecessBlur,
        EngraveShadowRecessOffsetY: diegetic.EngraveShadowRecessOffsetY,
        PhosphorGlowBlur: diegetic.PhosphorGlowBlur,
        PlateBottom: ResolveColor(
            color: diegetic.PlateBottom,
            mirror: mirror
        ),
        PlateMid: ResolveColor(
            color: diegetic.PlateMid,
            mirror: mirror
        ),
        PlateStripeColor: ResolveColor(
            color: diegetic.PlateStripeColor,
            mirror: mirror
        ),
        PlateTop: ResolveColor(
            color: diegetic.PlateTop,
            mirror: mirror
        ),
        ScreenWellInner: ResolveColor(
            color: diegetic.ScreenWellInner,
            mirror: mirror
        ),
        ScreenWellOuter: ResolveColor(
            color: diegetic.ScreenWellOuter,
            mirror: mirror
        )
    );
    private static OverlayThemeValues.ElevationSet ResolveElevation(WorldThemeElevation elevation, ThemeReads mirror) => new(
        BloomAccent: ResolveBloomHue(
            hue: elevation.BloomAccent,
            mirror: mirror
        ),
        BloomDanger: ResolveBloomHue(
            hue: elevation.BloomDanger,
            mirror: mirror
        ),
        BloomHaloAlpha: ResolveScalar(
            scalar: elevation.BloomHaloAlpha,
            mirror: mirror
        ),
        BloomHaloBlur: elevation.BloomHaloBlur,
        BloomHaloSpread: elevation.BloomHaloSpread,
        BloomHeldInsetAlpha: ResolveScalar(
            scalar: elevation.BloomHeldInsetAlpha,
            mirror: mirror
        ),
        BloomHeldInsetBlur: elevation.BloomHeldInsetBlur,
        BloomHeldInsetSpread: elevation.BloomHeldInsetSpread,
        BloomNeutral: ResolveBloomHue(
            hue: elevation.BloomNeutral,
            mirror: mirror
        ),
        BloomNeutralHaloAlpha: ResolveScalar(
            scalar: elevation.BloomNeutralHaloAlpha,
            mirror: mirror
        ),
        BloomNeutralRingAlpha: ResolveScalar(
            scalar: elevation.BloomNeutralRingAlpha,
            mirror: mirror
        ),
        BloomPositive: ResolveBloomHue(
            hue: elevation.BloomPositive,
            mirror: mirror
        ),
        BloomRingAlpha: ResolveScalar(
            scalar: elevation.BloomRingAlpha,
            mirror: mirror
        ),
        BloomRingWidth: elevation.BloomRingWidth,
        BloomWarning: ResolveBloomHue(
            hue: elevation.BloomWarning,
            mirror: mirror
        ),
        CatchlightColor: ResolveColor(
            color: elevation.CatchlightColor,
            mirror: mirror
        ),
        CatchlightOffsetY: elevation.CatchlightOffsetY,
        ChipRestOpacity: elevation.ChipRestOpacity,
        EdgeHairlineWidth: elevation.EdgeHairlineWidth,
        PressHeldGlowBlur: elevation.PressHeldGlowBlur,
        PressHeldGlowColor: ResolveColor(
            color: elevation.PressHeldGlowColor,
            mirror: mirror
        ),
        PressHeldGlowSpread: elevation.PressHeldGlowSpread,
        PressHeldShadowBlur: elevation.PressHeldShadowBlur,
        PressHeldShadowColor: ResolveColor(
            color: elevation.PressHeldShadowColor,
            mirror: mirror
        ),
        PressHeldShadowOffsetY: elevation.PressHeldShadowOffsetY,
        PressHeldTranslateY: elevation.PressHeldTranslateY,
        RingStatusAlpha: elevation.RingStatusAlpha,
        RingStatusWidth: elevation.RingStatusWidth,
        ShadowSeatBlur: elevation.ShadowSeatBlur,
        ShadowSeatColor: ResolveColor(
            color: elevation.ShadowSeatColor,
            mirror: mirror
        ),
        ShadowSeatOffsetY: elevation.ShadowSeatOffsetY,
        ShadowSeatSpread: elevation.ShadowSeatSpread,
        ShadowSeatStripColor: ResolveColor(
            color: elevation.ShadowSeatStripColor,
            mirror: mirror
        ),
        ShadowSeatStripSpread: elevation.ShadowSeatStripSpread
    );
    private static OverlayThemeValues.IconSet ResolveIcon(WorldThemeIcon icon) => new(StrokeHalfWidth: icon.StrokeHalfWidth);
    private static OverlayThemeValues.MotionSet ResolveMotion(WorldThemeMotion motion) => new(
        CaretBlink: motion.CaretBlink,
        DurFast: motion.DurFast,
        DurMed: motion.DurMed,
        DurPanel: motion.DurPanel,
        EaseOut: ResolveBezier(bezier: motion.EaseOut),
        EaseStd: ResolveBezier(bezier: motion.EaseStd)
    );
    private static OverlayThemeValues.RadiusSet ResolveRadius(WorldThemeRadius radius) => new(
        Radius1: radius.Radius1,
        Radius2: radius.Radius2,
        Radius3: radius.Radius3
    );
    private static float ResolveScalar(BindableScalar scalar, ThemeReads mirror) => mirror.Scalar(
        fallback: 0f,
        scalar: scalar
    );
    private static OverlayThemeValues.Scrim ResolveScrim(WorldThemeScrim scrim, ThemeReads mirror) {
        var alpha = mirror.Scalar(
            fallback: 0f,
            scalar: scrim.Alpha
        );

        return new OverlayThemeValues.Scrim(
            Alpha: MathF.Max(
                x: alpha,
                y: WorldThemeCapacity.ScrimMinAlpha
            ),
            Color: ResolveColor(
                color: scrim.Color,
                mirror: mirror
            )
        );
    }
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

    /// <summary>Gets how many times this resolver has resolved the theme rather than answering from its cache.</summary>
    public int Resolutions => m_resolutions;

    /// <summary>Resolves (or returns the cached resolve of) the theme for a definition revision and the state mirror
    /// slots its bound tokens read: a slot bound only by another consumer never re-resolves it.</summary>
    /// <param name="definition">The live document.</param>
    /// <param name="revision">The definition's current revision.</param>
    /// <param name="mirror">The state mirror a bound token reads through.</param>
    /// <returns>The resolved theme.</returns>
    public OverlayThemeValues Resolve(WorldDefinition definition, int revision, WorldStateMirror mirror) {
        ArgumentNullException.ThrowIfNull(argument: definition);
        ArgumentNullException.ThrowIfNull(argument: mirror);

        if (
            (revision != m_revision) ||
            !ReferenceEquals(
            objA: m_reads?.Mirror,
            objB: mirror
        ) ||
            (mirror.Generation != m_generation) ||
            BoundSlotMoved()
        ) {
            if (!ReferenceEquals(
                objA: m_reads?.Mirror,
                objB: mirror
            )) {
                m_reads = new ThemeReads(mirror: mirror);
            }

            m_reads!.Bound.Clear();
            m_revision = revision;
            m_resolved = ResolveCore(
                definition: definition,
                mirror: m_reads
            );
            m_resolvedAt = mirror.Revision;
            m_generation = mirror.Generation;
            m_resolutions++;
        }

        return m_resolved;
    }

    private bool BoundSlotMoved() {
        foreach (var slot in m_reads!.Bound) {
            if (m_reads.Mirror.Changed(slot: slot) > m_resolvedAt) {
                return true;
            }
        }

        return false;
    }
}
