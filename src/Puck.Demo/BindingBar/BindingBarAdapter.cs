using Puck.Commands;
using Puck.Demo.Overworld;
using Puck.Input;
using Puck.Input.Devices;

namespace Puck.Demo.BindingBar;

/// <summary>
/// The binding-bar writer for the live overworld bare-room path: once per frame it reads the paged resolver's active
/// <see cref="BindingPageView"/> (slot 0 — the primary local player), joins it against the layout's twelve
/// physical slots, marks pressed slots from the frame's <see cref="CommandSnapshot"/>, resolves glyphs for the
/// connected controller family, and publishes one immutable <see cref="BindingBarFrame"/> to the store the
/// overlay renders from. Pure CPU, one small allocation set per frame, no GPU knowledge.
/// </summary>
internal sealed class BindingBarAdapter {
    // Per-player bars in the multiplayer quadrant layout render mostly transparent so they read as a quiet HUD.
    private const float MultiBarAlpha = 0.5f;

    private readonly PagedInputBindings m_bindings;
    private readonly GamepadManager m_manager;
    private readonly BindingBarStore m_store;

    /// <summary>Initializes a new instance of the <see cref="BindingBarAdapter"/> class.</summary>
    /// <param name="bindings">The paged resolver whose active page drives the bar.</param>
    /// <param name="manager">The gamepad manager (controller-family resolution).</param>
    /// <param name="store">The store the overlay reads.</param>
    public BindingBarAdapter(PagedInputBindings bindings, GamepadManager manager, BindingBarStore store) {
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(store);

        m_bindings = bindings;
        m_manager = manager;
        m_store = store;
    }

    /// <summary>Publishes this frame's bar state (call once per frame, after the intent source's <c>BeginFrame</c>).</summary>
    /// <param name="source">The frame's intent source (the snapshot + command-id authority).</param>
    public void Publish(RouterIntentSource source) {
        ArgumentNullException.ThrowIfNull(source);

        var snapshot = source.FrameSnapshot;

        PublishCore(isActive: command => IsActive(snapshot: in snapshot, source: source, command: command), contextIcon: null);
    }

    /// <summary>Publishes this frame's bar state for the overworld's CONSOLE mode, where held/pressed command state
    /// comes from the page adapter rather than a router snapshot, and the contextual run/activate entry may swap
    /// its icon while the player stands at a bootable console.</summary>
    /// <param name="isHeld">Answers whether a command is held this frame.</param>
    /// <param name="contextIcon">An icon override for the <c>overworld.context</c> entry (null keeps the profile's).</param>
    public void Publish(Func<string, bool> isHeld, string? contextIcon) {
        ArgumentNullException.ThrowIfNull(isHeld);

        PublishCore(isActive: isHeld, contextIcon: contextIcon);
    }

    /// <summary>Publishes this frame's bar state for MULTI-PLAYER console mode: one 12-slot bar per active player
    /// slot, concatenated in slot order (the layout fans bar 1+ outward from the primary cluster), each bar joined
    /// against ITS player's active page and held state. The modifier pips stay singular — they render once, from
    /// the first active slot's view — and the single-bar overloads above keep the bare room unchanged.</summary>
    /// <param name="activeSlots">The active player slots, in slot order.</param>
    /// <param name="isHeldForSlot">Answers whether a slot's command is held this frame.</param>
    /// <param name="contextIconForSlot">A per-slot icon override for the <c>overworld.context</c> entry (null keeps
    /// the profile's).</param>
    public void Publish(IReadOnlyList<int> activeSlots, Func<int, string, bool> isHeldForSlot, Func<int, string?> contextIconForSlot) {
        ArgumentNullException.ThrowIfNull(activeSlots);
        ArgumentNullException.ThrowIfNull(contextIconForSlot);
        ArgumentNullException.ThrowIfNull(isHeldForSlot);

        if (activeSlots.Count == 0) {
            return;
        }

        var family = ResolveFamily();
        var slots = new BindingSlotView[activeSlots.Count * BindingBarLayout.SlotButtons.Length];

        for (var bar = 0; (bar < activeSlots.Count); bar++) {
            var playerSlot = activeSlots[bar];
            var slot = playerSlot;

            BuildBarSlots(
                barAlpha: MultiBarAlpha,
                contextIcon: contextIconForSlot(playerSlot),
                destination: slots.AsSpan(start: (bar * BindingBarLayout.SlotButtons.Length), length: BindingBarLayout.SlotButtons.Length),
                family: family,
                isActive: command => isHeldForSlot(slot, command),
                view: m_bindings.ViewFor(slot: playerSlot)
            );
        }

        m_store.Publish(frame: new BindingBarFrame(
            ActivePageId: m_bindings.ViewFor(slot: activeSlots[0]).PageId,
            BarCount: activeSlots.Count,
            Family: family,
            Modifiers: BuildModifierSlots(view: m_bindings.ViewFor(slot: activeSlots[0])),
            Slots: slots
        ));
    }

    /// <summary>Publishes the CREATOR-mode bar: the physical buttons remapped to the in-engine SDF authoring verbs
    /// (bumpers cycle the primitive, South places, East undoes, North exits, the left stick moves), plus a
    /// seven-segment readout of the selected primitive's number. A self-contained frame — creator input bypasses the
    /// binding profile, so this does not join against any page.</summary>
    /// <param name="heldButtons">The creating pad's live button mask (drives the pressed highlights).</param>
    /// <param name="shapeIndex">The selected primitive's 0-based index (rendered as the numeral readout).</param>
    public void PublishCreator(GamepadButtons heldButtons, int shapeIndex) {
        // Layout indices into BindingBarLayout.SlotButtons (the WoW cluster order).
        const int LeftShoulderSlot = 4;
        const int LeftStickSlot = 5;
        const int NorthSlot = 6;
        const int SouthSlot = 8;
        const int EastSlot = 9;
        const int RightShoulderSlot = 10;
        const int RightStickSlot = 11;

        var family = ResolveFamily();
        var slots = new BindingSlotView[BindingBarLayout.SlotButtons.Length];

        // Every slot starts as a dim, empty plate showing its physical-button badge (free buttons read as free).
        for (var index = 0; (index < slots.Length); index++) {
            slots[index] = new BindingSlotView(
                Alpha: 0.3f,
                Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[index], family: family),
                Icon: BindingIconId.None,
                Pressed: false,
                Visible: true
            );
        }

        BindingSlotView Verb(int slot, BindingIconId icon, GamepadButtons button) => new(
            Alpha: 1f,
            Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[slot], family: family),
            Icon: icon,
            Pressed: (0 != (heldButtons & button)),
            Visible: true
        );

        slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.LeftShoulder);
        slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.RightShoulder);
        slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
        slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
        slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorExit, button: GamepadButtons.ButtonNorth);

        // The left stick moves the ghost (its badge alone reads as "move" — no action icon).
        slots[LeftStickSlot] = new BindingSlotView(
            Alpha: 1f,
            Glyph: BindingGlyphResolver.Resolve(button: GamepadButtons.LeftStickPress, family: family),
            Icon: BindingIconId.None,
            Pressed: false,
            Visible: true
        );

        // The selected-primitive readout: a seven-segment numeral, no button badge (a display, not a control).
        slots[RightStickSlot] = new BindingSlotView(
            Alpha: 1f,
            Glyph: BindingGlyphId.None,
            Icon: (BindingIconId)(((int)BindingIconId.Number1) + Math.Clamp(value: shapeIndex, max: 11, min: 0)),
            Pressed: false,
            Visible: true
        );

        m_store.Publish(frame: new BindingBarFrame(
            ActivePageId: "creator",
            BarCount: 1,
            Family: family,
            // The trigger pips hint the raise/lower controls between the clusters.
            Modifiers: new BindingModifierSlotView[] {
                new(Glyph: BindingGlyphId.TriggerLeft, Held: false),
                new(Glyph: BindingGlyphId.TriggerRight, Held: false),
            },
            Slots: slots
        ));
    }

    private void PublishCore(Func<string, bool> isActive, string? contextIcon) {
        var view = m_bindings.ViewFor(slot: 0);
        var family = ResolveFamily();
        var slots = new BindingSlotView[BindingBarLayout.SlotButtons.Length];

        BuildBarSlots(contextIcon: contextIcon, destination: slots, family: family, isActive: isActive, view: view);

        m_store.Publish(frame: new BindingBarFrame(
            ActivePageId: view.PageId,
            BarCount: 1,
            Family: family,
            Modifiers: BuildModifierSlots(view: view),
            Slots: slots
        ));
    }

    // Joins one bar's twelve layout slots against a page view — the shared core of the single-bar and per-player
    // publishes, so both render identically.
    private static void BuildBarSlots(BindingPageView view, GamepadType family, Func<string, bool> isActive, string? contextIcon, Span<BindingSlotView> destination, float barAlpha = 1f) {
        for (var index = 0; (index < destination.Length); index++) {
            var buttonSource = BindingProfileDocuments.PageButtons[index];
            var button = FindButton(view: view, source: buttonSource);

            if (button is null) {
                // An UNMAPPED slot still renders — a dim, empty plate with its gamepad-glyph badge — so the player
                // can see the physical button exists and is free, rather than the cluster silently missing sockets.
                destination[index] = new BindingSlotView(
                    Alpha: (0.35f * barAlpha),
                    Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[index], family: family),
                    Icon: BindingIconId.None,
                    Pressed: false,
                    Visible: true
                );

                continue;
            }

            var icon = (((contextIcon is not null) && string.Equals(a: button.Command, b: DemoActionCommandModule.ContextCommand, comparisonType: StringComparison.OrdinalIgnoreCase))
                ? contextIcon
                : button.Icon);

            destination[index] = new BindingSlotView(
                Alpha: (1f * barAlpha),
                Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[index], family: family),
                Icon: BindingGlyphResolver.ResolveIcon(icon: icon),
                Pressed: isActive(button.Command),
                Visible: true
            );
        }
    }

    private static BindingModifierSlotView[] BuildModifierSlots(BindingPageView view) {
        var modifiers = new BindingModifierSlotView[view.Modifiers.Count];

        for (var index = 0; (index < modifiers.Length); index++) {
            var modifier = view.Modifiers[index];

            // The active page's chord IS the held modifier sequence, so Required doubles as "held right now".
            modifiers[index] = new BindingModifierSlotView(
                Glyph: BindingGlyphResolver.ResolveModifierSource(source: modifier.Source),
                Held: modifier.Required
            );
        }

        return modifiers;
    }

    private static BindingPageButtonView? FindButton(BindingPageView view, string source) {
        foreach (var button in view.Buttons) {
            if (string.Equals(a: button.Source, b: source, comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return button;
            }
        }

        return null;
    }

    private static bool IsActive(in CommandSnapshot snapshot, RouterIntentSource source, string command) {
        if (!source.TryGetCommandId(
            name: command,
            id: out var commandId
        )) {
            return false;
        }

        if (!snapshot.TryGetLane(slot: 0, out var lane)) {
            return false;
        }

        foreach (var entry in lane.Entries) {
            if ((entry.CommandId == commandId) && (entry.Phase is CommandPhase.Started or CommandPhase.Active) && entry.Value.IsActive) {
                return true;
            }
        }

        return false;
    }

    // The primary player's family drives the glyph theme; the first connected pad stands in until per-player
    // bars exist.
    private GamepadType ResolveFamily() {
        var devices = m_manager.ConnectedDevices();

        return (((devices.Count > 0) && m_manager.TryGetType(
            deviceId: devices[0],
            type: out var type
        ))
            ? type
            : GamepadType.Unknown);
    }
}
