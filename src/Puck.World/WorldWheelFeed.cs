using System.Numerics;
using Puck.Commands;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The radial menu facts the <c>world.view.wheel</c> read-back echoes — the last composed frame's
/// decision, stashed beside the publish so the verb answers exactly what the overlay drew (the
/// <see cref="WorldCursorStatus"/> convention).</summary>
/// <param name="Slot">The 0-based seat slot the wheel serves (the pointer's seat).</param>
/// <param name="Open">Whether a wheel presented this frame.</param>
/// <param name="Group">The presenting wheel's binding group (empty while closed).</param>
/// <param name="RingCount">The presenting wheel's ring count (0 while closed).</param>
/// <param name="ActiveRing">The 0-based active ring (-1 while closed).</param>
/// <param name="ActiveRingLabel">The active ring's display label (empty while closed).</param>
/// <param name="HoverSector">The 0-based hovered sector within the active ring, or -1.</param>
/// <param name="HoverLabel">The hovered sector's display label (empty with none).</param>
/// <param name="HoverCommand">The command the hovered sector would commit, or <see langword="null"/>.</param>
/// <param name="HoverReason">Why nothing is hovered (<c>sector</c> when something is): <c>closed</c>,
/// <c>dead-center</c>, <c>outside</c>, or <c>no-cursor</c>.</param>
/// <param name="Center">The wheel hub in FRAME pixels (meaningful while open with a known center).</param>
/// <param name="CenterKnown">Whether the hub is anchored — false until the pointer has reported a position, and
/// everything cancels until it is.</param>
internal readonly record struct WorldWheelStatus(
    int Slot,
    bool Open,
    string Group,
    int RingCount,
    int ActiveRing,
    string ActiveRingLabel,
    int HoverSector,
    string HoverLabel,
    string? HoverCommand,
    string HoverReason,
    Vector2 Center,
    bool CenterKnown
);

/// <summary>
/// The radial action menu — held binding pages presenting themselves. Once per produced frame (the overlay's
/// <c>FeedTick</c>, after <see cref="WorldCursorFeed.Tick"/> published this frame's cursor decision) it asks the
/// seat's bindings which wheel the ACTIVE page presents (<see cref="WorldSeatBindings.WheelView"/> — non-null
/// exactly while the seat's held chord keeps a wheel's hold page selected, so holding Tab IS holding the wheel
/// open), keeps the radial's own presentation state (hub anchor, active ring, hovered sector), and publishes one
/// <see cref="OverlayWheelFrame"/> for <see cref="WheelWriter"/> to draw. Everything here is presentation/session
/// state; a sector reaches the simulation only when a commit dispatches its command through the console door
/// (<see cref="TextCommandSource.Enqueue"/> — the <see cref="WorldEditorMouse"/> discipline), so nothing new enters
/// simulation.
/// </summary>
/// <remarks>
/// <para><b>Wheel input.</b> This type is the process's ONE <see cref="IWorldWheelConsumer"/>: every wheel report
/// banks here (<see cref="OnPointer"/> drains <see cref="WorldPointer.TakeWheel"/> — the marker registration is
/// what stops <see cref="WorldPointerSink"/>'s own drain-and-discard), and <see cref="Tick"/> spends the bank —
/// cycling the active ring while open, discarding while closed (free scrolling must not cycle a wheel nobody sees,
/// and a bank must never be applied in one jump the moment one opens; the open transition ALSO discards, so a
/// nonzero first read — whatever its provenance — can never pre-cycle a freshly opened wheel).</para>
/// <para><b>Selection.</b> The hub anchors at the cursor's frame position when the wheel opens (so a Tab TAP —
/// open and release without aiming — releases over the hub and cancels); the cursor's ANGLE from the hub picks the
/// sector within the ACTIVE ring (sector 0 at twelve o'clock, clockwise — the <see cref="WheelWriter"/>
/// convention), and only the dead zone and the outer edge cancel by distance. Ring bands are presentation; the
/// wheel (or the bound ring-cycle rows) picks the ring, never the cursor's radius.</para>
/// <para><b>Commit.</b> The hold page binds Tab's release edge to <see cref="WorldWheelCommandModule.CommitCommand"/>
/// (the press that turned the page latches the row, so the release resolves back to it — the substrate's own
/// chord-latch machinery); the handler calls <see cref="Commit"/>. The commit decision is re-armed every open frame
/// and survives the close briefly (<see cref="CommitGraceFrames"/>): the release edge closes the wheel in the same
/// input fold that queues the commit dispatch, and on a frame the simulation accumulator owes zero ticks the closing
/// FeedTick can run BEFORE the dispatch's tick applies — a token consumed at first close-observation would
/// silently cancel every commit landing on such a frame. The grace is counted in observed frames, never wall time,
/// and a router-synthesized cancellation (focus loss — phase <see cref="CommandPhase.Canceled"/>, the one shape the
/// dispatch gate lets through besides a real release) revokes it immediately. All state is single-threaded on the
/// launcher's window-pump thread (the <see cref="WorldEditorMouse"/> contract): the sink's <see cref="OnPointer"/>,
/// the overlay's <see cref="Tick"/>, and every command handler run there.</para>
/// </remarks>
internal sealed class WorldWheelFeed : IWorldWheelConsumer {
    // The hub geometry, as fractions of the seat viewport's smaller pixel extent — presentation chrome, the
    // BindingBarLayout/CursorWriter constant discipline (the wheel's CONTENT is authored document data; its chrome
    // proportions are the writer's own, like the cursor's dot-to-ring ratio).
    private const float DeadZoneFraction = 0.10f;
    private const float RingWidthFraction = 0.07f;
    // A half band of forgiveness past the outer ring before a release reads as "outside".
    private const float OuterGraceRingFraction = 0.5f;
    // How many closed frames a commit decision survives — see the class remarks' commit paragraph.
    private const int CommitGraceFrames = 2;

    private readonly WorldSeatBindings m_bindings;
    // LAZY deliberately (the Func<InputRouter> precedent): a commit's dispatch door is TextCommandSource, whose
    // construction consumes the CommandRegistry, which aggregates WorldWheelCommandModule, which consumes THIS
    // feed — a direct dependency would cycle the container. Resolved on first commit, long after build.
    private readonly Func<TextCommandSource> m_console;
    private readonly WorldCursorFeed m_feed;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly WheelStore m_store;
    private readonly WorldSeatViewports m_viewports;
    private readonly OverlayWheelSeat[] m_seats = new OverlayWheelSeat[1];

    // The banked mouse-wheel notches (OnPointer writes, Tick spends — one thread, see the class remarks).
    private float m_bankedNotches;
    // The open wheel (null while closed) and its presentation state.
    private BindingWheelView? m_wheel;
    private int m_slot;
    private int m_activeRing;
    private float m_ringScroll;
    private Vector2 m_center;
    private bool m_centerKnown;
    // The ring-label cache the store frame reuses — rebuilt only when the wheel reference changes.
    private OverlayWheelRing[] m_ringCache = [];
    private BindingWheelView? m_ringCacheSource;
    // The armed commit decision — re-armed every open frame, consumed once, aged out after the close grace.
    private bool m_commitArmed;
    private int m_commitSlot;
    private BindingWheelView? m_commitWheel;
    private int m_commitRing;
    private int m_commitSector;
    private string? m_commitCommand;
    private string m_commitLabel = string.Empty;
    private string m_commitReason = "closed";
    private int m_closedFrames;
    private WorldWheelStatus m_status = new(Slot: 0, Open: false, Group: string.Empty, RingCount: 0, ActiveRing: -1, ActiveRingLabel: string.Empty, HoverSector: -1, HoverLabel: string.Empty, HoverCommand: null, HoverReason: "closed", Center: Vector2.Zero, CenterKnown: false);

    /// <summary>Initializes a new instance of the <see cref="WorldWheelFeed"/> class.</summary>
    /// <param name="pointer">The live pointer store — this type is its one registered wheel consumer.</param>
    /// <param name="roster">The roster the pointer's seat resolves against (the keyboard's seat).</param>
    /// <param name="bindings">The per-seat bindings whose active page decides which wheel presents.</param>
    /// <param name="feed">The cursor feed whose published per-frame status anchors the hub and drives hover.</param>
    /// <param name="viewports">The per-seat viewport publication the wheel's pixel geometry derives from.</param>
    /// <param name="store">The wheel store the overlay reads.</param>
    /// <param name="console">The console door a committed sector's command dispatches through — lazy, see
    /// <c>m_console</c>'s remarks.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldWheelFeed(WorldPointer pointer, PlayerRoster roster, WorldSeatBindings bindings, WorldCursorFeed feed, WorldSeatViewports viewports, WheelStore store, Func<TextCommandSource> console) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: feed);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: console);

        m_bindings = bindings;
        m_console = console;
        m_feed = feed;
        m_pointer = pointer;
        m_roster = roster;
        m_store = store;
        m_viewports = viewports;
    }

    /// <summary>The last composed frame's radial facts (the <c>world.view.wheel</c> read-back).</summary>
    public WorldWheelStatus Status => m_status;

    /// <inheritdoc/>
    public void OnPointer(int slot) {
        // Bank every wheel report at the point of arrival; Tick decides what the bank means. Draining here (not in
        // Tick) keeps the accumulator's single-consumer discipline event-shaped, exactly where the sink's own
        // drain-and-discard used to run.
        m_bankedNotches += m_pointer.TakeWheel(slot: slot);
    }

    /// <summary>Advances the radial one produced frame — see the class summary.</summary>
    public void Tick() {
        var slot = (m_roster.DeviceSlot(device: PlayerRoster.KeyboardDevice) ?? 0);
        var wheel = m_bindings.WheelView(slot: slot);
        var view = m_viewports.Seat(slot: slot);
        var notches = m_bankedNotches;

        m_bankedNotches = 0f;

        if ((wheel is null) || !view.Present) {
            // Closed (or nothing to present into): free scrolling cycles nothing — the bank was already taken
            // above and is simply dropped. The armed commit ages out over the close grace; see the class remarks.
            m_wheel = null;

            if (m_commitArmed && (++m_closedFrames > CommitGraceFrames)) {
                m_commitArmed = false;
            }

            m_status = (m_status with { Slot = slot, Open = false, Group = string.Empty, RingCount = 0, ActiveRing = -1, ActiveRingLabel = string.Empty, HoverSector = -1, HoverLabel = string.Empty, HoverCommand = null, HoverReason = "closed" });
            m_store.Publish(frame: new OverlayWheelFrame(Seats: ReadOnlyMemory<OverlayWheelSeat>.Empty));

            return;
        }

        var status = m_feed.Status;
        var positionKnown = (status.Reason is not ("no-position" or "no-view"));

        if (!ReferenceEquals(objA: m_wheel, objB: wheel) || (slot != m_slot)) {
            // OPEN (or the wheel under an open hold changed — a recompose mid-hold re-opens honestly). The bank is
            // discarded rather than applied: scrolling done before the wheel existed chose nothing on it.
            m_wheel = wheel;
            m_slot = slot;
            m_activeRing = 0;
            m_ringScroll = 0f;
            m_centerKnown = false;
        } else {
            m_ringScroll += notches;

            while (m_ringScroll >= 1f) {
                m_ringScroll -= 1f;
                m_activeRing = ((m_activeRing + 1) % wheel.Rings.Count);
            }

            while (m_ringScroll <= -1f) {
                m_ringScroll += 1f;
                m_activeRing = (((m_activeRing - 1) + wheel.Rings.Count) % wheel.Rings.Count);
            }
        }

        if (!m_centerKnown && positionKnown) {
            // The hub anchors where the cursor first stands while open — at the open itself in the ordinary case,
            // so a Tab tap releases over the hub and cancels.
            m_center = status.Frame;
            m_centerKnown = true;
        }

        var ring = wheel.Rings[m_activeRing];
        var hoverSector = -1;
        var hoverReason = "no-cursor";

        if (m_centerKnown && positionKnown) {
            var unit = MathF.Min(x: (view.Region.Width * view.Width), y: (view.Region.Height * view.Height));
            var inner = (unit * DeadZoneFraction);
            var ringWidth = (unit * RingWidthFraction);
            var outer = (inner + ((wheel.Rings.Count + OuterGraceRingFraction) * ringWidth));
            var delta = (status.Frame - m_center);
            var distance = delta.Length();

            if (distance <= inner) {
                hoverReason = "dead-center";
            } else if (distance > outer) {
                hoverReason = "outside";
            } else {
                // Angle from twelve o'clock, clockwise, sector 0 CENTERED at the top — the writer's own layout
                // convention, so what is drawn under the cursor is what commits.
                var span = (MathF.Tau / ring.Sectors.Count);
                var angle = MathF.Atan2(y: delta.X, x: -delta.Y);

                if (angle < 0f) {
                    angle += MathF.Tau;
                }

                hoverSector = ((int)((angle + (span * 0.5f)) / span) % ring.Sectors.Count);
                hoverReason = "sector";
            }
        }

        // Re-arm the commit decision with what this frame actually shows — the release consumes exactly what the
        // player saw.
        var hovered = ((hoverSector >= 0) ? ring.Sectors[hoverSector] : null);

        m_commitArmed = true;
        m_commitSlot = slot;
        m_commitWheel = wheel;
        m_commitRing = m_activeRing;
        m_commitSector = hoverSector;
        m_commitCommand = hovered?.Command;
        m_commitLabel = (hovered?.Label ?? hovered?.Command ?? string.Empty);
        m_commitReason = hoverReason;
        m_closedFrames = 0;

        PublishOpen(view: in view, wheel: wheel, hoverSector: hoverSector);
        m_status = new WorldWheelStatus(
            Slot: slot,
            Open: true,
            Group: wheel.Group,
            RingCount: wheel.Rings.Count,
            ActiveRing: m_activeRing,
            ActiveRingLabel: (ring.Label ?? ring.PageId),
            HoverSector: hoverSector,
            HoverLabel: m_commitLabel,
            HoverCommand: m_commitCommand,
            HoverReason: hoverReason,
            Center: m_center,
            CenterKnown: m_centerKnown
        );
    }

    /// <summary>Steps the active ring — the <c>player.wheel.ring</c> handler's whole act.</summary>
    /// <param name="slot">The 0-based seat slot the step targets.</param>
    /// <param name="direction">+1 cycles outward, -1 inward (wrapping).</param>
    /// <param name="activeRing">The resulting 0-based active ring.</param>
    /// <param name="ringCount">The open wheel's ring count.</param>
    /// <param name="ringLabel">The resulting active ring's display label.</param>
    /// <returns><see langword="false"/> when no wheel is open for the seat (nothing steps).</returns>
    public bool TryCycleRing(int slot, int direction, out int activeRing, out int ringCount, out string ringLabel) {
        activeRing = -1;
        ringCount = 0;
        ringLabel = string.Empty;

        if ((m_wheel is not { } wheel) || (slot != m_slot)) {
            return false;
        }

        m_activeRing = (((m_activeRing + Math.Sign(value: direction)) + wheel.Rings.Count) % wheel.Rings.Count);
        activeRing = m_activeRing;
        ringCount = wheel.Rings.Count;

        var ring = wheel.Rings[m_activeRing];

        ringLabel = (ring.Label ?? ring.PageId);

        return true;
    }

    /// <summary>The commit outcome <see cref="Commit"/> reports.</summary>
    /// <param name="Armed">Whether an armed decision existed for the seat at all.</param>
    /// <param name="Dispatched">The dispatched command, or <see langword="null"/> for a cancel.</param>
    /// <param name="Label">The dispatched sector's display label.</param>
    /// <param name="Ring">The 0-based ring the commit resolved in.</param>
    /// <param name="Sector">The 0-based sector, or -1 for a cancel.</param>
    /// <param name="Reason">The cancel reason (<c>dead-center</c>, <c>outside</c>, <c>no-cursor</c>) — empty on a
    /// dispatch.</param>
    public readonly record struct WheelCommitOutcome(bool Armed, string? Dispatched, string Label, int Ring, int Sector, string Reason);

    /// <summary>Consumes the armed commit decision — the <c>player.wheel.commit</c> handler's whole act. A
    /// dispatched sector's command is enqueued on the console door before this returns.</summary>
    /// <param name="slot">The 0-based seat slot the commit targets.</param>
    /// <returns>The outcome (Armed false when no wheel is open, or the grace expired, or the seat differs).</returns>
    public WheelCommitOutcome Commit(int slot) {
        if (!m_commitArmed || (slot != m_commitSlot)) {
            return new WheelCommitOutcome(Armed: false, Dispatched: null, Label: string.Empty, Ring: -1, Sector: -1, Reason: string.Empty);
        }

        m_commitArmed = false;

        // Both outcomes narrate act-scale on stderr (the [editor.mouse] discipline): a BOUND dispatch's own
        // CommandResult is deliberately silent on the process streams (physical snapshot entries carry no text —
        // see SimulationCommandOutputObserver), so without this line a released commit would be pipe-assertable
        // only through the dispatched verb's own echo, and a cancel not at all.
        if (m_commitCommand is not { } command) {
            Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} release cancelled ({m_commitReason})");

            return new WheelCommitOutcome(Armed: true, Dispatched: null, Label: string.Empty, Ring: m_commitRing, Sector: -1, Reason: m_commitReason);
        }

        // The dispatch: the sector's command as an ordinary console line — Console-identified, echoing and
        // refusing exactly like a typed one.
        Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} committed ring {(m_commitRing + 1)} sector {(m_commitSector + 1)} '{m_commitLabel}' -> {command}");
        m_console().Enqueue(line: command);

        return new WheelCommitOutcome(Armed: true, Dispatched: command, Label: m_commitLabel, Ring: m_commitRing, Sector: m_commitSector, Reason: string.Empty);
    }

    /// <summary>Revokes the armed commit decision without dispatching — the focus-loss cancellation path (the
    /// router's synthesized <see cref="CommandPhase.Canceled"/> edge reaches the commit handler, which calls this
    /// instead of <see cref="Commit"/>: an alt-tab mid-hold must never commit a sector).</summary>
    /// <param name="slot">The 0-based seat slot the cancellation targets.</param>
    public void Revoke(int slot) {
        if (m_commitArmed && (slot == m_commitSlot)) {
            m_commitArmed = false;
        }
    }

    private void PublishOpen(in WorldSeatView view, BindingWheelView wheel, int hoverSector) {
        if (!ReferenceEquals(objA: m_ringCacheSource, objB: wheel)) {
            var rings = new OverlayWheelRing[wheel.Rings.Count];

            for (var ringIndex = 0; (ringIndex < rings.Length); ringIndex++) {
                var ring = wheel.Rings[ringIndex];
                var sectors = new string[ring.Sectors.Count];

                for (var sectorIndex = 0; (sectorIndex < sectors.Length); sectorIndex++) {
                    var sector = ring.Sectors[sectorIndex];

                    sectors[sectorIndex] = (sector.Label ?? sector.Command);
                }

                rings[ringIndex] = new OverlayWheelRing(Label: (ring.Label ?? ring.PageId), Sectors: sectors);
            }

            m_ringCache = rings;
            m_ringCacheSource = wheel;
        }

        var unit = MathF.Min(x: (view.Region.Width * view.Width), y: (view.Region.Height * view.Height));

        // An unanchored hub (no cursor position yet) presents at the viewport center — everything cancels until
        // the pointer reports, so the placement is purely visual.
        var centerX = (m_centerKnown ? m_center.X : ((view.Region.X + (view.Region.Width * 0.5f)) * view.Width));
        var centerY = (m_centerKnown ? m_center.Y : ((view.Region.Y + (view.Region.Height * 0.5f)) * view.Height));

        m_seats[0] = new OverlayWheelSeat(
            Viewport: view.Region,
            CenterX: centerX,
            CenterY: centerY,
            InnerRadius: (unit * DeadZoneFraction),
            RingWidth: (unit * RingWidthFraction),
            ActiveRing: m_activeRing,
            HoveredSector: hoverSector,
            Rings: m_ringCache
        );
        m_store.Publish(frame: new OverlayWheelFrame(Seats: m_seats.AsMemory(start: 0, length: 1)));
    }
}
