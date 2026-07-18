using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The World-side feed behind the unified overlay's binding-bar source: once per produced frame (the node's
/// <c>FeedTick</c>, render thread) it walks the joined roster seats, resolves each seat's ACTIVE
/// <see cref="Puck.Commands.BindingPageView"/> through <see cref="WorldSeatBindings"/>, joins it against the twelve
/// physical layout slots for the connected controller family — chips lit from the router's carried held state
/// (<see cref="InputRouter.IsCommandHeld"/>) — scopes it into that seat's split-screen viewport rect
/// (<see cref="WorldFrameSource.LayoutRegion(int, int, int)"/>, editor layout policy included), and publishes one
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

    // One seat's cached HUD lines + the fact key they were formatted from (re-formatted only on a key change).
    private struct HudCache {
        public (int Targeting, int Drag, int Definition, bool SnapEnabled, float SnapPitch) Key;
        public string SelectionLine;
        public string ContextLine;
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
    private readonly GamepadManager? m_gamepads;
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
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, WorldClient client, WorldEditorSession editor, WorldEditorTargeting targeting, WorldEditorDrag drag, InputRouter router, BindingBarStore store, EditorHudStore editorHudStore, GamepadManager? gamepads) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: drag);
        ArgumentNullException.ThrowIfNull(argument: editor);
        ArgumentNullException.ThrowIfNull(argument: editorHudStore);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: targeting);

        m_bindings = bindings;
        m_client = client;
        m_drag = drag;
        m_editor = editor;
        m_editorHudStore = editorHudStore;
        m_gamepads = gamepads;
        m_roster = roster;
        m_store = store;
        m_targeting = targeting;
        m_editorSeats = new OverlayEditorSeat[PlayerRoster.MaxSlots];
        m_hudCaches = new HudCache[PlayerRoster.MaxSlots];
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];
        m_pressedBySlot = new Func<string, bool>[PlayerRoster.MaxSlots];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            var slot = index;

            m_hudCaches[index] = new HudCache {
                Key = (-1, -1, -1, false, 0f),
                SelectionLine = string.Empty,
                ContextLine = string.Empty,
                DragLine = string.Empty,
            };
            m_slots[index] = new OverlayBindingSlot[BindingBarSeatComposer.SlotSources.Length];
            m_modifiers[index] = new OverlayBindingModifier[ModifierCapacity];
            m_pressedBySlot[index] = command => router.IsCommandHeld(slot: slot, command: command);
        }
    }

    /// <summary>Recomposes and publishes this frame's per-seat binding frame and editor-HUD frame (the overlay's
    /// <c>FeedTick</c>).</summary>
    public void Tick() {
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
            var viewport = WorldFrameSource.LayoutRegion(count: joined, index: viewIndex, soleEditorIndex: soleEditorViewIndex);

            m_seats[viewIndex] = new OverlayBindingSeat(
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

    // Re-format a seat's HUD lines only when the fact key moved: a drag step or a selection/definition change is a
    // rebuild-scale event anyway, so the transient string cost rides an already-paid frame.
    private void RefreshHudCache(int slot, ref HudCache cache) {
        var snap = m_drag.Snap(slot: slot);
        var key = (m_targeting.Revision, m_drag.Revision, m_client.DefinitionRevision, snap.Enabled, snap.Pitch.X);

        if (cache.Key == key) {
            return;
        }

        cache.Key = key;

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
            // ASCII only — the overlay glyph pack is ASCII-95 (a non-ASCII rune renders as the blank cell).
            handler: $"targets {m_targeting.TargetCount} | snap {(snap.Enabled ? "on" : "off")} {snap.Pitch.X:0.##}"
        );
        cache.DragLine = (m_drag.Describe(slot: slot) ?? string.Empty);
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
