using System.Diagnostics;
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
/// player-scope panel is confined to). A <c>Frame</c> element's ranked source candidates are keyed once at structure
/// build and ranked every produced frame: the first candidate whose condition holds wins, and a change of winner
/// cross-fades on the presentation clock for the element's <c>fadeSeconds</c>, both keys staying active until the
/// fade completes. Fade state lives per (panel, element, seat) inside the build, so a steady frame allocates nothing.
/// </summary>
internal sealed class WorldHudFeed(WorldClient client, PlayerRoster roster, HudStore store, WorldOverlayFacts facts, WorldOverlayFrameSources frameSources, WorldSeatViewports viewports) {
    private readonly WorldClient m_client = client;
    private readonly WorldOverlayFacts m_facts = facts;
    private readonly WorldOverlayFrameSources m_frameSources = frameSources;
    private readonly PlayerRoster m_roster = roster;
    private readonly HudStore m_store = store;
    private readonly WorldSeatViewports m_viewports = viewports;
    private readonly OverlayHudSeatPanel[] m_seatPanels = new OverlayHudSeatPanel[PlayerRoster.MaxSlots];
    // Per-seat structure memo: the document row a seat's panel was last built FROM, and the build. An identity
    // edit publishes a new WorldHudPanel instance, so reference identity is an exact staleness test — the cheap
    // revision check world scope has, expressed at the only grain seat scope offers.
    private readonly WorldHudPanel?[] m_seatSources = new WorldHudPanel?[PlayerRoster.MaxSlots];
    private readonly PanelBuild?[] m_seatBuilds = new PanelBuild?[PlayerRoster.MaxSlots];
    private int m_seenRevision = -1;
    private WorldHudSection? m_seenHud;
    private PanelBuild[] m_worldPanels = [];
    private OverlayHudPanel[] m_visiblePanels = [];
    private WorldHudPanel[] m_worldSources = [];

    // Every Frame candidate's source (present only for that kind, enforced at validation) resolves to a key through
    // the process's one WorldOverlayFrameSources at build; a non-Frame element carries no candidate state and -1 for
    // "no source to bind". seat is the enclosing seat scope a bare (Seat-less) camera source falls back to: the
    // owning identity panel's slot+1 for a player-scope panel, or 1 for a world-scope panel.
    private PanelBuild BuildPanel(WorldHudPanel panel, int seat, int slot) {
        var elements = panel.Elements;
        var built = new OverlayHudElement[elements.Count];
        var frames = new FrameElementState?[elements.Count];

        for (var index = 0; (index < elements.Count); index++) {
            var element = elements[index];

            built[index] = new OverlayHudElement(
                Kind: ToKind(kind: element.Kind),
                Rect: ToOverlayRect(rect: element.Rect),
                Role: ToRole(token: element.Style),
                Text: element.Text,
                Binding: element.Binding,
                Template: BuildTemplate(template: element.Template),
                FrameSource: -1,
                Fit: ToFit(fit: element.Fit),
                Mirror: element.Mirror,
                Radius: element.Radius,
                Opacity: element.Opacity
            );

            if (element.Kind != WorldHudElementKind.Frame) {
                continue;
            }

            var candidates = element.FrameCandidates;

            if (candidates.Count == 0) {
                continue;
            }

            var keys = new int[candidates.Count];
            var whens = new OverlayPredicate?[candidates.Count];

            for (var candidate = 0; (candidate < candidates.Count); candidate++) {
                keys[candidate] = m_frameSources.KeyFor(
                    seat: seat,
                    source: candidates[candidate].Source
                );
                whens[candidate] = candidates[candidate].When;
            }

            frames[index] = new FrameElementState(
                fadeSeconds: element.FadeSeconds,
                keys: keys,
                whens: whens
            );
        }

        return new PanelBuild(
            elements: built,
            frames: frames,
            panel: new OverlayHudPanel(
                Id: panel.Id,
                Rect: ToOverlayRect(rect: panel.Rect),
                Band: ToBand(layer: panel.Layer),
                Style: ToStyle(style: panel.Style),
                Elements: built
            ),
            slot: slot
        );
    }
    private PanelBuild[] BuildPanels(IReadOnlyList<WorldHudPanel> panels) {
        var built = new PanelBuild[panels.Count];

        for (var index = 0; (index < panels.Count); index++) {
            built[index] = BuildPanel(
                panel: panels[index],
                seat: 1,
                slot: -1
            );
        }

        return built;
    }
    private static double NowSeconds() => (((double)Stopwatch.GetTimestamp()) / Stopwatch.Frequency);
    // Ranks every Frame element of a visible build for this frame: the first candidate whose condition holds for
    // the build's scope wins, the element's cross-fade advances on the presentation clock, and the winner (plus the
    // outgoing key while a fade runs) is marked into the generation so the binder keeps both producers alive.
    private void RankFrames(PanelBuild build, double nowSeconds) {
        var frames = build.Frames;

        for (var index = 0; (index < frames.Length); index++) {
            if (frames[index] is not { } frame) {
                continue;
            }

            var winner = OverlayRanking.FirstHolding(
                candidates: frame.Whens,
                evaluator: m_facts,
                slot: build.Slot,
                when: static when => when
            );

            frame.Crossfade.Advance(
                fadeSeconds: frame.FadeSeconds,
                nowSeconds: nowSeconds,
                winner: ((winner >= 0) ? frame.Keys[winner] : -1)
            );

            var current = frame.Crossfade.Current;
            var outgoing = frame.Crossfade.Outgoing;

            build.Elements[index] = (build.Elements[index] with {
                FrameSource = current,
                FrameSourceB = outgoing,
                FrameMix = frame.Crossfade.Mix,
            });

            if (current >= 0) {
                m_frameSources.MarkActive(key: current);
            }

            if (outgoing >= 0) {
                m_frameSources.MarkActive(key: outgoing);
            }
        }
    }
    private void ReleaseFrameSources(PanelBuild build) {
        foreach (var frame in build.Frames) {
            if (frame is null) {
                continue;
            }

            foreach (var key in frame.Keys) {
                m_frameSources.ReleaseStructureKey(key: key);
            }
        }
    }
    private void ReleaseSeatBuild(int slot) {
        if (m_seatBuilds[slot] is not { } build) {
            return;
        }

        ReleaseFrameSources(build: build);
        m_seatSources[slot] = null;
        m_seatBuilds[slot] = null;
    }
    // Walks the joined roster (publishing one entry per seat that is BOTH joined and has authored a player-scope
    // panel), scoping each seat's panel into the SAME published viewport WorldOverlayFeed's binding bar renders in
    // — the authored-layout-aware rect WorldFramePresenter resolved for this seat this frame, not the builtin
    // ladder. Reuses the preallocated array, and rebuilds a seat's panel only when its document row is a different
    // instance — zero steady-state allocation.
    private int BuildSeatPanels(double nowSeconds) {
        var count = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                ReleaseSeatBuild(slot: slot);

                continue;
            }

            var panel = m_roster.ProfileAt(slot: slot)?.Hud;

            if (panel is null) {
                ReleaseSeatBuild(slot: slot);

                continue;
            }

            var viewport = m_viewports.Seat(slot: slot).Region;

            if (!ReferenceEquals(
                objA: m_seatSources[slot],
                objB: panel
            )) {
                ReleaseSeatBuild(slot: slot);
                m_seatSources[slot] = panel;
                m_seatBuilds[slot] = BuildPanel(
                    panel: panel,
                    seat: PlayerRoster.DisplayNumber(slot: slot),
                    slot: slot
                );
            }
            var presence = m_facts.Presence(predicate: panel.Visible, slot: slot);

            if (presence <= 0f) {
                continue;
            }

            var build = m_seatBuilds[slot]!;

            RankFrames(
                build: build,
                nowSeconds: nowSeconds
            );
            build.Panel = (build.Panel with { Alpha = presence, });
            m_seatPanels[count++] = new OverlayHudSeatPanel(
                Viewport: new OverlayHudRect(
                    X: viewport.X,
                    Y: viewport.Y,
                    Width: viewport.Width,
                    Height: viewport.Height
                ),
                Panel: build.Panel
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
        m_frameSources.BeginGeneration();

        try {
            var revision = m_client.DefinitionRevision;
            var nowSeconds = NowSeconds();

            var hud = m_client.Definition.Hud;

            // A revision moves on every mutation — a state cell write included — but the HUD section is the same
            // instance unless a HUD row changed; only then is the structure rebuilt. The new builds take their keys
            // before the old release theirs, so a source both name never drops to zero references in between (which
            // would tear its producer down and rebuild it a frame later).
            if ((revision != m_seenRevision) && !ReferenceEquals(objA: hud, objB: m_seenHud)) {
                m_seenHud = hud;

                var previous = m_worldPanels;

                m_worldSources = (hud.Defaults.Enabled
                    ? [.. hud.Panels]
                    : []
                );
                m_worldPanels = BuildPanels(panels: m_worldSources);
                m_visiblePanels = new OverlayHudPanel[m_worldPanels.Length];

                foreach (var build in previous) {
                    ReleaseFrameSources(build: build);
                }
            }

            m_seenRevision = revision;

            // The world-scope gate (hud.defaults.visible) then each panel's own; both evaluated across every joined seat.
            // Only panels that survive both gates mark their Frame sources into this generation: structural caches keep
            // stable keys, while the binder's producer ownership follows what can actually draw this frame.
            var visibleCount = 0;

            // Presence, not a boolean: a fading predicate eases the panel out. World scope takes the strongest
            // presence over the joined seats, the same quantifier EvaluateAnySeat applies.
            var defaultsPresence = PresenceAnySeat(predicate: hud.Defaults.Visible);

            if (defaultsPresence > 0f) {
                for (var index = 0; (index < m_worldPanels.Length); index++) {
                    var presence = (defaultsPresence * PresenceAnySeat(predicate: m_worldSources[index].Visible));

                    if (presence <= 0f) {
                        continue;
                    }

                    var build = m_worldPanels[index];

                    RankFrames(
                        build: build,
                        nowSeconds: nowSeconds
                    );
                    build.Panel = (build.Panel with { Alpha = presence, });
                    m_visiblePanels[visibleCount++] = build.Panel;
                }
            }

            var seatCount = BuildSeatPanels(nowSeconds: nowSeconds);

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
        } finally {
            m_frameSources.EndGeneration();
        }
    }

    // One Frame element's ranked candidates: the key each source resolved to, its condition, the authored fade, and
    // the element's live cross-fade. Per build, so per (panel, element, seat).
    private sealed class FrameElementState(int[] keys, OverlayPredicate?[] whens, float fadeSeconds) {
        public OverlayFrameCrossfade Crossfade;

        public float FadeSeconds { get; } = fadeSeconds;
        public int[] Keys { get; } = keys;
        public OverlayPredicate?[] Whens { get; } = whens;
    }
    // One built panel: the published snapshot, the element array it wraps (rewritten in place as winners and fades
    // move), the per-element frame state, and the scope it ranks in (a 0-based seat, or -1 for the world scope).
    private sealed class PanelBuild(OverlayHudPanel panel, OverlayHudElement[] elements, FrameElementState?[] frames, int slot) {
        public OverlayHudElement[] Elements { get; } = elements;
        public FrameElementState?[] Frames { get; } = frames;
        public OverlayHudPanel Panel { get; set; } = panel;
        public int Slot { get; } = slot;
    }
    private float PresenceAnySeat(OverlayPredicate? predicate) {
        if (predicate is null) {
            return 1f;
        }

        var strongest = 0f;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_roster.IsJoined(slot: slot)) {
                strongest = MathF.Max(x: strongest, y: m_facts.Presence(predicate: predicate, slot: slot));
            }
        }

        return strongest;
    }
}
