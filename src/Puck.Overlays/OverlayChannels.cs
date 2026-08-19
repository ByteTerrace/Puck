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
/// <summary>The counts the host declares the overlay for: how many local seats a per-seat writer will be asked to
/// emit, and how many authored HUD panels and elements its document contract admits at each scope. These are the
/// HOST's declarations, handed down as data at composition (a game's composition root supplies them from its own
/// schema); the render cost each writer expands one authored unit into — a gauge's <see cref="HudWriter.GaugeElementCost"/>
/// records, a text run's <see cref="HudWriter.TextRunChars"/> glyph words, a seat's <see cref="EditorGizmoWriter.MaxChipsPerSeat"/>
/// chips — is the writer's own constant and lives beside the writer. <see cref="OverlayChannelLeases"/> multiplies
/// the two into the reservation table.</summary>
/// <param name="Seats">The most local seats any per-seat writer will be asked to emit in one frame.</param>
/// <param name="HudPanels">The most world-scope authored HUD panels one frame carries.</param>
/// <param name="HudElementsPerPanel">The most elements one world-scope HUD panel carries.</param>
/// <param name="HudSeatPanelsPerSeat">The most seat-scope authored HUD panels one seat carries.</param>
/// <param name="HudElementsPerSeatPanel">The most elements one seat-scope HUD panel carries.</param>
/// <param name="BindingBarMaxBanks">The most stacked banks one seat's binding bar authors.</param>
/// <param name="BindingBarMaxSlotsPerBank">The most physical-button slots one bank's authored slot set carries.</param>
public readonly record struct OverlayCapacity(
    int Seats,
    int HudPanels,
    int HudElementsPerPanel,
    int HudSeatPanelsPerSeat,
    int HudElementsPerSeatPanel,
    int BindingBarMaxBanks,
    int BindingBarMaxSlotsPerBank
);
/// <summary>
/// The lease table: every channel's reservation, each sized at the writer's measured maximum — the largest record
/// shape its own code can legally produce — derived from the host's <see cref="OverlayCapacity"/> and the writers'
/// declared caps, so a change to either moves the reservation with it. The reservations are the budget;
/// <see cref="OverlayFrameBuilder"/>'s capacities are only the cannot-overflow backstop above them. Nothing that
/// legally renders today clips: a channel clipping at its own reservation means that channel exceeded its own declared
/// maximum, which is a bug, and it is attributed to the bug's owner without costing any other channel a single record.
/// </summary>
/// <remarks>Built once, at construction, and refused there: a table whose totals over-subscribe any of
/// <see cref="OverlayFrameBuilder"/>'s four backstops throws, naming the resource and both numbers, so an
/// over-provisioned host fails on every boot rather than at the first frame that fills the region. The gap between
/// <see cref="TotalElements"/> (and its siblings) and the backstop is simply unclaimed capacity — no
/// addon/contributor-lease admission model reads it. Growing a channel's own reservation, first-party or the
/// authored-HUD one, means growing it here, in the open, against the sum — never drawing silently from the unclaimed
/// remainder.</remarks>
public sealed class OverlayChannelLeases {
    // Binding bar, per JOINED seat — every stacked bank's own slot cluster (BindingBarMaxBanks x
    // BindingBarMaxSlotsPerBank, computed in the constructor from the host's capacity), plus the ONE page label, the
    // modifier pips, and the hint lines the bar draws once regardless of bank count. Every one of its text runs
    // (label and hints alike) rides the same character clamp; a slot's letter badge (LB/RB, the menu trio, the
    // exotics) packs into the icon record itself (OverlayFrameBuilder.PackBadgeLabel), never this text-word budget.
    private const int BindingBarFixedElementsPerSeat = ((1 + BindingBarWriter.MaxModifierPips) + BindingBarWriter.MaxHintLines);
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
    // Hud — the AUTHORED-HUD reservation, computed in the constructor from the host's OverlayCapacity: world-scope
    // panels x elements PLUS the seat-scope budget (per-seat panels x elements x seats). Each authored element's
    // render cost is taken at its OWN worst case per resource: a GAUGE costs the most render elements
    // (HudWriter.GaugeElementCost: track + fill + label) and a TEXT run costs the most glyph words
    // (HudWriter.TextRunChars, which the writer CLIPS to — the bound is enforced, not merely assumed); the two
    // ceilings are computed independently because no single authored mix hits both maxima at once, and each is
    // still a valid upper bound alone. One clip per PANEL (HudWriter.EmitPanel/EmitSeatPanel scopes every panel —
    // world or seat — to its own rect), so the clip reservation is simply the panel count.
    private const int HudElementCost = HudWriter.GaugeElementCost;
    private const int HudTextWordCost = HudWriter.TextRunChars;
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

    // Indexed by (int)OverlayChannel — the enum's declared values are the array index.
    private readonly OverlayChannelReservation[] m_reservations;

    /// <summary>Initializes a new instance of the <see cref="OverlayChannelLeases"/> class: derives every channel's
    /// reservation from <paramref name="capacity"/> and the writers' declared caps, sums them, and refuses a table
    /// that over-subscribes any of <see cref="OverlayFrameBuilder"/>'s four backstops.</summary>
    /// <param name="capacity">The host's declared counts.</param>
    /// <exception cref="ArgumentOutOfRangeException">A count in <paramref name="capacity"/> is negative, or the
    /// channels' summed reservation exceeds a backstop — the message names the resource, the summed reservation,
    /// and the backstop.</exception>
    public OverlayChannelLeases(OverlayCapacity capacity) {
        RequireNonNegative(count: capacity.Seats, name: nameof(OverlayCapacity.Seats));
        RequireNonNegative(count: capacity.HudPanels, name: nameof(OverlayCapacity.HudPanels));
        RequireNonNegative(count: capacity.HudElementsPerPanel, name: nameof(OverlayCapacity.HudElementsPerPanel));
        RequireNonNegative(count: capacity.HudSeatPanelsPerSeat, name: nameof(OverlayCapacity.HudSeatPanelsPerSeat));
        RequireNonNegative(count: capacity.HudElementsPerSeatPanel, name: nameof(OverlayCapacity.HudElementsPerSeatPanel));
        RequireNonNegative(count: capacity.BindingBarMaxBanks, name: nameof(OverlayCapacity.BindingBarMaxBanks));
        RequireNonNegative(count: capacity.BindingBarMaxSlotsPerBank, name: nameof(OverlayCapacity.BindingBarMaxSlotsPerBank));

        var seats = capacity.Seats;
        var hudWorldElements = (capacity.HudPanels * capacity.HudElementsPerPanel);
        var hudSeatElements = ((seats * capacity.HudSeatPanelsPerSeat) * capacity.HudElementsPerSeatPanel);
        var hudPanels = (capacity.HudPanels + (capacity.HudSeatPanelsPerSeat * seats));
        var bindingBarElementsPerSeat = (BindingBarFixedElementsPerSeat + (capacity.BindingBarMaxBanks * capacity.BindingBarMaxSlotsPerBank));

        Capacity = capacity;
        m_reservations = [
            new OverlayChannelReservation(
                Clips: 0,
                Elements: ConsoleElements,
                Panels: 1,
                TextWords: ConsoleTextWords
            ),
            new OverlayChannelReservation(
                Clips: 0,
                Elements: (bindingBarElementsPerSeat * seats),
                Panels: 0,
                TextWords: (BindingBarTextWordsPerSeat * seats)
            ),
            new OverlayChannelReservation(
                Clips: seats,
                Elements: (GizmoElementsPerSeat * seats),
                Panels: 0,
                TextWords: 0
            ),
            new OverlayChannelReservation(
                Clips: seats,
                Elements: (EditorHudElementsPerSeat * seats),
                Panels: seats,
                TextWords: (EditorHudTextWordsPerSeat * seats)
            ),
            new OverlayChannelReservation(
                Clips: 0,
                Elements: ToastElements,
                Panels: 1,
                TextWords: ToastTextWords
            ),
            new OverlayChannelReservation(
                Clips: hudPanels,
                Elements: ((hudWorldElements + hudSeatElements) * HudElementCost),
                Panels: hudPanels,
                TextWords: ((hudWorldElements + hudSeatElements) * HudTextWordCost)
            ),
            new OverlayChannelReservation(
                Clips: seats,
                Elements: (CursorElementsPerSeat * seats),
                Panels: 0,
                TextWords: (CursorTextWordsPerSeat * seats)
            ),
            new OverlayChannelReservation(
                Clips: seats,
                Elements: (WheelElementsPerSeat * seats),
                Panels: 0,
                TextWords: (WheelTextWordsPerSeat * seats)
            ),
        ];

        var totalClips = 0;
        var totalElements = 0;
        var totalPanels = 0;
        var totalTextWords = 0;

        for (var index = 0; (index < m_reservations.Length); index++) {
            ref readonly var reservation = ref m_reservations[index];

            totalClips += reservation.Clips;
            totalElements += reservation.Elements;
            totalPanels += reservation.Panels;
            totalTextWords += reservation.TextWords;
        }

        TotalClips = totalClips;
        TotalElements = totalElements;
        TotalPanels = totalPanels;
        TotalTextWords = totalTextWords;

        RequireWithinBackstop(resource: "clip-table rects", total: totalClips, backstop: OverlayFrameBuilder.MaxClips, backstopName: nameof(OverlayFrameBuilder.MaxClips));
        RequireWithinBackstop(resource: "element records", total: totalElements, backstop: OverlayFrameBuilder.MaxElements, backstopName: nameof(OverlayFrameBuilder.MaxElements));
        RequireWithinBackstop(resource: "panel records", total: totalPanels, backstop: OverlayFrameBuilder.MaxPanels, backstopName: nameof(OverlayFrameBuilder.MaxPanels));
        RequireWithinBackstop(resource: "glyph-code words", total: totalTextWords, backstop: OverlayFrameBuilder.TextWordCapacity, backstopName: nameof(OverlayFrameBuilder.TextWordCapacity));
    }

    /// <summary>Gets the host's declared counts this table was derived from.</summary>
    public OverlayCapacity Capacity { get; }
    /// <summary>Gets the most seats any per-seat writer may emit — <see cref="OverlayCapacity.Seats"/>.</summary>
    public int MaxSeats => Capacity.Seats;
    /// <summary>Gets the clip-table rects every reservation claims together.</summary>
    public int TotalClips { get; }
    /// <summary>Gets the element records every reservation claims together.</summary>
    public int TotalElements { get; }
    /// <summary>Gets the panel records every reservation claims together.</summary>
    public int TotalPanels { get; }
    /// <summary>Gets the glyph-code words every reservation claims together.</summary>
    public int TotalTextWords { get; }

    private static void RequireNonNegative(int count, string name) {
        if (count < 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: count,
                message: $"OverlayCapacity.{name} must not be negative.",
                paramName: "capacity"
            );
        }
    }
    private static void RequireWithinBackstop(string resource, int total, int backstop, string backstopName) {
        if (total > backstop) {
            throw new ArgumentOutOfRangeException(
                actualValue: total,
                message: $"The overlay lease table over-subscribes its {resource}: the channels reserve {total} together, but OverlayFrameBuilder.{backstopName} addresses only {backstop}. Lower the host's OverlayCapacity or a writer's declared cap; a backstop grows only with the GPU region it sizes.",
                paramName: "capacity"
            );
        }
    }

    /// <summary>Throws when a per-seat writer is about to emit more seats than the host provisioned this table for.
    /// A published seat count past <see cref="MaxSeats"/> is a structural mismatch between the host's roster and the
    /// capacity it declared, and fails loudly here rather than narrating through the per-channel reservation drop
    /// path as if it were an ordinary content overflow.</summary>
    /// <param name="seatCount">The seats the writer is about to emit.</param>
    /// <param name="writerName">The writer's diagnostic name (for the exception message).</param>
    /// <exception cref="InvalidOperationException"><paramref name="seatCount"/> exceeds <see cref="MaxSeats"/>.</exception>
    public void EnsureSeatCapacity(int seatCount, string writerName) {
        if (seatCount > MaxSeats) {
            throw new InvalidOperationException(message: $"[{writerName}] published {seatCount} seats but the host declared OverlayCapacity.Seats = {MaxSeats} — the host's roster and the capacity it composed the overlay with disagree.");
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
    public OverlayChannelReservation ReservationOf(OverlayChannel channel) {
        if (((uint)channel) >= Count) {
            throw new ArgumentOutOfRangeException(
                paramName: nameof(channel),
                actualValue: channel,
                message: "Not a declared overlay channel."
            );
        }

        return m_reservations[((int)channel)];
    }
}
