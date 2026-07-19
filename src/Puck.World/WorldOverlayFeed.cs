using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;
using Puck.Overlays;
using Puck.World.Client;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>The class of the last edit act the editor HUD tags (the D7 asymmetry, presented): a LIVE session lever
/// (applies now, <c>world.save</c> folds it), an ordinary DOCUMENT mutation (applies live on delivery), or a
/// DOCUMENT-DEFAULTS mutation (next boot; live levers unchanged).</summary>
internal enum EditorActClass {
    None,
    Live,
    Document,
    DocumentDefault,
}

/// <summary>
/// The World-side feed behind the unified overlay's binding-bar source: once per produced frame (the node's
/// <c>FeedTick</c>, render thread) it walks the joined roster seats, resolves each seat's ACTIVE
/// <see cref="Puck.Commands.BindingPageView"/> through <see cref="WorldSeatBindings"/>, joins it against the twelve
/// physical layout slots for the connected controller family — chips lit from the router's carried held state
/// (<see cref="InputRouter.IsCommandHeld"/>) — scopes it into that seat's split-screen viewport rect
/// (<see cref="WorldFrameSource.LayoutRegion(int, int, int, float)"/>, editor layout policy included), and publishes one
/// <see cref="OverlayBindingBarFrame"/> — plus, for every EDITING seat, one <see cref="OverlayEditorSeat"/> HUD entry
/// (selection readout, candidate/snap context, live drag line). Zero steady-state allocation: the per-seat arrays and
/// pressed probes are preallocated and reused, and the HUD lines are re-formatted only when their source facts move
/// (a selection change, a drag step, a definition delivery) — safe because the feed and the overlay reader run on the
/// SAME thread, sequentially (see <see cref="BindingBarStore"/>'s remarks).
/// </summary>
internal sealed class WorldOverlayFeed {
    // Secondary-seat bars render at partial opacity so the split-screen HUD stays quiet.
    private const float MultiSeatBarAlpha = 0.5f;
    private const int ModifierCapacity = 8;
    // Session-honesty sweep cadence in published frames (~4 Hz at 120 FPS): the drift hint and exclusive-hold labels
    // recompute at human cadence, never per frame — DescribeDrift is a verb-scale composition, not a tick fact.
    private const int SessionSweepFrames = 32;

    // One seat's cached HUD lines + the fact key they were formatted from (re-formatted only on a key change).
    private struct HudCache {
        public (int Targeting, int Drag, int Definition, int Session, int Workbench, bool SnapEnabled, float SnapPitch) Key;
        public string SelectionLine;
        public string ContextLine;
        public string SessionLine;
        public string DragLine;
    }

    private readonly WorldSeatBindings m_bindings;
    private readonly WorldClient m_client;
    private readonly WorldEditorDrag m_drag;
    private readonly WorldEditorSession m_editor;
    private readonly EditorHudStore m_editorHudStore;
    private readonly OverlayEditorSeat[] m_editorSeats;
    private readonly HudCache[] m_hudCaches;
    private readonly WorldEditorTargeting m_targeting;
    private readonly WorldWorkbench m_workbench;
    private readonly GamepadManager? m_gamepads;
    // The session-honesty sources (loopback-local reads, the DescribePopulation precedent): the live render levers
    // (their Revision is the live-act watch), the live census, the screen binder's runtime inserts, and the server's
    // grant table for exclusive-hold readouts.
    private readonly WorldRenderSettings m_settings;
    private readonly WorldPopulation m_population;
    private readonly WorldScreenBinder m_binder;
    private readonly WorldServer m_server;
    private readonly WorldAudioDirector m_audio;
    // The last edit act's class (fed by the EchoTap wiring and the settings-revision watch), the cached drift hint,
    // the per-seat exclusive-hold labels, and the generation counter the HUD caches key on — all bumped at human
    // cadence only.
    private readonly string[] m_exclusiveLabels;
    private readonly WorldPrincipal?[] m_exclusiveHolders;
    private EditorActClass m_lastActClass;
    private string m_driftHint = "none";
    private int m_sessionGeneration;
    private int m_settingsRevisionSeen;
    private int m_sessionSweepCountdown;
    // Whether a mutation note arrived since the last sweep — a drift change it caused must not be re-tagged Live.
    private bool m_mutationNoted;
    // Per-SEAT chord-hint cache: the hint lines are formatted once per published view (views are immutable and
    // reference-stable per page), so the per-frame publish is a reference handoff.
    private readonly BindingPageView?[] m_hintViews;
    private readonly string[][] m_hintLines;
    private readonly OverlayBindingModifier[][] m_modifiers;
    // One cached pressed-probe delegate per SEAT SLOT (the router's held state is slot-keyed), so the per-frame
    // compose closes over nothing.
    private readonly Func<string, bool>[] m_pressedBySlot;
    private readonly PlayerRoster m_roster;
    private readonly OverlayBindingSeat[] m_seats;
    private readonly OverlayBindingSlot[][] m_slots;
    private readonly BindingBarStore m_store;

    /// <summary>Initializes a new instance of the <see cref="WorldOverlayFeed"/> class.</summary>
    /// <param name="roster">The participant roster (which seats are joined).</param>
    /// <param name="bindings">The per-seat binding resolver (each seat's active page view).</param>
    /// <param name="client">The client view (its definition revision keys the HUD re-format).</param>
    /// <param name="editor">The per-seat editor mode (the sole-editor layout policy + editing liveness).</param>
    /// <param name="targeting">The editor selection state the HUD narrates.</param>
    /// <param name="drag">The editor drag channel the HUD narrates.</param>
    /// <param name="router">The input router whose carried held state lights the pressed chips.</param>
    /// <param name="store">The binding-bar store the overlay reads.</param>
    /// <param name="editorHudStore">The editor-HUD store the overlay reads.</param>
    /// <param name="settings">The live render levers (their revision drives the live-act tag + drift refresh).</param>
    /// <param name="population">The live census (a drift dimension).</param>
    /// <param name="binder">The screen binder (runtime inserts are a drift dimension).</param>
    /// <param name="server">The server whose grant table answers exclusive-hold readouts (loopback-local).</param>
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <param name="workbench">The sculpt workbench whose bench facts the HUD narrates while a seat sculpts (§P6).</param>
    /// <param name="audio">The audio director (the master-volume lever — a drift dimension).</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, WorldClient client, WorldEditorSession editor, WorldEditorTargeting targeting, WorldEditorDrag drag, InputRouter router, BindingBarStore store, EditorHudStore editorHudStore, WorldRenderSettings settings, WorldPopulation population, WorldScreenBinder binder, WorldServer server, GamepadManager? gamepads, WorldWorkbench workbench, WorldAudioDirector audio) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: binder);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: drag);
        ArgumentNullException.ThrowIfNull(argument: editor);
        ArgumentNullException.ThrowIfNull(argument: editorHudStore);
        ArgumentNullException.ThrowIfNull(argument: population);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: server);
        ArgumentNullException.ThrowIfNull(argument: settings);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: targeting);
        ArgumentNullException.ThrowIfNull(argument: workbench);
        ArgumentNullException.ThrowIfNull(argument: audio);

        m_audio = audio;
        m_workbench = workbench;
        m_bindings = bindings;
        m_binder = binder;
        m_client = client;
        m_drag = drag;
        m_editor = editor;
        m_editorHudStore = editorHudStore;
        m_gamepads = gamepads;
        m_population = population;
        m_roster = roster;
        m_server = server;
        m_settings = settings;
        m_settingsRevisionSeen = settings.Revision;
        m_store = store;
        m_targeting = targeting;
        m_exclusiveLabels = new string[PlayerRoster.MaxSlots];
        m_exclusiveHolders = new WorldPrincipal?[PlayerRoster.MaxSlots];
        Array.Fill(array: m_exclusiveLabels, value: string.Empty);
        m_editorSeats = new OverlayEditorSeat[PlayerRoster.MaxSlots];
        m_hintLines = new string[PlayerRoster.MaxSlots][];
        m_hintViews = new BindingPageView?[PlayerRoster.MaxSlots];
        m_hudCaches = new HudCache[PlayerRoster.MaxSlots];
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];
        m_pressedBySlot = new Func<string, bool>[PlayerRoster.MaxSlots];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            var slot = index;

            m_hudCaches[index] = new HudCache {
                Key = (-1, -1, -1, -1, -1, false, 0f),
                SelectionLine = string.Empty,
                ContextLine = string.Empty,
                SessionLine = string.Empty,
                DragLine = string.Empty,
            };
            m_hintLines[index] = [];
            m_slots[index] = new OverlayBindingSlot[BindingBarSeatComposer.SlotSources.Length];
            m_modifiers[index] = new OverlayBindingModifier[ModifierCapacity];
            m_pressedBySlot[index] = command => router.IsCommandHeld(slot: slot, command: command);
        }
    }

    /// <summary>Notes an applied edit-boundary outcome (the server's <see cref="WorldServer.EchoTap"/> wiring calls
    /// this beside the toast publish) so the HUD's act-class tag reflects the last act honestly. Runs on the same
    /// window-pump thread as <see cref="Tick"/>.</summary>
    /// <param name="documentOnly">Whether the applied edit was DOCUMENT-DEFAULTS class (next boot) rather than live.</param>
    public void NoteMutationApplied(bool documentOnly) {
        m_lastActClass = (documentOnly ? EditorActClass.DocumentDefault : EditorActClass.Document);
        m_mutationNoted = true;
        m_sessionGeneration++;
        // A mutation can move the drift baseline (a defaults edit redefines what the levers drift FROM), so the next
        // published frame refreshes the hint.
        m_sessionSweepCountdown = 0;
    }

    /// <summary>Recomposes and publishes this frame's per-seat binding frame and editor-HUD frame (the overlay's
    /// <c>FeedTick</c>).</summary>
    public void Tick() {
        // A render-lever write since the last frame is a LIVE session act (world.shadows/.ao/.render-scale/…): tag it
        // and refresh the drift hint on this frame. Human cadence — the levers are console verbs.
        if (m_settings.Revision != m_settingsRevisionSeen) {
            m_settingsRevisionSeen = m_settings.Revision;
            m_lastActClass = EditorActClass.Live;
            m_sessionGeneration++;
            m_sessionSweepCountdown = 0;
        }

        RefreshSessionFacts();

        var joined = m_roster.Count;
        var family = ResolveFamily();
        var barAlpha = ((joined > 1) ? MultiSeatBarAlpha : 1f);
        // The SAME layout resolution the frame source renders with, so each bar lands in its seat's actual rect.
        var soleEditorViewIndex = m_editor.SoleEditorViewIndex();
        var viewIndex = 0;
        var editorCount = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            var view = m_bindings.PageView(slot: slot);

            BindingBarSeatComposer.ComposeSlots(barAlpha: barAlpha, destination: m_slots[viewIndex], family: family, isPressed: m_pressedBySlot[slot], view: view);

            var modifierCount = BindingBarSeatComposer.ComposeModifiers(destination: m_modifiers[viewIndex], view: view);
            var viewport = WorldFrameSource.LayoutRegion(count: joined, index: viewIndex, soleEditorIndex: soleEditorViewIndex, workbenchFraction: m_client.Definition.Authoring.WorkbenchFraction);

            m_seats[viewIndex] = new OverlayBindingSeat(
                Group: view.Group,
                Hints: HintLinesFor(slot: slot, view: view),
                Modifiers: m_modifiers[viewIndex].AsMemory(start: 0, length: modifierCount),
                PageId: view.PageId,
                Slots: m_slots[viewIndex],
                Viewport: viewport
            );

            if (m_editor.IsEditing(slot: slot)) {
                ref var cache = ref m_hudCaches[slot];

                RefreshHudCache(slot: slot, cache: ref cache);
                m_editorSeats[editorCount++] = new OverlayEditorSeat(
                    Viewport: viewport,
                    SelectionLine: cache.SelectionLine,
                    ContextLine: cache.ContextLine,
                    SessionLine: cache.SessionLine,
                    DragLine: cache.DragLine,
                    DragActive: m_drag.IsDragging(slot: slot)
                );
            }

            viewIndex++;
        }

        m_store.Publish(frame: new OverlayBindingBarFrame(
            Family: family,
            Seats: m_seats.AsMemory(start: 0, length: viewIndex)
        ));
        m_editorHudStore.Publish(frame: new OverlayEditorHudFrame(
            Seats: m_editorSeats.AsMemory(start: 0, length: editorCount)
        ));
    }

    // The human-cadence session-honesty sweep: refreshes the drift hint and per-seat exclusive-hold labels every
    // SessionSweepFrames published frames WHILE a seat edits (the HUD is hidden otherwise, so an idle session pays
    // nothing), plus immediately after an explicit trigger (a lever write, a mutation note). DescribeDrift's small
    // transient allocation rides this divided cadence, never the frame path.
    private void RefreshSessionFacts() {
        var anyEditing = false;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (m_editor.IsEditing(slot: slot)) {
                anyEditing = true;

                break;
            }
        }

        if (!anyEditing) {
            return;
        }

        if (m_sessionSweepCountdown > 0) {
            m_sessionSweepCountdown--;

            return;
        }

        m_sessionSweepCountdown = SessionSweepFrames;

        var drift = WorldSessionCapture.DescribeDrift(definition: m_client.Definition, render: m_settings, population: m_population, binder: m_binder, audio: m_audio);

        if (!string.Equals(a: drift, b: m_driftHint, comparisonType: StringComparison.Ordinal)) {
            // Drift moved without a lever write or a mutation note in the window: the remaining producers are the live
            // census / screen-insert levers (or an undo moving the baseline) — session-class either way.
            if (!m_mutationNoted && (m_lastActClass != EditorActClass.Live)) {
                m_lastActClass = EditorActClass.Live;
            }

            m_driftHint = drift;
            m_sessionGeneration++;
        }

        m_mutationNoted = false;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            WorldPrincipal? holder = null;

            if (m_editor.IsEditing(slot: slot) && (m_targeting.Selected(slot: slot) is { } selection)) {
                holder = m_server.Grants.ExclusiveHolder(capability: WorldCapability.Mutate, subject: GrantSubject.Section(section: selection.Section));
            }

            if (m_exclusiveHolders[slot] != holder) {
                m_exclusiveHolders[slot] = holder;
                m_exclusiveLabels[slot] = (holder?.Describe() ?? string.Empty);
                m_sessionGeneration++;
            }
        }
    }

    // Re-format a seat's HUD lines only when the fact key moved: a drag step or a selection/definition change is a
    // rebuild-scale event anyway, so the transient string cost rides an already-paid frame.
    private void RefreshHudCache(int slot, ref HudCache cache) {
        var snap = m_drag.Snap(slot: slot);
        var key = (m_targeting.Revision, m_drag.Revision, m_client.DefinitionRevision, m_sessionGeneration, m_workbench.Revision, snap.Enabled, snap.Pitch.X);

        if (cache.Key == key) {
            return;
        }

        cache.Key = key;

        // A sculpting seat's HUD narrates the BENCH (its target, budget, timeline, and the two undo domains) —
        // the world-row selection lines return when the bench closes.
        if (m_workbench.Model(slot: slot) is { } sculpt) {
            cache.SelectionLine = ComposeSculptTargetLine(model: sculpt);
            cache.ContextLine = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                // ASCII only — the overlay glyph pack is ASCII-95. The shape budget is the §P6 HUD narration.
                handler: $"'{m_workbench.RowId(slot: slot)}' shapes {sculpt.StampShapeCount}/{sculpt.ShapeCapacity} | frame {sculpt.CurrentFrame}/{sculpt.FrameCount}{(sculpt.Playing ? " play" : string.Empty)} | chains {sculpt.Chains.Count}"
            );
            cache.SessionLine = ComposeSessionLine(slot: slot);
            cache.DragLine = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"ring {sculpt.HistoryCount}/{Puck.Authoring.SculptModel.HistoryCapacity} local | uncommitted {m_workbench.UncommittedEdits(slot: slot)} | world.undo = journal"
            );

            return;
        }

        if (m_targeting.Selected(slot: slot) is { } selection) {
            var position = (m_targeting.SelectionPosition(slot: slot) ?? default);

            cache.SelectionLine = string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"sel {selection.Describe()} ({position.X:0.0}, {position.Y:0.0}, {position.Z:0.0})"
            );
        } else {
            cache.SelectionLine = "sel none - RT pick/cycle";
        }

        cache.ContextLine = string.Create(
            provider: System.Globalization.CultureInfo.InvariantCulture,
            // ASCII only — the overlay glyph pack is ASCII-95 (a non-ASCII rune renders as the blank cell). "rows" is
            // the definition's total selectable-row count (revision-keyed, so the cache stays honest); the live
            // in-radius candidate ring is a camera-relative fact editor.status narrates on demand.
            handler: $"rows {m_targeting.TargetCount} | snap {(snap.Enabled ? "on" : "off")} {snap.Pitch.X:0.##}"
        );
        cache.SessionLine = ComposeSessionLine(slot: slot);
        cache.DragLine = (m_drag.Describe(slot: slot) ?? string.Empty);
    }

    // The sculpting seat's target line: the model's live edit target (shape / chain goal / brush).
    private static string ComposeSculptTargetLine(Puck.Authoring.SculptModel model) {
        if (model.TargetIsGoal) {
            var chain = model.TargetGoalChain!;

            return string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"goal chain {chain.Id} ({chain.Goal.X:0.0}, {chain.Goal.Y:0.0}, {chain.Goal.Z:0.0})"
            );
        }

        if (model.SelectedShape is { } shape) {
            return string.Create(
                provider: System.Globalization.CultureInfo.InvariantCulture,
                handler: $"shape {shape.Id} {shape.Type} ({shape.Position.X:0.0}, {shape.Position.Y:0.0}, {shape.Position.Z:0.0})"
            );
        }

        return "brush - South adds, D-pad cycles";
    }

    // The session-honesty line: the last act's class ("live" applies now and folds at world.save; "doc" applied live
    // on delivery; "defaults" is next-boot only — the D7 asymmetry made visible), the world.status session-drift hint
    // while drift exists, and the exclusive holder of the selection's section. Empty when nothing needs saying.
    private string ComposeSessionLine(int slot) {
        var act = m_lastActClass switch {
            EditorActClass.Live => "live (save folds)",
            EditorActClass.Document => "doc",
            EditorActClass.DocumentDefault => "defaults (next boot)",
            _ => null,
        };
        var drift = ((m_driftHint.Length > 0) && !string.Equals(a: m_driftHint, b: "none", comparisonType: StringComparison.Ordinal) ? m_driftHint : null);
        var exclusive = ((m_exclusiveLabels[slot].Length > 0) ? m_exclusiveLabels[slot] : null);

        if ((act is null) && (drift is null) && (exclusive is null)) {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();

        if (act is not null) {
            _ = builder.Append(value: "act ").Append(value: act);
        }

        if (drift is not null) {
            _ = builder.Append(value: (builder.Length > 0) ? " | " : string.Empty).Append(value: "drift ").Append(value: drift);
        }

        if (exclusive is not null) {
            _ = builder.Append(value: (builder.Length > 0) ? " | " : string.Empty).Append(value: "excl ").Append(value: exclusive);
        }

        return builder.ToString();
    }

    // The seat's chord-hint lines, re-formatted only when its published view changes (a page/group flip or a
    // recompose — human cadence, never per frame). One ASCII line per command-chord row of the active group:
    // the chord's modifier labels joined by '+', then the row's label (or its command name).
    private ReadOnlyMemory<string> HintLinesFor(int slot, BindingPageView view) {
        if (ReferenceEquals(objA: m_hintViews[slot], objB: view)) {
            return m_hintLines[slot];
        }

        var chords = view.CommandChords;
        var lines = ((chords.Count == 0) ? [] : new string[chords.Count]);

        for (var index = 0; (index < chords.Count); index++) {
            var chord = chords[index];
            var members = new string[chord.Chord.Count];

            for (var memberIndex = 0; (memberIndex < members.Length); memberIndex++) {
                var id = chord.Chord[memberIndex];

                members[memberIndex] = (ModifierLabelFor(view: view, id: id) ?? id.ToUpperInvariant());
            }

            lines[index] = $"{string.Join(separator: '+', values: members)} {chord.Label ?? chord.Command}";
        }

        m_hintLines[slot] = lines;
        m_hintViews[slot] = view;

        return lines;
    }

    private static string? ModifierLabelFor(BindingPageView view, string id) {
        foreach (var modifier in view.Modifiers) {
            if (string.Equals(a: modifier.Id, b: id, comparisonType: StringComparison.Ordinal)) {
                return modifier.Label;
            }
        }

        return null;
    }

    // The primary player's family drives the glyph theme; the first connected pad stands in until per-player
    // family routing exists.
    private GamepadType ResolveFamily() {
        if (m_gamepads is not { } gamepads) {
            return GamepadType.Unknown;
        }

        var devices = gamepads.ConnectedDevices();

        return (((devices.Count > 0) && gamepads.TryGetType(
            deviceId: devices[0],
            type: out var type
        ))
            ? type
            : GamepadType.Unknown);
    }
}
