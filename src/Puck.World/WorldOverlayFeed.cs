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
/// <see cref="OverlayBindingBarFrame"/>. Zero steady-state allocation: the per-seat slot/modifier arrays and the
/// per-seat pressed probes are preallocated and reused — safe because the feed and the overlay reader run on the
/// SAME thread, sequentially (see <see cref="BindingBarStore"/>'s remarks).
/// </summary>
internal sealed class WorldOverlayFeed {
    // Secondary-seat bars render at partial opacity so the split-screen HUD stays quiet.
    private const float MultiSeatBarAlpha = 0.5f;
    private const int ModifierCapacity = 8;

    private readonly WorldSeatBindings m_bindings;
    private readonly WorldEditorSession m_editor;
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
    /// <param name="editor">The per-seat editor mode (the sole-editor layout policy).</param>
    /// <param name="router">The input router whose carried held state lights the pressed chips.</param>
    /// <param name="store">The binding-bar store the overlay reads.</param>
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, WorldEditorSession editor, InputRouter router, BindingBarStore store, GamepadManager? gamepads) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: editor);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_bindings = bindings;
        m_editor = editor;
        m_gamepads = gamepads;
        m_roster = roster;
        m_store = store;
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];
        m_pressedBySlot = new Func<string, bool>[PlayerRoster.MaxSlots];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            var slot = index;

            m_slots[index] = new OverlayBindingSlot[BindingBarSeatComposer.SlotSources.Length];
            m_modifiers[index] = new OverlayBindingModifier[ModifierCapacity];
            m_pressedBySlot[index] = command => router.IsCommandHeld(slot: slot, command: command);
        }
    }

    /// <summary>Recomposes and publishes this frame's per-seat binding frame (the overlay's <c>FeedTick</c>).</summary>
    public void Tick() {
        var joined = m_roster.Count;
        var family = ResolveFamily();
        var barAlpha = ((joined > 1) ? MultiSeatBarAlpha : 1f);
        // The SAME layout resolution the frame source renders with, so each bar lands in its seat's actual rect.
        var soleEditorViewIndex = m_editor.SoleEditorViewIndex();
        var viewIndex = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            var view = m_bindings.PageView(slot: slot);

            BindingBarSeatComposer.ComposeSlots(barAlpha: barAlpha, destination: m_slots[viewIndex], family: family, isPressed: m_pressedBySlot[slot], view: view);

            var modifierCount = BindingBarSeatComposer.ComposeModifiers(destination: m_modifiers[viewIndex], view: view);

            m_seats[viewIndex] = new OverlayBindingSeat(
                Modifiers: m_modifiers[viewIndex].AsMemory(start: 0, length: modifierCount),
                PageId: view.PageId,
                Slots: m_slots[viewIndex],
                Viewport: WorldFrameSource.LayoutRegion(count: joined, index: viewIndex, soleEditorIndex: soleEditorViewIndex)
            );
            viewIndex++;
        }

        m_store.Publish(frame: new OverlayBindingBarFrame(
            Family: family,
            Seats: m_seats.AsMemory(start: 0, length: viewIndex)
        ));
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
