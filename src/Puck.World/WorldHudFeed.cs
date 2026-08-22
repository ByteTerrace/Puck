using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The World-side feed behind the unified overlay's authored-HUD source. Two reconcile domains share one published
/// <see cref="HudStore"/> frame: world-scope panels (<see cref="WorldDefinition.Hud"/>) reconcile only when
/// <see cref="WorldClient.DefinitionRevision"/> moves (the <c>WorldFramePresenter</c> revision-reconcile pattern), so
/// most produced frames pay only the revision-compare cost; player-scope panels (each joined seat's
/// <c>WorldProfile.Hud</c>) recompose every produced frame from a preallocated array — they depend on the roster
/// (who is joined, in what view order), which the definition revision does not cover; the walk is bounded to
/// <see cref="PlayerRoster.MaxSlots"/> seats and each seat's panel is memoized against the document row it was
/// built from, so a steady frame allocates nothing. Both halves publish
/// together (one <see cref="HudStore.Publish"/> call) so neither half's snapshot can lag behind a call that only
/// refreshed the other. Live binding values are resolved separately, every frame, by <see cref="HudWriter"/> through
/// <see cref="WorldHudBindingResolver"/>; this feed only republishes structure (which panels/elements exist, their
/// rects, their style, which binding token or parsed template runs each names, and which seat's viewport a
/// player-scope panel is confined to).
/// </summary>
internal sealed class WorldHudFeed(WorldClient client, PlayerRoster roster, HudStore store, WorldOverlayFacts facts, WorldOverlayFrameSources frameSources) {
    private readonly WorldClient m_client = client;
    private readonly WorldOverlayFacts m_facts = facts;
    private readonly WorldOverlayFrameSources m_frameSources = frameSources;
    private readonly PlayerRoster m_roster = roster;
    private readonly HudStore m_store = store;
    private readonly OverlayHudSeatPanel[] m_seatPanels = new OverlayHudSeatPanel[PlayerRoster.MaxSlots];
    // Per-seat structure memo: the document row a seat's panel was last built FROM, and the build. An identity
    // edit publishes a new WorldHudPanel instance, so reference identity is an exact staleness test — the cheap
    // revision check world scope has, expressed at the only grain seat scope offers.
    private readonly WorldHudPanel?[] m_seatSources = new WorldHudPanel?[PlayerRoster.MaxSlots];
    private readonly OverlayHudPanel[] m_seatBuilds = new OverlayHudPanel[PlayerRoster.MaxSlots];
    private int m_seenRevision = -1;
    private OverlayHudPanel[] m_worldPanels = [];
    private OverlayHudPanel[] m_visiblePanels = [];
    private WorldHudPanel[] m_worldSources = [];

    // A Frame element's Source (present only for that kind, enforced at validation) resolves through the process's
    // one WorldOverlayFrameSources — the same key a non-Frame element carries -1 for, meaning "no source to bind".
    // seat is the enclosing seat scope a bare (Seat-less) camera source falls back to: the owning identity panel's
    // slot+1 for a player-scope panel, or 1 for a world-scope panel.
    private OverlayHudElement[] BuildElements(IReadOnlyList<WorldHudElement> elements, int seat) {
        var built = new OverlayHudElement[elements.Count];

        for (var index = 0; (index < elements.Count); index++) {
            var element = elements[index];

            built[index] = new OverlayHudElement(
                Kind: ToKind(kind: element.Kind),
                Rect: ToOverlayRect(rect: element.Rect),
                Role: ToRole(token: element.Style),
                Text: element.Text,
                Binding: element.Binding,
                Template: BuildTemplate(template: element.Template),
                FrameSource: ((element.Source is { } source)
                    ? m_frameSources.KeyFor(source: source, seat: seat)
                    : -1
                ),
                Fit: ToFit(fit: element.Fit),
                Mirror: element.Mirror,
                Radius: element.Radius,
                Opacity: element.Opacity
            );
        }

        return built;
    }
    private OverlayHudPanel BuildPanel(WorldHudPanel panel, int seat) {
        return new OverlayHudPanel(
            Id: panel.Id,
            Rect: ToOverlayRect(rect: panel.Rect),
            Band: ToBand(layer: panel.Layer),
            Style: ToStyle(style: panel.Style),
            Elements: BuildElements(elements: panel.Elements, seat: seat)
        );
    }
    private OverlayHudPanel[] BuildPanels(IReadOnlyList<WorldHudPanel> panels) {
        var built = new OverlayHudPanel[panels.Count];

        for (var index = 0; (index < panels.Count); index++) {
            built[index] = BuildPanel(panel: panels[index], seat: 1);
        }

        return built;
    }
    // Walks the joined roster in view order (the SAME order WorldOverlayFeed lays seats out in — LayoutRegion is a
    // pure function of (count, view index), so calling it here with identical arguments reproduces the exact rect a
    // seat's binding bar/editor HUD already renders in, with no cross-feed dependency), publishing one entry per
    // seat that is BOTH joined and has authored a player-scope panel. Reuses the preallocated array, and rebuilds a
    // seat's panel only when its document row is a different instance — zero steady-state allocation.
    private int BuildSeatPanels() {
        var joined = m_roster.Count;
        var viewIndex = 0;
        var count = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            var localViewIndex = viewIndex++;

            if (
                (m_roster.ProfileAt(slot: slot) is not { Hud: { } panel }) ||
                !m_facts.Evaluate(
                predicate: panel.Visible,
                slot: slot
            )
            ) {
                continue;
            }

            var viewport = WorldFramePresenter.LayoutRegion(
                count: joined,
                index: localViewIndex
            );

            if (!ReferenceEquals(
                objA: m_seatSources[slot],
                objB: panel
            )) {
                m_seatSources[slot] = panel;
                m_seatBuilds[slot] = BuildPanel(panel: panel, seat: (slot + 1));
            }

            m_seatPanels[count++] = new OverlayHudSeatPanel(
                Viewport: new OverlayHudRect(
                    X: viewport.X,
                    Y: viewport.Y,
                    Width: viewport.Width,
                    Height: viewport.Height
                ),
                Panel: m_seatBuilds[slot]
            );
        }

        return count;
    }
    // The one place a template string is parsed on the way to the screen: HudTemplate (Puck.World.Schema) owns the
    // brace/escape grammar, and Puck.Overlays — which cannot reference that project — receives RUNS, never a
    // grammar to restate. Parsing here also moves the cost off the per-frame render path onto this structure
    // rebuild. A template that reaches a live document has already been proven well formed by
    // WorldDefinitionValidator, so the parse failure below is unreachable by construction; it degrades to one
    // literal run carrying the raw text, which puts the anomaly on screen rather than blanking the element.
    private static ReadOnlyMemory<OverlayHudTemplateSegment> BuildTemplate(string? template) {
        if (template is not { Length: > 0 }) {
            return default;
        }

        if (!HudTemplate.TryParse(
            error: out _,
            segments: out var segments,
            template: template
        )) {
            return new[] { new OverlayHudTemplateSegment(
                IsPlaceholder: false,
                Text: template
            ) };
        }

        var built = new OverlayHudTemplateSegment[segments.Count];

        for (var index = 0; (index < segments.Count); index++) {
            built[index] = new OverlayHudTemplateSegment(
                IsPlaceholder: segments[index].IsPlaceholder,
                Text: segments[index].Text
            );
        }

        return built;
    }
    private static OverlayHudBand ToBand(WorldHudLayer layer) => layer switch {
        WorldHudLayer.Under => OverlayHudBand.Under,
        WorldHudLayer.Over => OverlayHudBand.Over,
        WorldHudLayer.Replace => OverlayHudBand.Replace,
        _ => OverlayHudBand.Under,
    };
    private static OverlayHudElementKind ToKind(WorldHudElementKind kind) => kind switch {
        WorldHudElementKind.Rect => OverlayHudElementKind.Rect,
        WorldHudElementKind.Text => OverlayHudElementKind.Text,
        WorldHudElementKind.Gauge => OverlayHudElementKind.Gauge,
        WorldHudElementKind.Frame => OverlayHudElementKind.Frame,
        _ => OverlayHudElementKind.Rect,
    };
    private static OverlayHudFrameFit ToFit(WorldHudFrameFit fit) => fit switch {
        WorldHudFrameFit.Cover => OverlayHudFrameFit.Cover,
        WorldHudFrameFit.Contain => OverlayHudFrameFit.Contain,
        WorldHudFrameFit.Stretch => OverlayHudFrameFit.Stretch,
        _ => OverlayHudFrameFit.Cover,
    };
    private static OverlayHudRect ToOverlayRect(WorldHudRect rect) => new(
        X: rect.X,
        Y: rect.Y,
        Width: rect.Width,
        Height: rect.Height
    );
    private static OverlayColorRole ToRole(WorldHudStyleToken token) => token switch {
        WorldHudStyleToken.Primary => OverlayColorRole.TextPrimary,
        WorldHudStyleToken.Dim => OverlayColorRole.TextDim,
        WorldHudStyleToken.Accent => OverlayColorRole.Accent,
        WorldHudStyleToken.Positive => OverlayColorRole.Positive,
        WorldHudStyleToken.Warning => OverlayColorRole.Warning,
        WorldHudStyleToken.Danger => OverlayColorRole.Danger,
        _ => OverlayColorRole.TextPrimary,
    };
    private static OverlayPanelStyle ToStyle(WorldHudPanelStyle style) => style switch {
        WorldHudPanelStyle.Panel => OverlayPanelStyle.Panel,
        WorldHudPanelStyle.Strip => OverlayPanelStyle.Strip,
        WorldHudPanelStyle.Chip => OverlayPanelStyle.Chip,
        _ => OverlayPanelStyle.Panel,
    };

    /// <summary>Reconciles the world-scope structure snapshot if the definition revision moved since the last call,
    /// recomposes the player-scope seat panels unconditionally, and publishes both together. Cheap to call every
    /// produced frame (the render thread's <c>FeedTick</c>).</summary>
    public void Tick() {
        var revision = m_client.DefinitionRevision;

        var hud = m_client.Definition.Hud;

        if (revision != m_seenRevision) {
            m_seenRevision = revision;
            m_worldSources = (hud.Defaults.Enabled
                ? [.. hud.Panels]
                : []
            );
            m_worldPanels = BuildPanels(panels: m_worldSources);
            m_visiblePanels = new OverlayHudPanel[m_worldPanels.Length];
        }

        // The world-scope gate (hud.defaults.visible) then each panel's own; both evaluated across every joined seat.
        var visibleCount = 0;

        if (m_facts.EvaluateAnySeat(predicate: hud.Defaults.Visible)) {
            for (var index = 0; (index < m_worldPanels.Length); index++) {
                if (m_facts.EvaluateAnySeat(predicate: m_worldSources[index].Visible)) {
                    m_visiblePanels[visibleCount++] = m_worldPanels[index];
                }
            }
        }

        var seatCount = BuildSeatPanels();

        m_store.Publish(frame: new OverlayHudFrame(
            Panels: m_visiblePanels.AsMemory(
                length: visibleCount,
                start: 0
            ),
            SeatPanels: m_seatPanels.AsMemory(
                length: seatCount,
                start: 0
            )
        ));
    }
}
