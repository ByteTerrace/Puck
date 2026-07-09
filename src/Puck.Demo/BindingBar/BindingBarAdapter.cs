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

    /// <summary>Publishes the CREATOR-mode bar for the controller's active, CHORD-SELECTED verb page (see
    /// <c>Puck.Demo.Creator.CreatorController</c>'s remarks): bare/[LT]/[RT]/[LT,RT]/[RT,LT] select SCULPT/SELECT/
    /// STYLE/ANIMATE/RIG. North always exits; left/right stick click always toggle scope/camera. The bumpers, face
    /// buttons, and (on STYLE/RIG) the d-pad remap per page. The modifier pips are FULLY DETERMINED by the page (with
    /// exactly two modifiers, every held-combination maps to one page), so this needs no separate held-state input —
    /// a self-contained frame, since creator input bypasses the binding profile entirely.</summary>
    /// <param name="heldButtons">The creating pad's live button mask (drives the pressed highlights).</param>
    /// <param name="page">The controller's active verb page (the <c>Puck.Demo.Creator.CreatorPage</c> value).</param>
    public void PublishCreator(GamepadButtons heldButtons, int page = 0) {
        // Layout indices into BindingBarLayout.SlotButtons (the WoW cluster order).
        const int DpadUpSlot = 0;
        const int DpadRightSlot = 1;
        const int DpadDownSlot = 2;
        const int DpadLeftSlot = 3;
        const int LeftShoulderSlot = 4;
        const int LeftStickSlot = 5;
        const int NorthSlot = 6;
        const int WestSlot = 7;
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

        // A lit slot showing a bound-action ICON plus its button badge (bumpers/faces/d-pad verbs).
        BindingSlotView Verb(int slot, BindingIconId icon, GamepadButtons button) => new(
            Alpha: 1f,
            Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[slot], family: family),
            Icon: icon,
            Pressed: (0 != (heldButtons & button)),
            Visible: true
        );
        // A lit slot showing ONLY its button badge (the sticks read as "move"/"rotate"; the d-pad arrows as axes).
        BindingSlotView Badge(int slot, GamepadButtons button) => new(
            Alpha: 1f,
            Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[slot], family: family),
            Icon: BindingIconId.None,
            Pressed: (0 != (heldButtons & button)),
            Visible: true
        );

        // North is the GLOBAL exit on every page (checked once, outside the switch).
        slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorExit, button: GamepadButtons.ButtonNorth);

        // The page-dependent slots: bumpers + South/East/West always; STYLE and RIG additionally repurpose the
        // whole d-pad (their cases set DpadUp/Down/Left/Right themselves — every other page leaves the d-pad at
        // its GLOBAL raise-lower/roll badges, set below the switch).
        switch (page) {
            case 1: // SELECT [LT]
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.RightShoulder);
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorDuplicate, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.CreatorLink, button: GamepadButtons.ButtonWest);
                break;
            case 2: // STYLE [RT] — d-pad: up = bake style (press), left/right = twist sweep, down = onion sweep.
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorMaterial, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorMaterial, button: GamepadButtons.RightShoulder);
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorOpCycle, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorOpCycle, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.Number1, button: GamepadButtons.ButtonWest);
                slots[DpadUpSlot] = Verb(slot: DpadUpSlot, icon: BindingIconId.CreatorStyle, button: GamepadButtons.DpadUp);
                slots[DpadLeftSlot] = Verb(slot: DpadLeftSlot, icon: BindingIconId.Number2, button: GamepadButtons.DpadLeft);
                slots[DpadRightSlot] = Verb(slot: DpadRightSlot, icon: BindingIconId.Number2, button: GamepadButtons.DpadRight);
                slots[DpadDownSlot] = Verb(slot: DpadDownSlot, icon: BindingIconId.Number3, button: GamepadButtons.DpadDown);
                break;
            case 3: // ANIMATE [LT,RT]
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.RightShoulder);
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorRecord, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.CreatorPlay, button: GamepadButtons.ButtonWest);
                break;
            case 4: // RIG [RT,LT] — d-pad nudges the cursor chain's pole (planar, like Move).
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.RightShoulder);
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.CreatorOpCycle, button: GamepadButtons.ButtonWest);
                slots[DpadUpSlot] = Badge(slot: DpadUpSlot, button: GamepadButtons.DpadUp);
                slots[DpadDownSlot] = Badge(slot: DpadDownSlot, button: GamepadButtons.DpadDown);
                slots[DpadLeftSlot] = Badge(slot: DpadLeftSlot, button: GamepadButtons.DpadLeft);
                slots[DpadRightSlot] = Badge(slot: DpadRightSlot, button: GamepadButtons.DpadRight);
                break;
            default: // SCULPT (bare)
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.RightShoulder);
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                // West REDOES (East undoes) — the reticle reads as "step forward" opposite East's "step back".
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.Target, button: GamepadButtons.ButtonWest);
                break;
        }

        // The GLOBALS: left stick moves the target (its click toggles shape↔group scope), right stick rotates (its
        // click toggles camera mode); the d-pad's default vertical axis raises/lowers and horizontal axis rolls —
        // STYLE and RIG already overrode their four d-pad slots above, so this only fills the ones still unset.
        slots[LeftStickSlot] = Badge(slot: LeftStickSlot, button: GamepadButtons.LeftStickPress);
        slots[RightStickSlot] = Badge(slot: RightStickSlot, button: GamepadButtons.RightStickPress);

        if (page is not (2 or 4)) {
            slots[DpadUpSlot] = Badge(slot: DpadUpSlot, button: GamepadButtons.DpadUp);
            slots[DpadDownSlot] = Badge(slot: DpadDownSlot, button: GamepadButtons.DpadDown);
            slots[DpadRightSlot] = Badge(slot: DpadRightSlot, button: GamepadButtons.DpadRight);
            slots[DpadLeftSlot] = Badge(slot: DpadLeftSlot, button: GamepadButtons.DpadLeft);
        }

        // The modifier pips: fully determined by the page (LT/RT held-set — with only two modifiers every
        // combination maps to exactly one page; the pip row cannot distinguish [LT,RT] from [RT,LT] by design, the
        // active page's id/label does).
        var leftHeld = (page is 1 or 3 or 4);
        var rightHeld = (page is 2 or 3 or 4);

        m_store.Publish(frame: new BindingBarFrame(
            ActivePageId: (page switch { 1 => "creator-select", 2 => "creator-style", 3 => "creator-anim", 4 => "creator-rig", _ => "creator" }),
            BarCount: 1,
            Family: family,
            Modifiers: new BindingModifierSlotView[] {
                new(Glyph: BindingGlyphId.TriggerLeft, Held: leftHeld),
                new(Glyph: BindingGlyphId.TriggerRight, Held: rightHeld),
            },
            Slots: slots
        ));
    }

    /// <summary>Publishes the WORLD-SCULPT bar for the sculptor's chord-selected page (see
    /// <c>Puck.Demo.World.WorldSculptController</c>'s remarks): bare/[LT]/[RT]/[LT,RT]/[RT,LT] select STAMP/SELECT/
    /// TERRAIN/OVERRIDES/BOUNDS. The same pip derivation as <see cref="PublishCreator"/> — with two modifiers, every
    /// held-combination maps to one page.</summary>
    /// <param name="heldButtons">The creating pad's live button mask (pressed highlights).</param>
    /// <param name="page">The controller's active page (the <c>WorldSculptController.Page</c> value).</param>
    public void PublishWorld(GamepadButtons heldButtons, int page = 0) {
        const int DpadUpSlot = 0;
        const int DpadRightSlot = 1;
        const int DpadDownSlot = 2;
        const int DpadLeftSlot = 3;
        const int LeftShoulderSlot = 4;
        const int LeftStickSlot = 5;
        const int NorthSlot = 6;
        const int WestSlot = 7;
        const int SouthSlot = 8;
        const int EastSlot = 9;
        const int RightShoulderSlot = 10;
        const int RightStickSlot = 11;

        var family = ResolveFamily();
        var slots = new BindingSlotView[BindingBarLayout.SlotButtons.Length];

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
        BindingSlotView Badge(int slot, GamepadButtons button) => new(
            Alpha: 1f,
            Glyph: BindingGlyphResolver.Resolve(button: BindingBarLayout.SlotButtons[slot], family: family),
            Icon: BindingIconId.None,
            Pressed: (0 != (heldButtons & button)),
            Visible: true
        );

        // The page-dependent slots, mirroring WorldSculptController's own remarks table.
        switch (page) {
            case 1: // SELECT [LT] — cycle/delete/deselect, drag via the sticks; LB rebinds, RB toggles cabinet role.
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.ButtonSouth);
                slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.ButtonNorth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.Target, button: GamepadButtons.ButtonWest);
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorLink, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.CreatorStyle, button: GamepadButtons.RightShoulder);
                break;
            case 2: // TERRAIN/LIGHTS [RT] — slab/plaza/light adds, remove-nearest; sticks resize, d-pad tunes.
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.Number1, button: GamepadButtons.ButtonWest);
                slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonNorth);
                break;
            case 3: // WALK OVERRIDES [LT,RT] — paint blocker/walkable, remove nearest.
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.Target, button: GamepadButtons.ButtonEast);
                slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonNorth);
                break;
            case 4: // BOUNDS/SAVE/REPEAT [RT,LT] — save/verify, clear repeat, mirror cycle; sticks grow the lot.
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorRecord, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorPlay, button: GamepadButtons.ButtonEast);
                slots[WestSlot] = Verb(slot: WestSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonWest);
                slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorOpCycle, button: GamepadButtons.ButtonNorth);
                slots[DpadLeftSlot] = Badge(slot: DpadLeftSlot, button: GamepadButtons.DpadLeft);
                slots[DpadRightSlot] = Badge(slot: DpadRightSlot, button: GamepadButtons.DpadRight);
                break;
            default: // STAMP (bare) — place/clear the ghost, undo/redo, RB grid-snap toggle; d-pad = repeat count/spacing.
                slots[SouthSlot] = Verb(slot: SouthSlot, icon: BindingIconId.CreatorPlace, button: GamepadButtons.ButtonSouth);
                slots[EastSlot] = Verb(slot: EastSlot, icon: BindingIconId.CreatorDelete, button: GamepadButtons.ButtonEast);
                slots[NorthSlot] = Verb(slot: NorthSlot, icon: BindingIconId.CreatorPrev, button: GamepadButtons.ButtonNorth);
                slots[LeftShoulderSlot] = Verb(slot: LeftShoulderSlot, icon: BindingIconId.CreatorNext, button: GamepadButtons.LeftShoulder);
                slots[RightShoulderSlot] = Verb(slot: RightShoulderSlot, icon: BindingIconId.Target, button: GamepadButtons.RightShoulder);
                slots[DpadUpSlot] = Badge(slot: DpadUpSlot, button: GamepadButtons.DpadUp);
                slots[DpadDownSlot] = Badge(slot: DpadDownSlot, button: GamepadButtons.DpadDown);
                slots[DpadLeftSlot] = Badge(slot: DpadLeftSlot, button: GamepadButtons.DpadLeft);
                slots[DpadRightSlot] = Badge(slot: DpadRightSlot, button: GamepadButtons.DpadRight);
                break;
        }

        slots[LeftStickSlot] = Badge(slot: LeftStickSlot, button: GamepadButtons.LeftStickPress);
        slots[RightStickSlot] = Badge(slot: RightStickSlot, button: GamepadButtons.RightStickPress);

        var leftHeld = (page is 1 or 3 or 4);
        var rightHeld = (page is 2 or 3 or 4);

        m_store.Publish(frame: new BindingBarFrame(
            ActivePageId: (page switch { 1 => "world-select", 2 => "world-terrain", 3 => "world-overrides", 4 => "world-bounds", _ => "world-stamp" }),
            BarCount: 1,
            Family: family,
            Modifiers: new BindingModifierSlotView[] {
                new(Glyph: BindingGlyphId.TriggerLeft, Held: leftHeld),
                new(Glyph: BindingGlyphId.TriggerRight, Held: rightHeld),
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
