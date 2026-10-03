using System.Numerics;
using Puck.Hosting;
using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>
/// Resolves the document's authored <c>theme</c> section (<see cref="WorldDefinition.Theme"/>) against live state
/// into the mechanism-side <see cref="OverlayThemeValues"/> Puck.Overlays reads — the theme's counterpart to
/// <see cref="WorldEnvironmentResolve"/>: recomputed only when the definition revision or its timeline moves, a
/// <see cref="WorldStateMirror"/> slot one of its own <c>state.&lt;row&gt;</c> tokens or state clocks reads changes, or
/// the presented tick moves while one of its tokens is keyed on a tick clock or a moving anchor; never for a slot some
/// other consumer binds. A keyed token resolves through the mirror as every keyed value does. Every bindable scalar is
/// mapped into its field's declared domain (<see cref="WorldValueFields"/>), which holds a scrim alpha a live write
/// moves below <see cref="WorldThemeCapacity.ScrimMinAlpha"/> at that floor.
/// </summary>
public sealed class WorldThemeResolve {
    private readonly WorldValueDomainGuard m_domains;

    private ThemeReads? m_reads;
    private int m_generation;
    private OverlayThemeValues m_resolved;
    private int m_resolvedAt;
    private PresentedTick m_resolvedTick;
    private int m_resolutions;
    private int m_revision = -1;
    private WorldTimelineSection? m_timeline;

    // The mirror reads one resolve makes, noting every slot a bound token reads so the next frame can ask whether any
    // of them moved.
    private sealed class ThemeReads(WorldStateMirror mirror, WorldValueDomainGuard domains) {
        public List<(int Slot, double Value)> Bound { get; } = [];
        public WorldStateMirror Mirror { get; } = mirror;

        // Whether a keyed token reads a tick clock, whose phase moves with the presented tick.
        public bool ReadsTick { get; set; }

        public Vector4 Color(in BindableColor color, Vector4 fallback) {
            Note(
                binding: color.State,
                conversion: WorldStateConversion.Color
            );
            NoteKeys(keys: color.Keys);

            return Mirror.Color(
                color: in color,
                fallback: fallback
            );
        }
        public float Scalar(in BindableScalar scalar, float fallback, WorldValueField field, in WorldValueSite site) {
            Note(
                binding: scalar.State,
                conversion: WorldStateConversion.Number
            );
            NoteKeys(keys: scalar.Keys);

            return domains.Resolve(
                fallback: fallback,
                field: field,
                mirror: Mirror,
                scalar: in scalar,
                site: in site,
                value: Mirror.Scalar(
                    fallback: fallback,
                    scalar: in scalar
                )
            );
        }

        // A keyed token re-resolves when its clock moves: a state clock's slot, the presented tick a tick clock or a
        // moving anchor moves with, and for an anchor held still only the definition.
        private void NoteKeys(IWorldKeyTrack? keys) {
            if (keys is null) {
                return;
            }

            var slot = Mirror.ClockSlotOf(name: keys.Clock);

            if (slot >= 0) {
                NoteSlot(slot: slot);
            } else if (!Mirror.ClockHoldsStill(name: keys.Clock)) {
                ReadsTick = true;
            }
        }
        private void Note(StateBinding? binding, WorldStateConversion conversion) {
            if (binding is { } bound) {
                NoteSlot(slot: Mirror.SlotOf(
                    binding: in bound,
                    conversion: conversion
                ));
            }
        }
        private void NoteSlot(int slot) {
            _ = Mirror.TryValue(slot: slot, value: out var value);
            Bound.Add(item: (slot, value));
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
            mirror: mirror,
            scrim: color.ScrimChip,
            section: "theme.color.scrimChip"
        ),
        ScrimPanel: ResolveScrim(
            mirror: mirror,
            scrim: color.ScrimPanel,
            section: "theme.color.scrimPanel"
        ),
        ScrimStrip: ResolveScrim(
            mirror: mirror,
            scrim: color.ScrimStrip,
            section: "theme.color.scrimStrip"
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
            field: WorldValueFields.BloomHaloAlpha,
            mirror: mirror,
            scalar: elevation.BloomHaloAlpha
        ),
        BloomHaloBlur: elevation.BloomHaloBlur,
        BloomHaloSpread: elevation.BloomHaloSpread,
        BloomHeldInsetAlpha: ResolveScalar(
            field: WorldValueFields.BloomHeldInsetAlpha,
            mirror: mirror,
            scalar: elevation.BloomHeldInsetAlpha
        ),
        BloomHeldInsetBlur: elevation.BloomHeldInsetBlur,
        BloomHeldInsetSpread: elevation.BloomHeldInsetSpread,
        BloomNeutral: ResolveBloomHue(
            hue: elevation.BloomNeutral,
            mirror: mirror
        ),
        BloomNeutralHaloAlpha: ResolveScalar(
            field: WorldValueFields.BloomNeutralHaloAlpha,
            mirror: mirror,
            scalar: elevation.BloomNeutralHaloAlpha
        ),
        BloomNeutralRingAlpha: ResolveScalar(
            field: WorldValueFields.BloomNeutralRingAlpha,
            mirror: mirror,
            scalar: elevation.BloomNeutralRingAlpha
        ),
        BloomPositive: ResolveBloomHue(
            hue: elevation.BloomPositive,
            mirror: mirror
        ),
        BloomRingAlpha: ResolveScalar(
            field: WorldValueFields.BloomRingAlpha,
            mirror: mirror,
            scalar: elevation.BloomRingAlpha
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
    private static float ResolveScalar(BindableScalar scalar, WorldValueField field, ThemeReads mirror) => mirror.Scalar(
        fallback: 0f,
        field: field,
        scalar: scalar,
        site: new WorldValueSite(Section: "theme.elevation")
    );
    private static OverlayThemeValues.Scrim ResolveScrim(WorldThemeScrim scrim, string section, ThemeReads mirror) {
        var alpha = mirror.Scalar(
            fallback: 0f,
            field: WorldValueFields.ScrimAlpha,
            scalar: scrim.Alpha,
            site: new WorldValueSite(Section: section)
        );

        return new OverlayThemeValues.Scrim(
            Alpha: alpha,
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

    /// <summary>Initializes a new instance of the <see cref="WorldThemeResolve"/> class.</summary>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    public WorldThemeResolve(WorldValueDomainGuard domains) => m_domains = (domains ?? throw new ArgumentNullException(paramName: nameof(domains)));

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
            // A delivery that re-anchors a clock replaces the timeline without moving the revision.
            !ReferenceEquals(
            objA: definition.TimelineRaw,
            objB: m_timeline
        ) ||
            !ReferenceEquals(
            objA: m_reads?.Mirror,
            objB: mirror
        ) ||
            (mirror.Generation != m_generation) ||
            ((m_reads?.ReadsTick == true) && (mirror.Presented != m_resolvedTick)) ||
            BoundSlotMoved()
        ) {
            if (!ReferenceEquals(
                objA: m_reads?.Mirror,
                objB: mirror
            )) {
                m_reads = new ThemeReads(
                    domains: m_domains,
                    mirror: mirror
                );
            }

            m_reads!.Bound.Clear();
            m_reads.ReadsTick = false;
            m_revision = revision;
            m_timeline = definition.TimelineRaw;
            m_resolved = ResolveCore(
                definition: definition,
                mirror: m_reads
            );
            m_resolvedAt = mirror.Revision;
            m_resolvedTick = mirror.Presented;
            m_generation = mirror.Generation;
            m_resolutions++;
        }

        return m_resolved;
    }

    private bool BoundSlotMoved() {
        foreach (var (slot, value) in m_reads!.Bound) {
            _ = m_reads.Mirror.TryValue(slot: slot, value: out var presented);

            if ((m_reads.Mirror.Changed(slot: slot) > m_resolvedAt) || (presented != value)) {
                return true;
            }
        }

        return false;
    }
}
