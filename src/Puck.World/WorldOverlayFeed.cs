using Puck.Input;
using Puck.Input.Devices;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The World-side feed behind the unified overlay's binding-bar source: once per produced frame (the node's
/// <c>FeedTick</c>, render thread) it walks the joined roster seats, resolves each seat's ACTIVE
/// <see cref="Puck.Commands.BindingPageView"/> through <see cref="WorldSeatBindings"/>, joins it against the twelve
/// physical layout slots for the connected controller family, scopes it into that seat's split-screen viewport rect
/// (<see cref="WorldFrameSource.LayoutRegion"/>), and publishes one <see cref="OverlayBindingBarFrame"/>. Zero
/// steady-state allocation: the per-seat slot/modifier arrays are preallocated and reused — safe because the feed
/// and the overlay reader run on the SAME thread, sequentially (see <see cref="BindingBarStore"/>'s remarks).
/// </summary>
internal sealed class WorldOverlayFeed {
    // Secondary-seat bars render at partial opacity so the split-screen HUD stays quiet.
    private const float MultiSeatBarAlpha = 0.5f;
    private const int ModifierCapacity = 8;

    private readonly WorldSeatBindings m_bindings;
    private readonly GamepadManager? m_gamepads;
    private readonly OverlayBindingModifier[][] m_modifiers;
    private readonly PlayerRoster m_roster;
    private readonly OverlayBindingSeat[] m_seats;
    private readonly OverlayBindingSlot[][] m_slots;
    private readonly BindingBarStore m_store;

    /// <summary>Initializes a new instance of the <see cref="WorldOverlayFeed"/> class.</summary>
    /// <param name="roster">The participant roster (which seats are joined).</param>
    /// <param name="bindings">The per-seat binding resolver (each seat's active page view).</param>
    /// <param name="store">The binding-bar store the overlay reads.</param>
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, BindingBarStore store, GamepadManager? gamepads) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_bindings = bindings;
        m_gamepads = gamepads;
        m_roster = roster;
        m_store = store;
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            m_slots[index] = new OverlayBindingSlot[BindingBarSeatComposer.SlotSources.Length];
            m_modifiers[index] = new OverlayBindingModifier[ModifierCapacity];
        }
    }

    /// <summary>Recomposes and publishes this frame's per-seat binding frame (the overlay's <c>FeedTick</c>).</summary>
    public void Tick() {
        var joined = m_roster.Count;
        var family = ResolveFamily();
        var barAlpha = ((joined > 1) ? MultiSeatBarAlpha : 1f);
        var viewIndex = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            var view = m_bindings.PageView(slot: slot);

            BindingBarSeatComposer.ComposeSlots(barAlpha: barAlpha, destination: m_slots[viewIndex], family: family, isPressed: null, view: view);

            var modifierCount = BindingBarSeatComposer.ComposeModifiers(destination: m_modifiers[viewIndex], view: view);

            m_seats[viewIndex] = new OverlayBindingSeat(
                Modifiers: m_modifiers[viewIndex].AsMemory(start: 0, length: modifierCount),
                PageId: view.PageId,
                Slots: m_slots[viewIndex],
                Viewport: WorldFrameSource.LayoutRegion(count: joined, index: viewIndex)
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
