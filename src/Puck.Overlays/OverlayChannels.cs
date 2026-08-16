namespace Puck.Overlays;

/// <summary>
/// The overlay's eight declared writer channels. The value is the priority for the five first-party writers (0 draws
/// first/bottom, 4 draws last/top), pinned to the order <see cref="UnifiedOverlayNode"/> emits them in when they run
/// — draw order stops being an implicit contract nobody states and becomes the lease table's first column.
/// <see cref="Hud"/> is the odd one out: it is not part of that fixed loop at all — <see cref="UnifiedOverlayNode"/>'s
/// banded pipeline opens it up to four separate times per frame (under, base-slot-when-replacing, over — the
/// world-scope bands — plus one unbanded pass for the player-scope per-seat panels), each its own
/// <see cref="OverlayFrameBuilder.BeginChannel"/>/<see cref="OverlayFrameBuilder.EndChannel"/> scope charged
/// against this one reservation. <see cref="Wheel"/> and <see cref="Cursor"/> sit outside the fixed loop too — the
/// frame's last two channel scopes, in that order, topmost over every band and the seat panels, and deliberately
/// outside the replace-band suppression: the wheel is the pointer's radial action menu and the cursor its on-screen
/// echo, neither of them content, and a fullscreen replace panel is exactly what a pointer must still be able to
/// point (and commit) at. The cursor draws after the wheel so the pointer is never buried under the menu it steers.
/// </summary>
public enum OverlayChannel {
    /// <summary>The console panel (one singleton instance).</summary>
    Console = 0,
    /// <summary>The per-seat binding bars (one instance per joined seat).</summary>
    BindingBar = 1,
    /// <summary>The per-seat editor gizmo chips (one instance per editing seat).</summary>
    Gizmos = 2,
    /// <summary>The per-seat editor HUD strips (one instance per editing seat).</summary>
    EditorHud = 3,
    /// <summary>The transient toast echo (one singleton instance).</summary>
    Toast = 4,
    /// <summary>The authored world-scope AND player-scope HUD panels (<see cref="HudWriter"/>) — opened up to four
    /// times per frame across the under/base/over bands plus the unbanded seat-panel pass; see this enum's
    /// remarks.</summary>
    Hud = 5,
    /// <summary>The per-seat drawn pointer cursor (<see cref="CursorWriter"/>) — the frame's last, topmost channel
    /// scope; see this enum's remarks.</summary>
    Cursor = 6,
    /// <summary>The per-seat radial action menu (<see cref="WheelWriter"/>) — drawn immediately under the cursor,
    /// outside the replace-band suppression; see this enum's remarks.</summary>
    Wheel = 7,
}
/// <summary>One channel's hard reservation across the four frame resources. A channel may write up to these counts
/// and not one record more: it clips at its own boundary, attributed to itself, and can never consume another
/// channel's capacity.</summary>
/// <param name="Elements">The element records (rects, rings, text runs, icon chips) the channel may write.</param>
/// <param name="TextWords">The glyph-code words the channel's text runs may consume.</param>
/// <param name="Panels">The panel-chrome records the channel may write.</param>
/// <param name="Clips">The clip-table rects the channel may open.</param>
public readonly record struct OverlayChannelReservation(
    int Elements,
    int TextWords,
    int Panels,
    int Clips
);
/// <summary>One channel's per-frame counts across the four frame resources — written, or dropped at the channel's
/// own reservation.</summary>
/// <param name="Elements">The element records.</param>
/// <param name="TextWords">The glyph-code words.</param>
/// <param name="Panels">The panel-chrome records.</param>
/// <param name="Clips">The clip-table rects.</param>
public readonly record struct OverlayChannelUsage(
    int Elements,
    int TextWords,
    int Panels,
    int Clips
) {
    /// <summary>Gets whether every count is zero.</summary>
    public bool IsEmpty => ((Elements | TextWords | Panels | Clips) == 0);
}
/// <summary>
/// The lease table: every channel's reservation, each sized at the writer's measured maximum — the largest record
/// shape its own code can legally produce, re-derived here from the writers' declared caps so a cap change moves the
/// reservation with it. The reservations are the budget; <see cref="OverlayFrameBuilder"/>'s capacities are only the
/// cannot-overflow backstop above them. Nothing that legally renders today clips: a channel clipping at its own
/// reservation means that channel exceeded its own declared maximum, which is a bug, and it is attributed to the
/// bug's owner without costing any other channel a single record.
/// </summary>
/// <remarks>The gap between <see cref="TotalElements"/> (and its siblings) and the envelope is simply unclaimed
/// capacity — no addon/contributor-lease admission model reads it. Growing a channel's own reservation, first-party
/// or the authored-HUD one, means growing it here, in the open, against the sum — never drawing silently from the
/// unclaimed remainder.</remarks>
public static class OverlayChannelLeases {
    // The machine's local seat ceiling — mirrors the World roster's WorldPopulation.LocalSeatCount BY OWNER RULING:
    // Puck.Overlays sits below the game and may not reference Puck.World, so this cannot be derived from that
    // constant at compile time. What CAN be enforced here is that a published seat count never silently exceeds it:
    // EnsureSeatCapacity below throws loudly instead of letting the per-channel reservation drop path narrate a
    // structural roster mismatch as if it were an ordinary content overflow.
    internal const int MaxSeats = 4;

    // Binding bar, per JOINED seat — the twelve slot chips, the page label, the modifier pips, and the hint lines.
    // Every one of its text runs (label and hints alike) rides the same character clamp.
    private const int BindingBarElementsPerSeat = (((BindingBarLayout.SlotCount + 1) + BindingBarWriter.MaxModifierPips) + BindingBarWriter.MaxHintLines);
    private const int BindingBarTextWordsPerSeat = ((BindingBarWriter.MaxHintLines + 1) * BindingBarWriter.MaxLineChars);
    private const int ConsoleElements = (ConsolePanelWriter.MaxRows + 3);
    private const int ConsoleTextWords = ((ConsoleTitleChars + ((ConsolePanelWriter.MaxRows - 1) * ConsolePanelWriter.MaxColumns)) + ConsolePanelWriter.MaxColumns);
    // Console panel — one titled panel; elements are the title run, the (MaxRows - 1) history runs, the two
    // prompt runs (prefix + input), and one selection-highlight rect (Ctrl+A's all-or-nothing highlight behind the
    // input run — drawn at most once per frame, WriteRect buckets as an element like WriteText does). Text is the
    // title (ConsolePanelWriter.TitleChars — the SAME constant the writer clamps its title WriteText call to, so
    // the two can never drift apart), every history row at its column clamp, and the prompt row, whose prefix and
    // input clamps sum to exactly MaxColumns.
    private const int ConsoleTitleChars = ConsolePanelWriter.TitleChars;
    // Cursor, per VISIBLE seat — the ring, the center dot, and the hover label, all inside the seat's one clip
    // scope. Text is the label at its character clamp (CursorWriter.MaxLabelChars — the SAME constant the writer
    // clamps its label WriteText call to, so the two can never drift apart). No panel.
    private const int CursorElementsPerSeat = 3;
    private const int CursorTextWordsPerSeat = CursorWriter.MaxLabelChars;
    private const int EditorHudElementsPerSeat = (EditorHudWriter.MaxLines + 1);
    private const int EditorHudTextWordsPerSeat = (EditorHudTitleChars + (EditorHudWriter.MaxLines * EditorHudWriter.MaxLineChars));
    // Editor HUD, per EDITING seat — one strip panel inside the seat's one clip scope, the title run and the four
    // readout lines; text is the title (EditorHudWriter.TitleChars — shared with the writer's own clamp) plus every
    // line at its character clamp.
    private const int EditorHudTitleChars = EditorHudWriter.TitleChars;
    // Editor gizmos, per EDITING seat — each admitted chip writes its presence ring and its icon chip, all inside
    // the seat's one clip scope. No text, no panel.
    private const int GizmoElementsPerSeat = (EditorGizmoWriter.MaxChipsPerSeat * 2);
    private const int HudClips = HudPanels;
    private const int HudElements = (((HudMaxWorldPanels * HudMaxElementsPerPanel) * HudGaugeElementCost) + ((MaxSeats * HudMaxElementsPerSeatPanel) * HudGaugeElementCost));
    private const int HudGaugeElementCost = 3;
    private const int HudMaxElementsPerPanel = 24;
    private const int HudMaxElementsPerSeatPanel = 12;
    // Hud — the AUTHORED-HUD reservation (Puck.World.Schema's WorldHudCapacity is the source of truth; this project
    // cannot reference that one, so these constants restate its numbers by hand): world-scope panels/elements PLUS
    // the seat-scope budget (player-scope HUD, authored via identity.hud <panel-json> [player], element count
    // enforced at WorldDefinitionValidator against this SAME HudMaxElementsPerSeatPanel ceiling) spends from. DO NOT
    // widen these numbers for a future HUD change without a fresh measurement against WorldHudCapacity.
    // World scope: HudMaxWorldPanels panels x HudMaxElementsPerPanel elements. Seat scope: one panel x twelve
    // elements per LOCAL seat. Each authored element's render cost is taken at its OWN worst case per resource: a
    // GAUGE costs the most render elements (3: track + fill + label) and a TEXT run costs the most glyph words
    // (HudWriter.TextRunChars, which the writer CLIPS to — the bound is enforced, not merely assumed) — the two
    // ceilings are computed independently because no single authored mix hits both maxima at once, and each is
    // still a valid upper bound alone. One clip per PANEL (HudWriter.EmitPanel/EmitSeatPanel scopes every
    // panel — world or seat — to its own rect), so the clip reservation is simply the panel count.
    private const int HudMaxWorldPanels = 4;
    private const int HudPanels = (HudMaxWorldPanels + (HudSeatPanelsPerSeat * MaxSeats));
    private const int HudSeatPanelsPerSeat = 1;
    // Read from the writer's own clamp (the EditorHudTitleChars precedent above) rather than restated: the number
    // multiplied into the reservation and the number a text run is actually clipped to are then one constant.
    private const int HudTextWordCost = HudWriter.TextRunChars;
    private const int HudTextWords = (((HudMaxWorldPanels * HudMaxElementsPerPanel) * HudTextWordCost) + ((MaxSeats * HudMaxElementsPerSeatPanel) * HudTextWordCost));
    private const int ToastElements = (3 + ToastWriter.MaxMessageLines);
    // Toast — one chip panel, the state rail, the icon square, the [OK]/[ER] label and the wrapped message lines;
    // text is the label (ToastWriter.LabelChars — shared with the writer's own clamp) plus every wrapped line at its
    // character clamp.
    private const int ToastLabelChars = ToastWriter.LabelChars;
    private const int ToastTextWords = (ToastLabelChars + (ToastWriter.MaxMessageChars * ToastWriter.MaxMessageLines));
    // Wheel, per OPEN seat — the hub dot, one outline per ring plus the active ring's second stroke, every ring's
    // sector labels, the hovered sector's marker, and the active ring's hub label, all inside the seat's one clip
    // scope. Every count and clamp reads the writer's own declared caps (the CursorWriter discipline), so a cap
    // change moves the reservation with it. No panel.
    private const int WheelElementsPerSeat = ((1 + (WheelWriter.MaxRings + 1)) + ((WheelWriter.MaxRings * WheelWriter.MaxSectorsPerRing) + (1 + 1)));
    private const int WheelTextWordsPerSeat = (((WheelWriter.MaxRings * WheelWriter.MaxSectorsPerRing) * WheelWriter.MaxSectorLabelChars) + WheelWriter.MaxRingLabelChars);

    /// <summary>The number of declared channels.</summary>
    public const int Count = 8;
    /// <summary>The clip-table rects every reservation claims together.</summary>
    public const int TotalClips = ((((MaxSeats + MaxSeats) + HudClips) + MaxSeats) + MaxSeats);
    /// <summary>The element records every reservation claims together.</summary>
    public const int TotalElements = (((((((ConsoleElements + (BindingBarElementsPerSeat * MaxSeats)) + (GizmoElementsPerSeat * MaxSeats)) + (EditorHudElementsPerSeat * MaxSeats)) + ToastElements) + HudElements) + (CursorElementsPerSeat * MaxSeats)) + (WheelElementsPerSeat * MaxSeats));
    /// <summary>The panel records every reservation claims together.</summary>
    public const int TotalPanels = (((1 + MaxSeats) + 1) + HudPanels);
    /// <summary>The glyph-code words every reservation claims together.</summary>
    public const int TotalTextWords = ((((((ConsoleTextWords + (BindingBarTextWordsPerSeat * MaxSeats)) + (EditorHudTextWordsPerSeat * MaxSeats)) + ToastTextWords) + HudTextWords) + (CursorTextWordsPerSeat * MaxSeats)) + (WheelTextWordsPerSeat * MaxSeats));

    /// <summary>Throws when a per-seat writer is about to emit more seats than the lease table provisioned for.
    /// <see cref="MaxSeats"/> mirrors Puck.World's <c>WorldPopulation.LocalSeatCount</c>, but nothing checks that
    /// mirror across the assembly boundary, so a grown roster crossing this ceiling must fail loudly here rather
    /// than quietly clipping seats through the per-channel reservation drop path.</summary>
    /// <param name="seatCount">The seats the writer is about to emit.</param>
    /// <param name="writerName">The writer's diagnostic name (for the exception message).</param>
    /// <exception cref="InvalidOperationException"><paramref name="seatCount"/> exceeds <see cref="MaxSeats"/>.</exception>
    internal static void EnsureSeatCapacity(int seatCount, string writerName) {
        if (seatCount > MaxSeats) {
            throw new InvalidOperationException(message: $"[{writerName}] published {seatCount} seats but OverlayChannelLeases.MaxSeats is only {MaxSeats} (it mirrors Puck.World's WorldPopulation.LocalSeatCount) — grow MaxSeats and its per-seat reservations here before raising the seat roster.");
        }
    }

    /// <summary>The channel's stable diagnostic name (the attribution overflow narration prints).</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The kebab-case channel name.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public static string NameOf(OverlayChannel channel) => channel switch {
        OverlayChannel.Console => "console",
        OverlayChannel.BindingBar => "binding-bar",
        OverlayChannel.Gizmos => "gizmos",
        OverlayChannel.EditorHud => "editor-hud",
        OverlayChannel.Toast => "toast",
        OverlayChannel.Hud => "hud",
        OverlayChannel.Cursor => "cursor",
        OverlayChannel.Wheel => "wheel",
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(channel),
        actualValue: channel,
        message: "Not a declared overlay channel."
    ),
    };
    /// <summary>The reservation of one channel.</summary>
    /// <param name="channel">The channel.</param>
    /// <returns>The channel's hard reservation.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a declared channel.</exception>
    public static OverlayChannelReservation ReservationOf(OverlayChannel channel) => channel switch {
        OverlayChannel.Console => new OverlayChannelReservation(
        Clips: 0,
        Elements: ConsoleElements,
        Panels: 1,
        TextWords: ConsoleTextWords
    ),
        OverlayChannel.BindingBar => new OverlayChannelReservation(
        Clips: 0,
        Elements: (BindingBarElementsPerSeat * MaxSeats),
        Panels: 0,
        TextWords: (BindingBarTextWordsPerSeat * MaxSeats)
    ),
        OverlayChannel.Gizmos => new OverlayChannelReservation(
        Clips: MaxSeats,
        Elements: (GizmoElementsPerSeat * MaxSeats),
        Panels: 0,
        TextWords: 0
    ),
        OverlayChannel.EditorHud => new OverlayChannelReservation(
        Clips: MaxSeats,
        Elements: (EditorHudElementsPerSeat * MaxSeats),
        Panels: MaxSeats,
        TextWords: (EditorHudTextWordsPerSeat * MaxSeats)
    ),
        OverlayChannel.Toast => new OverlayChannelReservation(
        Clips: 0,
        Elements: ToastElements,
        Panels: 1,
        TextWords: ToastTextWords
    ),
        OverlayChannel.Hud => new OverlayChannelReservation(
        Clips: HudClips,
        Elements: HudElements,
        Panels: HudPanels,
        TextWords: HudTextWords
    ),
        OverlayChannel.Cursor => new OverlayChannelReservation(
        Clips: MaxSeats,
        Elements: (CursorElementsPerSeat * MaxSeats),
        Panels: 0,
        TextWords: (CursorTextWordsPerSeat * MaxSeats)
    ),
        OverlayChannel.Wheel => new OverlayChannelReservation(
        Clips: MaxSeats,
        Elements: (WheelElementsPerSeat * MaxSeats),
        Panels: 0,
        TextWords: (WheelTextWordsPerSeat * MaxSeats)
    ),
        _ => throw new ArgumentOutOfRangeException(
        paramName: nameof(channel),
        actualValue: channel,
        message: "Not a declared overlay channel."
    ),
    };
}
