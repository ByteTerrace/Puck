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
/// seat's published viewport rect (<see cref="WorldSeatViewports.Seat(int)"/>, the exact region
/// <c>WorldFramePresenter</c> resolved this frame, authored layout included), and publishes one
/// <see cref="OverlayBindingBarFrame"/>. Zero steady-state allocation: the per-seat arrays and
/// pressed probes are preallocated and reused, and the hint lines are re-formatted only when their source facts move
/// (a page/group flip) — safe because the feed and the overlay reader run on the
/// same thread, sequentially (see <see cref="BindingBarStore"/>'s remarks).
/// </summary>
internal sealed class WorldOverlayFeed {
    private readonly WorldBindingBarControl m_bindingBar;
    private readonly WorldSeatBindings m_bindings;
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
    // This tick's active page view per seat — the ONE mutable cell m_pressedBySource's preallocated (per-seat,
    // ctor-time) closures read, so a physical-press probe threads through every bank's ComposeBank call with no
    // per-frame delegate allocation.
    private readonly BindingPageView?[] m_activeBarView;
    private readonly Func<string, bool>[] m_pressedBySource;
    // One cached pressed-probe delegate per SEAT SLOT (the router's held state is slot-keyed), so the per-frame
    // compose closes over nothing.
    private readonly Func<string, bool>[] m_pressedBySlot;
    // Cached ONCE (never per tick, never per bank): the icon table's own resolvers are stateless, and the
    // family-aware badge resolver's only per-tick input (the connected family) rides the mutable m_currentFamily cell
    // instead of a fresh closure, the same "mutable cell + preallocated delegate" shape m_pressedBySource already takes.
    private readonly Func<string, OverlayResolvedGlyph> m_resolveBadge;
    private static readonly Func<string, OverlayResolvedGlyph> NoBadge = static _ => OverlayResolvedGlyph.None;
    // Same "mutable cell + preallocated delegate" shape as m_resolveBadge: the per-tick input is WHICH state row
    // this seat's bar named (m_currentIconRow), so the delegate is allocated once rather than per seat per tick.
    // Nothing is cached across ticks — the row's cells are live, so an ordinary state mutation retargets an icon
    // between frames.
    private readonly Func<string?, OverlayResolvedGlyph> m_resolveIcon;
    private string? m_currentIconRow;
    private int m_currentSlot;
    private GamepadType m_currentFamily;
    private readonly PlayerRoster m_roster;
    private readonly OverlayBindingSeat[] m_seats;
    private readonly OverlayBindingSlot[][] m_slots;
    private readonly BindingBarStore m_store;
    private readonly WorldSeatViewports m_viewports;

    /// <summary>Initializes a new instance of the <see cref="WorldOverlayFeed"/> class.</summary>
    /// <param name="roster">The participant roster (which seats are joined).</param>
    /// <param name="bindings">The per-seat binding resolver (each seat's active page view).</param>
    /// <param name="bindingBar">The per-seat authored binding-bar policy and live visibility resolver.</param>
    /// <param name="router">The input router whose carried held state lights the pressed chips.</param>
    /// <param name="store">The binding-bar store the overlay reads.</param>
    /// <param name="gamepads">The gamepad manager for family-resolved badge glyphs, or <see langword="null"/>
    /// (a non-Windows host) — the bar then themes for the unknown family.</param>
    /// <param name="icons">The boot document's resolved icon table (badges and bound-action icons alike).</param>
    /// <param name="viewports">Each seat's published viewport for the frame just dressed.</param>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public WorldOverlayFeed(PlayerRoster roster, WorldSeatBindings bindings, WorldBindingBarControl bindingBar, InputRouter router, BindingBarStore store, GamepadManager? gamepads, WorldIconTable icons, WorldSeatViewports viewports) {
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: bindingBar);
        ArgumentNullException.ThrowIfNull(argument: icons);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: router);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: viewports);

        m_viewports = viewports;
        m_bindings = bindings;
        m_bindingBar = bindingBar;
        m_gamepads = gamepads;
        m_icons = icons;
        m_resolveIcon = action => {
            m_bindings.GetRoutedState(
                slot: m_currentSlot,
                definition: out var definition,
                tick: out var tick
            );

            return (((m_currentIconRow is { Length: > 0 } row) && (action is { Length: > 0 }) && WorldStateReader.TryRead(
                definition: definition,
                key: action,
                rawValue: out _,
                row: out _,
                rowName: row,
                text: out var iconName,
                tick: tick
            ))
                ? icons.ResolveIcon(name: iconName)
                : OverlayResolvedGlyph.None
            );
        };
        m_resolveBadge = source => icons.ResolveBadge(
            family: m_currentFamily,
            source: source
        );
        m_roster = roster;
        m_store = store;
        m_hintLines = new string[PlayerRoster.MaxSlots][];
        m_hintViews = new BindingPageView?[PlayerRoster.MaxSlots];
        m_seats = new OverlayBindingSeat[PlayerRoster.MaxSlots];
        m_slots = new OverlayBindingSlot[PlayerRoster.MaxSlots][];
        m_modifiers = new OverlayBindingModifier[PlayerRoster.MaxSlots][];
        m_pressedBySlot = new Func<string, bool>[PlayerRoster.MaxSlots];
        m_activeBarView = new BindingPageView?[PlayerRoster.MaxSlots];
        m_pressedBySource = new Func<string, bool>[PlayerRoster.MaxSlots];

        for (var index = 0; (index < PlayerRoster.MaxSlots); index++) {
            var slot = index;

            m_hintLines[index] = [];
            m_slots[index] = new OverlayBindingSlot[(WorldBindingBarCapacity.MaxBanks * WorldBindingBarCapacity.MaxSlots)];
            m_modifiers[index] = new OverlayBindingModifier[WorldBindingBarCapacity.MaxModifiers];
            m_pressedBySlot[index] = command => router.IsCommandHeld(
                command: command,
                slot: slot
            );
            m_pressedBySource[index] = source => ((m_activeBarView[slot] is { } activeView) && BindingBarSeatComposer.IsPhysicallyPressed(
                activeView: activeView,
                isCommandHeld: m_pressedBySlot[slot],
                source: source
            ));
        }
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

            m_currentSlot = slot;

            var view = m_bindings.PageView(slot: slot);
            var barStatus = m_bindingBar.Status(slot: slot);
            var authoring = barStatus.Authoring;
            // The authored text policy resolves ONCE per seat here and shapes what the feed publishes — the writer
            // stays dumb: a suppressed badge is OverlayResolvedGlyph.None, a suppressed label is the empty string,
            // and suppressed hints are an empty span, each already a case the writer draws nothing for.
            var barText = authoring.Text;
            var liveLayout = barStatus.Layout;

            // "What does this action look like" is a lookup in authored state, not an engine table: the bar names
            // the row, the bound action names the cell, the cell's value names the icon.
            m_currentIconRow = (WorldStateBindingContext.TryParseRowReference(
                reference: authoring.IconRow,
                rowName: out var iconRowName
            )
                ? iconRowName
                : null
            );
            // The bar's opacity: the split-screen quieting lever times the visibility condition's presence, so a
            // fading "recently" condition eases the bar out instead of cutting it.
            var barAlpha = (((joined > 1)
                ? authoring.MultiSeatAlpha
                : 1f) * barStatus.Presence);
            var slotSet = authoring.SlotSet;
            var banks = authoring.Banks;
            var destination = m_slots[viewIndex];
            var writeOffset = 0;

            m_activeBarView[slot] = view;

            if (
                (slotSet.Count > 0) &&
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

                    if (!m_bindings.TryPageView(
                        pageId: bank.PageId,
                        slot: slot,
                        view: out var bankView
                    )) {
                        continue;
                    }

                    if (!stacked && !isActiveBank) {
                        continue;
                    }

                    var bankAlpha = (barAlpha * (isActiveBank
                        ? (bank.ActiveAlpha ?? 1f)
                        : bank.Alpha
                    ));
                    // Single: one bar that swaps in place — the active bank's page drawn in the first authored
                    // bank's frame and plates; its frame already carries the extent every bank gives it, so nothing
                    // moves between the models. Multi: every bank the live layout places, where it places it.
                    var placementId = (stacked
                        ? bank.Id
                        : banks[0].Id
                    );

                    if (!barStatus.Compiled.Banks.TryGetValue(
                        key: placementId,
                        value: out var placed
                    )) {
                        continue;
                    }

                    var slotCount = Math.Min(
                        val1: slotSet.Count,
                        val2: (destination.Length - writeOffset)
                    );

                    BindingBarSeatComposer.ComposeBank(
                        bankAlpha: bankAlpha,
                        bankOrder: bankIndex,
                        destination: destination.AsSpan(
                            start: writeOffset,
                            length: slotCount
                        ),
                        hideUnbound: barStatus.EffectiveHideUnbound,
                        frame: placed.Frame,
                        plates: placed.Plates,
                        // Only the active bank lights a held control: a wing shows what a chord would make the
                        // control do, and a press is happening on the page that is live, not on that hypothetical.
                        isPressed: (isActiveBank
                            ? m_pressedBySource[slot]
                            : null),
                        isCommandHeld: m_pressedBySlot[slot],
                        // The physical-button badge belongs to the live page too: a wing shows what a chord would
                        // make each plate do, and which control a plate is comes off the active bank.
                        resolveBadge: (isActiveBank
                            ? m_resolveBadge
                            : NoBadge),
                        resolveIcon: m_resolveIcon,
                        slotSet: ((slotCount == slotSet.Count)
                            ? slotSet
                            : slotSet.Take(count: slotCount).ToArray()),
                        text: barText,
                        view: bankView
                    );

                    writeOffset += slotCount;
                }

            }

            var modifierCount = (authoring.Modifiers
                ? BindingBarSeatComposer.ComposeModifiers(
                    destination: m_modifiers[viewIndex],
                    resolveBadge: m_resolveBadge,
                    text: barText,
                    view: view
                )
                : 0
            );
            var viewport = m_viewports.Seat(slot: slot).Region;
            var authoredLayout = liveLayout;

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
                    AnchorEdge: authoredLayout.ResolvedAnchor.Edge,
                    AnchorInset: authoredLayout.ResolvedAnchor.Inset,
                    ButtonSize: authoredLayout.ResolvedButtonSize,
                    GlyphOffsetRatio: authoredLayout.ResolvedGlyphOffsetRatio,
                    GlyphSizeRatio: authoredLayout.ResolvedGlyphSizeRatio,
                    HintBaseGapRatio: authoredLayout.ResolvedHintBaseGapRatio,
                    HintCellMinPx: authoredLayout.ResolvedHintCellMinPx,
                    HintCellRatio: authoredLayout.ResolvedHintCellRatio,
                    HintLineStepRatio: authoredLayout.ResolvedHintLineStepRatio,
                    LabelCellMinPx: authoredLayout.ResolvedLabelCellMinPx,
                    LabelCellRatio: authoredLayout.ResolvedLabelCellRatio,
                    LabelGapRatio: authoredLayout.ResolvedLabelGapRatio,
                    ModifierGlyphRatio: authoredLayout.ResolvedModifierGlyphRatio,
                    ModifierHalfRatio: authoredLayout.ResolvedModifierHalfRatio,
                    ModifierSpacingRatio: authoredLayout.ResolvedModifierSpacingRatio,
                    Scale: barStatus.EffectiveScale
                ),
                Frames: barStatus.Compiled.Frames,
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
