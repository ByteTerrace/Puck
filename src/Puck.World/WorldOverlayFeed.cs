using Puck.Commands;
using Puck.Input;
using Puck.Input.Devices;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>
/// The World-side feed behind the unified overlay's binding-bar source: once per produced frame (the node's
/// <c>FeedTick</c>, render thread) it walks the joined roster seats, resolves each seat's active
/// <see cref="Puck.Commands.BindingPageView"/> through <see cref="WorldSeatBindings"/>, joins the resolved
/// binding-bar policy's authored slot set against EVERY authored bank's own named page for the connected controller
/// family — chips lit from the router's carried PHYSICAL button state, the same fact reused across every bank
/// showing that button's slot (<see cref="BindingBarSeatComposer.IsPhysicallyPressed"/>) — scopes it into that
/// seat's split-screen viewport rect
/// (<see cref="WorldFrameSource.LayoutRegion(int, int)"/>), and publishes one
/// <see cref="OverlayBindingBarFrame"/>. Zero steady-state allocation: the per-seat arrays and
/// pressed probes are preallocated and reused, and the hint lines are re-formatted only when their source facts move
/// (a page/group flip) — safe because the feed and the overlay reader run on the
/// same thread, sequentially (see <see cref="BindingBarStore"/>'s remarks).
/// </summary>
internal sealed class WorldOverlayFeed {
    private readonly WorldBindingBarControl m_bindingBar;
    private readonly WorldSeatBindings m_bindings;
    private readonly WorldClient m_client;
    private readonly GamepadManager? m_gamepads;
    private readonly WorldIconTable m_icons;
    private readonly string[][] m_hintLines;
    // Per-SEAT chord-hint cache: the hint lines are formatted once per published view (views are immutable and
    // reference-stable per page), so the per-frame publish is a reference handoff.
    private readonly BindingPageView?[] m_hintViews;
    private readonly OverlayBindingModifier[][] m_modifiers;
    // Per-SEAT parsed-slot-set cache: re-parses the authored button-name strings only when the resolved
    // WorldBindingBarAuthoring instance changes (a document delivery or profile switch), never every frame — the
    // resolved document graph is reference-stable while nothing changes, so reference equality is the change key.
    private readonly WorldBindingBarAuthoring?[] m_barAuthoringSeen;
    private readonly GamepadButtons[][] m_barSlotSet;
    // This tick's active page view per seat — the ONE mutable cell m_pressedByButton's preallocated (per-seat,
    // ctor-time) closures read, so a physical-press probe threads through every bank's ComposeBank call with no
    // per-frame delegate allocation.
    private readonly BindingPageView?[] m_activeBarView;
    private readonly Func<GamepadButtons, bool>[] m_pressedByButton;
    // One cached pressed-probe delegate per SEAT SLOT (the router's held state is slot-keyed), so the per-frame
    // compose closes over nothing.
    private readonly Func<string, bool>[] m_pressedBySlot;
    // Cached ONCE (never per tick, never per bank): the icon table's own resolvers are stateless, and the two
    // family-aware resolvers' only per-tick input (the connected family) rides the mutable m_currentFamily cell
    // instead of a fresh closure, the same "mutable cell + preallocated delegate" shape m_pressedByButton already takes.
    private readonly Func<GamepadButtons, OverlayResolvedGlyph> m_resolveBadge;
    private readonly Func<string?, OverlayResolvedGlyph> m_resolveIcon;
    private readonly Func<string, OverlayResolvedGlyph> m_resolveModifierSource;
    private GamepadType m_currentFamily;
    private readonly PlayerRoster m_roster;
    private readonly OverlayBindingSeat[] m_seats;
    private readonly OverlayBindingSlot[][] m_slots;
    private readonly BindingBarStore m_store;

    /// <summary>Initializes a new instance of the <see cref="WorldOverlayFeed"/> class.</summary>
    /// <param name="roster">The participant roster (which seats are joined).</param>
    /// <param name="bindings">The per-seat binding resolver (each seat's active page view).</param>
    /// <param name="bindingBar">The per-seat authored binding-bar policy and live visibility resolver.</param>
    /// <param name="client">The client view.</param>
    /// <param name="router">The input router whose carried held state lights the pressed chips.</param>
    /// <param name="store">The binding-bar store the overlay reads.</param>
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <param name="icons">The boot document's resolved icon table (badges and bound-action icons alike).</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, WorldBindingBarControl bindingBar, WorldClient client, InputRouter router, BindingBarStore store, GamepadManager? gamepads, WorldIconTable icons) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: bindingBar);
        ArgumentNullException.ThrowIfNull(argument: client);
        ArgumentNullException.ThrowIfNull(argument: icons);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: store);

        m_bindings = bindings;
        m_bindingBar = bindingBar;
        m_client = client;
        m_gamepads = gamepads;
        m_icons = icons;
        m_resolveBadge = button => icons.ResolveBadge(
            button: button,
            family: m_currentFamily
        );
        m_resolveIcon = icons.ResolveIcon;
        m_resolveModifierSource = source => icons.ResolveModifierSource(
            source: source,
            family: m_currentFamily
        );
        m_roster = roster;
        m_store = store;
        m_hintLines = new string[PlayerRoster.MaxSlots][];
        m_hintViews = new BindingPageView?[PlayerRoster.MaxSlots];
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];
        m_pressedBySlot = new Func<string, bool>[PlayerRoster.MaxSlots];
        m_barAuthoringSeen = new WorldBindingBarAuthoring?[PlayerRoster.MaxSlots];
        m_barSlotSet = new GamepadButtons[PlayerRoster.MaxSlots][];
        m_activeBarView = new BindingPageView?[PlayerRoster.MaxSlots];
        m_pressedByButton = new Func<GamepadButtons, bool>[PlayerRoster.MaxSlots];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            var slot = index;

            m_hintLines[index] = [];
            m_slots[index] = new OverlayBindingSlot[(WorldBindingBarCapacity.MaxBanks * GamepadButtonCatalog.Count)];
            m_modifiers[index] = new OverlayBindingModifier[WorldBindingBarCapacity.MaxModifiers];
            m_barSlotSet[index] = [];
            m_pressedBySlot[index] = command => router.IsCommandHeld(
                command: command,
                slot: slot
            );
            m_pressedByButton[index] = button => ((m_activeBarView[slot] is { } activeView) && BindingBarSeatComposer.IsPhysicallyPressed(
                activeView: activeView,
                button: button,
                isCommandHeld: m_pressedBySlot[slot]
            ));
        }
    }
    // Re-parses a seat's authored slot-set names only when the resolved WorldBindingBarAuthoring instance changes.
    // An unknown name never reaches here (the document validator refuses it by name before boot); a defensive parse
    // failure is simply dropped rather than thrown, since this is a per-frame render path.
    private ReadOnlySpan<GamepadButtons> ResolveSlotSet(int slot, WorldBindingBarAuthoring authoring) {
        if (!ReferenceEquals(objA: m_barAuthoringSeen[slot], objB: authoring)) {
            var names = authoring.SlotSet;
            var parsed = new GamepadButtons[names.Count];
            var count = 0;

            foreach (var name in names) {
                if (
                    Enum.TryParse<GamepadButtons>(
                    value: name,
                    ignoreCase: false,
                    result: out var button
                ) &&
                    (button != GamepadButtons.None)
                ) {
                    parsed[count++] = button;
                }
            }

            m_barSlotSet[slot] = ((count == parsed.Length)
                ? parsed
                : parsed[..count]
            );
            m_barAuthoringSeen[slot] = authoring;
        }

        return m_barSlotSet[slot];
    }

    // The seat's chord-hint lines, re-formatted only when its published view changes (a page/group flip or a
    // recompose — human cadence, never per frame). One ASCII line per command-chord row of the active group:
    // the chord's modifier labels joined by '+', then the row's label (or its command name).
    private ReadOnlyMemory<string> HintLinesFor(int slot, BindingPageView view) {
        if (ReferenceEquals(
            objA: m_hintViews[slot],
            objB: view
        )) {
            return m_hintLines[slot];
        }

        var chords = view.CommandChords;
        var lines = ((chords.Count == 0)
            ? []
            : new string[chords.Count]
        );

        for (var index = 0; (index < chords.Count); index++) {
            // Held members join with '+' (any order); chord members with '>' (press order); both halves separated by a space.
            var chord = chords[index];
            var heldIds = (chord.Held ?? []);
            var held = new string[heldIds.Count];
            var ordered = new string[chord.Chord.Count];

            for (var memberIndex = 0; (memberIndex < held.Length); memberIndex++) {
                held[memberIndex] = (ModifierLabelFor(
                    id: heldIds[memberIndex],
                    view: view
                ) ?? heldIds[memberIndex].ToUpperInvariant());
            }

            for (var memberIndex = 0; (memberIndex < ordered.Length); memberIndex++) {
                ordered[memberIndex] = (ModifierLabelFor(
                    id: chord.Chord[memberIndex],
                    view: view
                ) ?? chord.Chord[memberIndex].ToUpperInvariant());
            }

            var heldText = string.Join(
                separator: '+',
                values: held
            );
            var orderedText = string.Join(
                separator: '>',
                values: ordered
            );

            lines[index] = $"{(((held.Length > 0) && (ordered.Length > 0))
                ? ((heldText + " ") + orderedText)
                : ((held.Length > 0)
                    ? heldText
                    : orderedText))} {(chord.Label ?? chord.Command)}";
        }

        m_hintLines[slot] = lines;
        m_hintViews[slot] = view;

        return lines;
    }
    private static string? ModifierLabelFor(BindingPageView view, string id) {
        foreach (var modifier in view.Modifiers) {
            if (string.Equals(
                a: modifier.Id,
                b: id,
                comparisonType: StringComparison.Ordinal
            )) {
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
            : GamepadType.Unknown
        );
    }

    /// <summary>Recomposes and publishes this frame's per-seat binding frame (the overlay's
    /// <c>FeedTick</c>).</summary>
    public void Tick() {
        var joined = m_roster.Count;
        var family = ResolveFamily();

        m_currentFamily = family;

        // The SAME layout resolution the frame source renders with, so each bar lands in its seat's actual rect.
        var viewIndex = 0;

        for (var slot = 0; (slot < PlayerRoster.MaxSlots); slot++) {
            if (!m_roster.IsJoined(slot: slot)) {
                continue;
            }

            var view = m_bindings.PageView(slot: slot);
            var barStatus = m_bindingBar.Status(slot: slot);
            var authoring = barStatus.Authoring;
            // The authored text policy resolves ONCE per seat here and shapes what the feed publishes — the writer
            // stays dumb: a suppressed badge is OverlayResolvedGlyph.None, a suppressed label is the empty string,
            // and suppressed hints are an empty span, each already a case the writer draws nothing for.
            var barText = authoring.Text;
            var barAlpha = ((joined > 1)
                ? authoring.MultiSeatAlpha
                : 1f
            );
            var slotSet = ResolveSlotSet(
                authoring: authoring,
                slot: slot
            );
            var banks = authoring.Banks;
            var destination = m_slots[viewIndex];
            var writeOffset = 0;

            m_activeBarView[slot] = view;

            if (
                (slotSet.Length > 0) &&
                (banks.Count > 0)
            ) {
                // A player's own "stacked off" preference still needs SOME bank to show — fall back to the full
                // stack when no authored bank actually names the seat's current page, rather than drawing nothing.
                var hasActiveBank = false;

                for (var bankIndex = 0; ((bankIndex < banks.Count) && !hasActiveBank); bankIndex++) {
                    hasActiveBank = string.Equals(
                        a: banks[bankIndex].PageId,
                        b: view.PageId,
                        comparisonType: StringComparison.Ordinal
                    );
                }

                var stacked = (barStatus.Stacked || !hasActiveBank);

                for (var bankIndex = 0; ((bankIndex < banks.Count) && (writeOffset < destination.Length)); bankIndex++) {
                    var bank = banks[bankIndex];
                    var isActiveBank = string.Equals(
                        a: bank.PageId,
                        b: view.PageId,
                        comparisonType: StringComparison.Ordinal
                    );

                    if (
                        (!stacked && !isActiveBank) ||
                        !m_bindings.TryPageView(
                        pageId: bank.PageId,
                        slot: slot,
                        view: out var bankView
                    )
                    ) {
                        continue;
                    }

                    var bankAlpha = (barAlpha * (isActiveBank
                        ? (bank.ActiveAlpha ?? 1f)
                        : bank.Alpha
                    ));
                    var slotCount = Math.Min(
                        val1: slotSet.Length,
                        val2: (destination.Length - writeOffset)
                    );

                    BindingBarSeatComposer.ComposeBank(
                        bankAlpha: bankAlpha,
                        bankOffset: new System.Numerics.Vector2(bank.OffsetX, bank.OffsetY),
                        destination: destination.AsSpan(
                            start: writeOffset,
                            length: slotCount
                        ),
                        hideUnbound: barStatus.EffectiveHideUnbound,
                        isPressed: m_pressedByButton[slot],
                        resolveBadge: m_resolveBadge,
                        resolveIcon: m_resolveIcon,
                        slotSet: slotSet[..slotCount],
                        text: barText,
                        view: bankView
                    );

                    writeOffset += slotCount;
                }
            }

            var modifierCount = BindingBarSeatComposer.ComposeModifiers(
                destination: m_modifiers[viewIndex],
                resolveModifierSource: m_resolveModifierSource,
                text: barText,
                view: view
            );
            var viewport = WorldFrameSource.LayoutRegion(
                count: joined,
                index: viewIndex
            );
            var authoredLayout = authoring.ResolvedLayout;

            m_seats[viewIndex] = new OverlayBindingSeat(
                Group: view.Group,
                Hints: (barText
                    ? HintLinesFor(
                        slot: slot,
                        view: view
                    )
                    : ReadOnlyMemory<string>.Empty),
                Label: (barText
                    ? (view.Label ?? view.PageId)
                    : string.Empty),
                Modifiers: m_modifiers[viewIndex].AsMemory(
                    length: modifierCount,
                    start: 0
                ),
                PageId: view.PageId,
                Slots: destination.AsMemory(
                    length: writeOffset,
                    start: 0
                ),
                Viewport: viewport,
                Layout: new BindingBarLayoutOptions(
                    ButtonSize: authoredLayout.ButtonSize,
                    CenterGap: authoredLayout.CenterGap,
                    AnchorOffsetY: authoredLayout.AnchorOffsetY,
                    GlyphOffsetRatio: authoredLayout.GlyphOffsetRatio,
                    GlyphSizeRatio: authoredLayout.GlyphSizeRatio,
                    Scale: barStatus.EffectiveScale
                ),
                Visible: !barStatus.Hidden
            );

            viewIndex++;
        }

        m_store.Publish(frame: new OverlayBindingBarFrame(
            Family: family,
            Seats: m_seats.AsMemory(
                length: viewIndex,
                start: 0
            )
        ));
    }
}
