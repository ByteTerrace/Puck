using Puck.Overlays;

namespace Puck.World.Client;

/// <summary>
/// Resolves the document's <c>theme</c> section (<see cref="WorldDefinition.Theme"/>, the engine's neutral default
/// when a document authors none) against live state into the mechanism-side <see cref="OverlayThemeValues"/>
/// Puck.Overlays reads — the theme's counterpart to <see cref="WorldEnvironmentResolve"/>: recomputed only when the
/// definition revision or its timeline moves, a <see cref="WorldStateMirror"/> slot one of its own
/// <c>state.&lt;row&gt;</c> tokens reads changes, a clock phase one of its keyed tokens reads moves, or the player's
/// <see cref="HighContrast"/> preference flips; never for a slot some other consumer binds. A keyed token resolves
/// through the mirror as every keyed value does. Every bindable scalar is mapped into its field's declared domain
/// (<see cref="WorldValueFields"/>), and a style color naming a core role takes that role's resolved color.
/// </summary>
public sealed class WorldThemeResolve {
    private readonly WorldValueDomainGuard m_domains;

    private bool m_resolvedHighContrast;
    private ThemeReads? m_reads;
    private int m_generation;
    private OverlayThemeValues m_resolved;
    private int m_resolvedAt;
    private int m_resolutions;
    private int m_revision = -1;
    private WorldTimelineSection? m_timeline;

    // The mirror reads one resolve makes, noting every slot a bound token reads so the next frame can ask whether any
    // of them moved.
    private sealed class ThemeReads(WorldStateMirror mirror, WorldValueDomainGuard domains) {
        public List<(int Slot, double Value)> Bound { get; } = [];
        public WorldStateMirror Mirror { get; } = mirror;
        public WorldClockReads Clocks { get; } = new();

        public RgbaColor Color(in BindableColor color) {
            Note(
                binding: color.State,
                conversion: WorldStateConversion.Color
            );
            NoteKeys(keys: color.Keys);

            var resolved = Mirror.Color(
                color: in color,
                fallback: default
            );

            return new RgbaColor(
                A: resolved.W,
                B: resolved.Z,
                G: resolved.Y,
                R: resolved.X
            );
        }
        public float Scalar(in BindableScalar scalar, WorldValueField field, string section) {
            Note(
                binding: scalar.State,
                conversion: WorldStateConversion.Number
            );
            NoteKeys(keys: scalar.Keys);

            return domains.Resolve(
                fallback: 0f,
                field: field,
                mirror: Mirror,
                scalar: in scalar,
                site: new WorldValueSite(Section: section),
                value: Mirror.Scalar(
                    fallback: 0f,
                    scalar: in scalar
                )
            );
        }

        // A keyed token re-resolves when its presented phase moves, including a session preview.
        private void NoteKeys(IWorldKeyTrack? keys) {
            if (keys is null) {
                return;
            }

            Clocks.Note(Mirror, keys.Clock);
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

    /// <summary>Returns the overlay's slab role a core theme role draws from: the one mapping from the document's role
    /// vocabulary onto <see cref="OverlayColorRole"/>, which Puck.World.Schema cannot reference.</summary>
    /// <param name="role">The core role.</param>
    /// <returns>The overlay role.</returns>
    public static OverlayColorRole RoleOf(WorldThemeRole role) => role switch {
        WorldThemeRole.SurfaceBase => OverlayColorRole.SurfaceBase,
        WorldThemeRole.SurfaceRaised => OverlayColorRole.SurfaceRaised,
        WorldThemeRole.SurfaceInset => OverlayColorRole.SurfaceInset,
        WorldThemeRole.ScrimPanel => OverlayColorRole.ScrimPanel,
        WorldThemeRole.ScrimStrip => OverlayColorRole.ScrimStrip,
        WorldThemeRole.ScrimChip => OverlayColorRole.ScrimChip,
        WorldThemeRole.LineHair => OverlayColorRole.LineHair,
        WorldThemeRole.LineSoft => OverlayColorRole.LineSoft,
        WorldThemeRole.TextDim => OverlayColorRole.TextDim,
        WorldThemeRole.TextMute => OverlayColorRole.TextMute,
        WorldThemeRole.Accent => OverlayColorRole.Accent,
        WorldThemeRole.AccentQuiet => OverlayColorRole.AccentQuiet,
        WorldThemeRole.AccentInk => OverlayColorRole.AccentInk,
        WorldThemeRole.Positive => OverlayColorRole.Positive,
        WorldThemeRole.Warning => OverlayColorRole.Warning,
        WorldThemeRole.Danger => OverlayColorRole.Danger,
        _ => OverlayColorRole.TextPrimary,
    };

    // A core role's resolved color, scrims with their alpha baked in as the slab bakes them.
    private static RgbaColor ColorOf(WorldThemeRole role, in OverlayThemeValues.ColorSet color) => role switch {
        WorldThemeRole.SurfaceBase => color.SurfaceBase,
        WorldThemeRole.SurfaceRaised => color.SurfaceRaised,
        WorldThemeRole.SurfaceInset => color.SurfaceInset,
        WorldThemeRole.ScrimPanel => color.ScrimPanel.Color with { A = color.ScrimPanel.Alpha },
        WorldThemeRole.ScrimStrip => color.ScrimStrip.Color with { A = color.ScrimStrip.Alpha },
        WorldThemeRole.ScrimChip => color.ScrimChip.Color with { A = color.ScrimChip.Alpha },
        WorldThemeRole.LineHair => color.LineHair,
        WorldThemeRole.LineSoft => color.LineSoft,
        WorldThemeRole.TextDim => color.TextDim,
        WorldThemeRole.TextMute => color.TextMute,
        WorldThemeRole.Accent => color.Accent,
        WorldThemeRole.AccentQuiet => color.AccentQuiet,
        WorldThemeRole.AccentInk => color.AccentInk,
        WorldThemeRole.Positive => color.Positive,
        WorldThemeRole.Warning => color.Warning,
        WorldThemeRole.Danger => color.Danger,
        _ => color.TextPrimary,
    };
    private static RgbaColor ResolveStyleColor(WorldThemeStyleColor style, in OverlayThemeValues.ColorSet core, ThemeReads mirror) => ((style.Role is { } role)
        ? ColorOf(
            color: in core,
            role: role
        )
        : mirror.Color(color: (style.Color ?? default)));
    private static OverlayThemeValues.ColorSet ResolveColor(WorldThemeColor color, ThemeReads mirror, bool highContrast) => new(
        Accent: mirror.Color(color: color.Accent),
        AccentInk: mirror.Color(color: color.AccentInk),
        AccentQuiet: mirror.Color(color: color.AccentQuiet),
        Danger: mirror.Color(color: color.Danger),
        LineHair: mirror.Color(color: color.LineHair),
        LineSoft: mirror.Color(color: color.LineSoft),
        Positive: mirror.Color(color: color.Positive),
        ScrimChip: ResolveScrim(
            highContrast: highContrast,
            mirror: mirror,
            scrim: color.ScrimChip,
            section: "theme.color.scrimChip"
        ),
        ScrimPanel: ResolveScrim(
            highContrast: highContrast,
            mirror: mirror,
            scrim: color.ScrimPanel,
            section: "theme.color.scrimPanel"
        ),
        ScrimStrip: ResolveScrim(
            highContrast: highContrast,
            mirror: mirror,
            scrim: color.ScrimStrip,
            section: "theme.color.scrimStrip"
        ),
        SurfaceBase: mirror.Color(color: color.SurfaceBase),
        SurfaceInset: mirror.Color(color: color.SurfaceInset),
        SurfaceRaised: mirror.Color(color: color.SurfaceRaised),
        TextDim: mirror.Color(color: color.TextDim),
        TextMute: mirror.Color(color: color.TextMute),
        TextPrimary: mirror.Color(color: color.TextPrimary),
        Warning: mirror.Color(color: color.Warning)
    );
    private static OverlayThemeValues ResolveCore(WorldDefinition definition, ThemeReads mirror, bool highContrast) {
        var theme = definition.Theme;
        var core = ResolveColor(
            color: theme.Color,
            highContrast: highContrast,
            mirror: mirror
        );
        var styles = theme.Styles;

        return new OverlayThemeValues(
            Color: core,
            Icon: new OverlayThemeValues.IconSet(StrokeHalfWidth: theme.Icon.StrokeHalfWidth),
            Motion: new OverlayThemeValues.MotionSet(DurMed: theme.Motion.DurMed),
            Radius: new OverlayThemeValues.RadiusSet(
                Radius1: theme.Radius.Radius1,
                Radius2: theme.Radius.Radius2,
                Radius3: theme.Radius.Radius3
            ),
            Space: new OverlayThemeValues.SpaceSet(
                Space2: theme.Space.Space2,
                Space3: theme.Space.Space3,
                Space8: theme.Space.Space8
            ),
            Stroke: new OverlayThemeValues.StrokeSet(Hairline: theme.Stroke.Hairline),
            Styles: new OverlayThemeValues.StyleSet(
                Bar: new OverlayThemeValues.BarStyle(
                    HintAlpha: styles.Bar.HintAlpha,
                    LabelAlpha: styles.Bar.LabelAlpha,
                    QuietAlpha: styles.Bar.QuietAlpha
                ),
                Console: new OverlayThemeValues.ConsoleStyle(
                    Echo: ResolveStyleColor(
                        core: in core,
                        mirror: mirror,
                        style: styles.Console.Echo
                    ),
                    HeadHeight: styles.Console.HeadHeight,
                    SelectionAlpha: styles.Console.SelectionAlpha
                ),
                Cursor: new OverlayThemeValues.CursorStyle(
                    Alpha: styles.Cursor.Alpha,
                    DotMaxHalf: styles.Cursor.DotMaxHalf,
                    DotRatio: styles.Cursor.DotRatio,
                    LabelGap: styles.Cursor.LabelGap
                ),
                Glow: new OverlayThemeValues.GlowStyle(
                    HaloAlpha: mirror.Scalar(
                        field: WorldValueFields.GlowHaloAlpha,
                        scalar: styles.Glow.HaloAlpha,
                        section: "theme.styles.glow"
                    ),
                    HaloBlur: styles.Glow.HaloBlur,
                    NeutralHaloAlpha: mirror.Scalar(
                        field: WorldValueFields.GlowNeutralHaloAlpha,
                        scalar: styles.Glow.NeutralHaloAlpha,
                        section: "theme.styles.glow"
                    ),
                    NeutralRingAlpha: mirror.Scalar(
                        field: WorldValueFields.GlowNeutralRingAlpha,
                        scalar: styles.Glow.NeutralRingAlpha,
                        section: "theme.styles.glow"
                    ),
                    RingAlpha: mirror.Scalar(
                        field: WorldValueFields.GlowRingAlpha,
                        scalar: styles.Glow.RingAlpha,
                        section: "theme.styles.glow"
                    )
                ),
                Hud: new OverlayThemeValues.HudStyle(TrackAlpha: styles.Hud.TrackAlpha),
                Keycap: new OverlayThemeValues.KeycapStyle(
                    Badge: ResolveStyleColor(
                        core: in core,
                        mirror: mirror,
                        style: styles.Keycap.Badge
                    ),
                    BadgeInk: ResolveStyleColor(
                        core: in core,
                        mirror: mirror,
                        style: styles.Keycap.BadgeInk
                    ),
                    ReferenceSize: styles.Keycap.ReferenceSize,
                    RestOpacity: styles.Keycap.RestOpacity,
                    ToggleMarch: styles.Keycap.ToggleMarch
                ),
                Toast: new OverlayThemeValues.ToastStyle(
                    IconSize: styles.Toast.IconSize,
                    MinHeight: styles.Toast.MinHeight,
                    RailWidth: styles.Toast.RailWidth
                ),
                Wheel: new OverlayThemeValues.WheelStyle(
                    ActiveRingAlpha: styles.Wheel.ActiveRingAlpha,
                    HubLabelGap: styles.Wheel.HubLabelGap,
                    LabelAlpha: styles.Wheel.LabelAlpha,
                    RingAlpha: styles.Wheel.RingAlpha
                )
            ),
            Type: new OverlayThemeValues.TypeSet(
                MicroSize: theme.Type.MicroSize,
                MonoSize: theme.Type.MonoSize
            )
        );
    }
    // A scrim's alpha, raised to the high-contrast floor while the player asks for it.
    private static OverlayThemeValues.Scrim ResolveScrim(WorldThemeScrim scrim, string section, ThemeReads mirror, bool highContrast) {
        var alpha = mirror.Scalar(
            field: WorldValueFields.ScrimAlpha,
            scalar: scrim.Alpha,
            section: section
        );

        return new OverlayThemeValues.Scrim(
            Alpha: (highContrast
                ? MathF.Max(
                    x: alpha,
                    y: WorldThemeCapacity.HighContrastScrimAlpha
                )
                : alpha),
            Color: mirror.Color(color: scrim.Color)
        );
    }

    /// <summary>Initializes a new instance of the <see cref="WorldThemeResolve"/> class.</summary>
    /// <param name="domains">The guard that holds the last valid value of a bound value and reports its transitions.</param>
    public WorldThemeResolve(WorldValueDomainGuard domains) => m_domains = (domains ?? throw new ArgumentNullException(paramName: nameof(domains)));

    /// <summary>Gets or sets whether the player asks for high contrast: every scrim resolves at least
    /// <see cref="WorldThemeCapacity.HighContrastScrimAlpha"/> opaque, so text on it stays legible over any scene. A
    /// presentation preference of this process, never document state; the next <see cref="Resolve"/> applies a
    /// change.</summary>
    public bool HighContrast { get; set; }
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
            (HighContrast != m_resolvedHighContrast) ||
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
            (m_reads?.Clocks.Moved(mirror: mirror) == true) ||
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
            m_reads.Clocks.Clear();
            m_revision = revision;
            m_resolvedHighContrast = HighContrast;
            m_timeline = definition.TimelineRaw;
            m_resolved = ResolveCore(
                definition: definition,
                highContrast: HighContrast,
                mirror: m_reads
            );
            m_resolvedAt = mirror.Revision;
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
