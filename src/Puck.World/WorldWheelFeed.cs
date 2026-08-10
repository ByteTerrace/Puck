using System.Numerics;
using Puck.Commands;
using Puck.Overlays;
using Puck.World.Client;

namespace Puck.World;

/// <summary>The last composed radial facts for one seat — stashed beside the publish so
/// <c>world.view.wheel</c> answers exactly what the overlay drew (the <see cref="WorldCursorStatus"/>
/// convention).</summary>
/// <param name="Slot">The 0-based seat the radial serves.</param>
/// <param name="Open">Whether a radial presented this frame.</param>
/// <param name="Id">The presenting radial's profile-unique id (empty while closed).</param>
/// <param name="Group">The presenting radial's binding group (empty while closed).</param>
/// <param name="RingCount">The presenting radial's ring count (0 while closed).</param>
/// <param name="ActiveRing">The 0-based active ring (-1 while closed).</param>
/// <param name="ActiveRingLabel">The active ring's display label (empty while closed).</param>
/// <param name="HoverSector">The 0-based hovered sector within the active ring, or -1.</param>
/// <param name="HoverLabel">The hovered sector's display label (empty with none).</param>
/// <param name="HoverCommand">The command the hovered sector would activate, or <see langword="null"/>.</param>
/// <param name="HoverReason">Why nothing is hovered (<c>sector</c> when something is): <c>closed</c>,
/// <c>no-selection</c>, <c>disabled</c>, <c>dead-center</c>, <c>outside</c>, or <c>cancelled</c>.</param>
/// <param name="PointerSelection">The authored spatial-selection policy in force.</param>
/// <param name="Placement">The authored hub-placement policy in force.</param>
/// <param name="RingSelection">The authored explicit/excursion ring-selection policy in force.</param>
/// <param name="Center">The radial hub in frame pixels (meaningful while open).</param>
/// <param name="CenterKnown">Whether the hub is anchored.</param>
internal readonly record struct WorldWheelStatus(
    int Slot,
    bool Open,
    string Id,
    string Group,
    int RingCount,
    int ActiveRing,
    string ActiveRingLabel,
    int HoverSector,
    string HoverLabel,
    string? HoverCommand,
    string HoverReason,
    BindingWheelSpatialSelectionMode PointerSelection,
    BindingWheelPlacement Placement,
    BindingWheelRingSelectionMode RingSelection,
    Vector2 Center,
    bool CenterKnown
);

/// <summary>
/// Per-seat radial presentation over the compiled binding system. It owns only radial interaction state (layout,
/// aim, ring, hover); opening, selection, ring steps, commit and cancel are ordinary authored bindings. A committed
/// sector returns its compiler-minted <see cref="BindingActivation"/> to <see cref="InputRouter"/>, so it keeps the
/// seat's principal, command maps, value kind, phase, and deterministic snapshot path.
/// </summary>
internal sealed class WorldWheelFeed : IWorldWheelConsumer {
    // How many CLOSED frames an armed commit decision survives. The release edge closes the radial in the same
    // input fold that queues the commit, and on a frame the simulation accumulator owes zero ticks the closing
    // FeedTick can run BEFORE the dispatch's tick applies — a decision dropped at first close-observation would
    // silently cancel every commit landing on such a frame. Counted in observed frames, never wall time.
    private const int CommitGraceFrames = 2;

    private sealed class SeatState {
        public BindingWheelView? Wheel;
        public int ActiveRing;
        public float RingScroll;
        public Vector2 Center;
        public bool CenterKnown;
        public int AxisExcursionRing = -1;
        public int SpatialExcursionRing = -1;
        public BindingWheelGestureState Gesture { get; } = new();
        public long PointerSequence;
        public float BankedNotches;
        public OverlayWheelRing[] RingCache = [];
        public BindingWheelView? RingCacheSource;
        public bool CommitArmed;
        public BindingWheelView? CommitWheel;
        public int CommitRing;
        public int CommitSector;
        public BindingActivation? CommitActivation;
        public string CommitLabel = string.Empty;
        public string CommitReason = "closed";
        public int ClosedFrames;
        public WorldWheelStatus Status;
    }

    private readonly WorldSeatBindings m_bindings;
    private readonly WorldCursorFeed m_cursor;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly Func<InputRouter> m_router;
    private readonly SeatState[] m_state = new SeatState[PlayerRoster.MaxSlots];
    private readonly WheelStore m_store;
    private readonly OverlayWheelSeat[] m_visible = new OverlayWheelSeat[PlayerRoster.MaxSlots];
    private readonly WorldSeatViewports m_viewports;
    private long m_selectionSequence;

    /// <summary>Initializes a new instance of the <see cref="WorldWheelFeed"/> class.</summary>
    /// <param name="pointer">The live pointer store — this type is its one registered wheel consumer.</param>
    /// <param name="roster">The roster the pointer's seat resolves against.</param>
    /// <param name="bindings">The per-seat bindings whose active page decides which radial presents.</param>
    /// <param name="cursor">The cursor feed whose published status anchors the hub and drives pointer hover.</param>
    /// <param name="viewports">The per-seat viewport publication the pixel geometry derives from.</param>
    /// <param name="store">The wheel store the overlay reads.</param>
    /// <param name="router">The input router a committed sector's activation enters — lazy, because the command
    /// registry aggregates <see cref="WorldWheelCommandModule"/>, which consumes this feed; a direct dependency
    /// would cycle the container.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldWheelFeed(WorldPointer pointer, PlayerRoster roster, WorldSeatBindings bindings, WorldCursorFeed cursor, WorldSeatViewports viewports, WheelStore store, Func<InputRouter> router) {
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: cursor);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: router);

        m_bindings = bindings;
        m_cursor = cursor;
        m_pointer = pointer;
        m_roster = roster;
        m_router = router;
        m_store = store;
        m_viewports = viewports;

        for (var slot = 0; (slot < m_state.Length); slot++) {
            m_state[slot] = new SeatState {
                Status = ClosedStatus(slot: slot),
            };
        }
    }

    /// <summary>The pointer seat's last composed status — what <c>world.view.wheel</c> answers without a seat
    /// argument.</summary>
    public WorldWheelStatus Status => StatusFor(slot: WorldPointerSlot.Resolve(roster: m_roster));

    /// <summary>One seat's last composed status.</summary>
    /// <param name="slot">The 0-based seat slot to read.</param>
    /// <returns>That seat's status, or a closed status when the slot is out of range.</returns>
    public WorldWheelStatus StatusFor(int slot) => (((uint)slot < m_state.Length) ? m_state[slot].Status : ClosedStatus(slot: 0));

    /// <inheritdoc/>
    public void OnPointer(int slot) {
        if ((uint)slot >= m_state.Length) {
            return;
        }

        var state = m_state[slot];

        state.BankedNotches += m_pointer.TakeWheel(slot: slot);
        state.PointerSequence = ++m_selectionSequence;
    }

    /// <summary>Accepts an authored Axis2D selection binding for a seat.</summary>
    public void Select(int slot, Vector2 axis) {
        if ((uint)slot >= m_state.Length) {
            return;
        }

        var state = m_state[slot];

        state.Gesture.Select(axis: axis, sequence: ++m_selectionSequence);
    }

    /// <summary>Composes every open seat's radial once per produced frame.</summary>
    public void Tick() {
        var pointerSlot = WorldPointerSlot.Resolve(roster: m_roster);
        var pointerStatus = m_cursor.Status;
        var visibleCount = 0;

        for (var slot = 0; (slot < m_state.Length); slot++) {
            var state = m_state[slot];
            var wheel = m_bindings.WheelView(slot: slot);
            var viewport = m_viewports.Seat(slot: slot);

            if ((wheel is null) || !viewport.Present) {
                Close(slot: slot, state: state);

                continue;
            }

            var viewportCenter = new Vector2(
                x: ((viewport.Region.X + (viewport.Region.Width * 0.5f)) * viewport.Width),
                y: ((viewport.Region.Y + (viewport.Region.Height * 0.5f)) * viewport.Height)
            );
            var pointerAvailable = ((slot == pointerSlot) && (pointerStatus.Reason is not ("no-position" or "no-view")));
            var unit = MathF.Min(x: (viewport.Region.Width * viewport.Width), y: (viewport.Region.Height * viewport.Height));

            var opened = !ReferenceEquals(objA: state.Wheel, objB: wheel);

            if (opened) {
                state.Wheel = wheel;
                state.Gesture.Open();
                state.ActiveRing = wheel.Style.InitialRing;
                state.AxisExcursionRing = -1;
                state.SpatialExcursionRing = -1;
                state.RingScroll = 0f;
                state.BankedNotches = 0f;
                state.Center = BindingWheelGeometry.ResolveOpeningCenter(
                    placement: wheel.Style.Placement,
                    pointerAvailable: pointerAvailable,
                    pointer: pointerStatus.Frame,
                    viewportCenter: viewportCenter
                );
                state.CenterKnown = true;
            } else if (wheel.Style.RingSelection == BindingWheelRingSelectionMode.Explicit) {
                ApplyRingScroll(state: state, wheel: wheel);
            } else {
                state.BankedNotches = 0f;
            }

            if (pointerAvailable && RequiresSpatialNeutral(wheel: wheel)) {
                _ = state.Gesture.TryCaptureSpatialNeutral(position: pointerStatus.Frame);
            }

            var hoverSector = -1;
            var hoverReason = "no-selection";
            var center = state.Center;
            var centerKnown = state.CenterKnown;

            if (state.Gesture.Cancelled) {
                hoverReason = "cancelled";
            } else if (state.Gesture.AxisKnown && (state.Gesture.AxisSequence > state.PointerSequence)) {
                var selection = SelectAxis(state: state, wheel: wheel);
                hoverSector = selection.Sector;
                hoverReason = selection.Reason;
            } else if (pointerAvailable && centerKnown &&
                (!RequiresSpatialNeutral(wheel: wheel) || state.Gesture.SpatialNeutralKnown)) {
                var selection = SelectPointer(state: state, wheel: wheel, pointer: pointerStatus.Frame, center: center, unit: unit);
                hoverSector = selection.Sector;
                hoverReason = selection.Reason;
            }

            var ring = wheel.Rings[state.ActiveRing];
            state.Center = center;
            state.CenterKnown = centerKnown;
            Arm(state: state, wheel: wheel, ring: ring, hoverSector: hoverSector, hoverReason: hoverReason);
            m_visible[visibleCount++] = BuildSeat(state: state, wheel: wheel, viewport: in viewport, hoverSector: hoverSector, unit: unit);
            state.Status = new WorldWheelStatus(
                Slot: slot,
                Open: true,
                Id: wheel.Id,
                Group: wheel.Group,
                RingCount: wheel.Rings.Count,
                ActiveRing: state.ActiveRing,
                ActiveRingLabel: (ring.Label ?? ring.PageId),
                HoverSector: hoverSector,
                HoverLabel: state.CommitLabel,
                HoverCommand: state.CommitActivation?.Command,
                HoverReason: hoverReason,
                PointerSelection: wheel.Style.PointerSelection,
                Placement: wheel.Style.Placement,
                RingSelection: wheel.Style.RingSelection,
                Center: center,
                CenterKnown: centerKnown
            );
        }

        m_store.Publish(frame: new OverlayWheelFrame(Seats: m_visible.AsMemory(start: 0, length: visibleCount)));
    }

    /// <summary>Steps the active ring — the <c>player.wheel.ring</c> handler's whole act.</summary>
    /// <param name="slot">The 0-based seat slot the step targets.</param>
    /// <param name="direction">+1 cycles outward, -1 inward (wrapping).</param>
    /// <param name="activeRing">The resulting 0-based active ring.</param>
    /// <param name="ringCount">The open radial's ring count.</param>
    /// <param name="ringLabel">The resulting active ring's display label.</param>
    /// <param name="excursionControlled">Whether refusal means the open radial derives its ring from selector
    /// excursion rather than explicit ring-step commands.</param>
    /// <returns><see langword="false"/> when no radial is open or the open radial derives its ring from excursion.</returns>
    public bool TryCycleRing(int slot, int direction, out int activeRing, out int ringCount, out string ringLabel, out bool excursionControlled) {
        activeRing = -1;
        ringCount = 0;
        ringLabel = string.Empty;
        excursionControlled = false;

        if (((uint)slot >= m_state.Length) || (m_state[slot].Wheel is not { } wheel)) {
            return false;
        }

        var state = m_state[slot];

        if (wheel.Style.RingSelection == BindingWheelRingSelectionMode.Excursion) {
            excursionControlled = true;

            return false;
        }

        state.ActiveRing = (((state.ActiveRing + Math.Sign(value: direction)) + wheel.Rings.Count) % wheel.Rings.Count);
        activeRing = state.ActiveRing;
        ringCount = wheel.Rings.Count;
        ringLabel = (wheel.Rings[activeRing].Label ?? wheel.Rings[activeRing].PageId);

        return true;
    }

    /// <summary>Commits the last presented sector — the <c>player.wheel.commit</c> handler's whole act. When another
    /// authored opener still presents the same radial, the release is deferred: the remaining opener owns the
    /// eventual commit.</summary>
    /// <param name="slot">The 0-based seat slot the commit targets.</param>
    /// <returns>The commit disposition, every failure distinguishable from the others.</returns>
    public BindingWheelCommitResult Commit(int slot) {
        if ((uint)slot >= m_state.Length) {
            return BindingWheelCommitResult.NotArmed();
        }

        var state = m_state[slot];

        if (state.Gesture.Cancelled) {
            return BindingWheelCommitResult.Cancelled(reason: "cancelled", ring: state.CommitRing, sector: -1);
        }

        if (!state.CommitArmed) {
            return BindingWheelCommitResult.NotArmed();
        }

        var currentlyOpen = m_bindings.WheelView(slot: slot);

        if ((currentlyOpen is not null) && string.Equals(a: currentlyOpen.Id, b: state.CommitWheel?.Id, comparisonType: StringComparison.Ordinal)) {
            return BindingWheelCommitResult.Deferred(label: state.CommitLabel, ring: state.CommitRing, sector: state.CommitSector);
        }

        state.CommitArmed = false;

        if (state.CommitActivation is not { } activation) {
            Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} release cancelled ({state.CommitReason})");

            return BindingWheelCommitResult.Cancelled(reason: state.CommitReason, ring: state.CommitRing, sector: -1);
        }

        var outcome = BindingWheelCommitResult.Dispatch(
            router: m_router(),
            slot: slot,
            activation: activation,
            label: state.CommitLabel,
            ring: state.CommitRing,
            sector: state.CommitSector
        );

        if (outcome.Status == BindingWheelCommitStatus.Dispatched) {
            Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} committed ring {(state.CommitRing + 1)} sector {(state.CommitSector + 1)} '{state.CommitLabel}' -> {activation.Command}");
        }

        return outcome;
    }

    /// <summary>Latches cancellation for the seat's current gesture — both the author-bound
    /// <c>player.wheel.cancel</c> act and the router's synthesized focus-loss cancellation (an alt-tab mid-hold must
    /// never commit a sector). The latch holds until the radial next opens, so no later presentation frame can
    /// re-arm the decision it cleared.</summary>
    /// <param name="slot">The 0-based seat slot the cancellation targets.</param>
    public void Revoke(int slot) {
        if ((uint)slot < m_state.Length) {
            var state = m_state[slot];

            state.Gesture.Cancel();
            state.CommitArmed = false;
        }
    }

    private static void ApplyRingScroll(SeatState state, BindingWheelView wheel) {
        state.RingScroll += state.BankedNotches;
        state.BankedNotches = 0f;

        while (state.RingScroll >= 1f) {
            state.RingScroll -= 1f;
            state.ActiveRing = ((state.ActiveRing + 1) % wheel.Rings.Count);
        }

        while (state.RingScroll <= -1f) {
            state.RingScroll += 1f;
            state.ActiveRing = (((state.ActiveRing - 1) + wheel.Rings.Count) % wheel.Rings.Count);
        }
    }

    private static BindingWheelSelection SelectAxis(SeatState state, BindingWheelView wheel) {
        var vector = state.Gesture.Axis;

        if (wheel.Excursion is not { } excursion) {
            return BindingWheelGeometry.SelectAxis(vector: vector, sectorCount: wheel.Rings[state.ActiveRing].Sectors.Count, style: wheel.Style);
        }

        var ring = BindingWheelGeometry.ResolveExcursionRing(vector: vector, excursion: excursion, previousRing: state.AxisExcursionRing);
        state.AxisExcursionRing = ring;

        if (ring < 0) {
            return new BindingWheelSelection(Sector: -1, Outcome: BindingWheelSelectionOutcome.DeadZone);
        }

        state.ActiveRing = ring;

        return BindingWheelGeometry.SelectDirection(vector: vector, sectorCount: wheel.Rings[ring].Sectors.Count, style: wheel.Style);
    }

    private static BindingWheelSelection SelectPointer(SeatState state, BindingWheelView wheel, Vector2 pointer, Vector2 center, float unit) {
        var mode = wheel.Style.PointerSelection;
        var targetingVector = BindingWheelGeometry.ResolveSpatialTargetVector(
            mode: mode,
            position: pointer,
            neutral: state.Gesture.SpatialNeutral,
            hub: center
        );

        if ((mode == BindingWheelSpatialSelectionMode.Disabled) || (wheel.Excursion is not { } excursion)) {
            return BindingWheelGeometry.SelectSpatial(
                vector: targetingVector,
                sectorCount: wheel.Rings[state.ActiveRing].Sectors.Count,
                ringCount: wheel.Rings.Count,
                style: wheel.Style,
                mode: mode,
                unit: unit
            );
        }

        var neutralVector = (pointer - state.Gesture.SpatialNeutral);
        var normalized = BindingWheelGeometry.NormalizeSpatialExcursion(vector: neutralVector, viewportUnit: unit, excursion: excursion);
        var ring = BindingWheelGeometry.ResolveExcursionRing(vector: normalized, excursion: excursion, previousRing: state.SpatialExcursionRing);
        state.SpatialExcursionRing = ring;

        if (ring < 0) {
            return new BindingWheelSelection(Sector: -1, Outcome: BindingWheelSelectionOutcome.DeadZone);
        }

        state.ActiveRing = ring;

        return ((mode == BindingWheelSpatialSelectionMode.Angle)
            ? BindingWheelGeometry.SelectDirection(vector: normalized, sectorCount: wheel.Rings[ring].Sectors.Count, style: wheel.Style)
            : BindingWheelGeometry.SelectSpatial(
                vector: targetingVector,
                sectorCount: wheel.Rings[ring].Sectors.Count,
                ringCount: wheel.Rings.Count,
                style: wheel.Style,
                mode: mode,
                unit: unit
            ));
    }

    private static bool RequiresSpatialNeutral(BindingWheelView wheel) =>
        ((wheel.Style.PointerSelection == BindingWheelSpatialSelectionMode.Angle) ||
            ((wheel.Style.PointerSelection == BindingWheelSpatialSelectionMode.HitTarget) && (wheel.Excursion is not null)));

    private static void Arm(SeatState state, BindingWheelView wheel, BindingWheelRingView ring, int hoverSector, string hoverReason) {
        if (!state.Gesture.CanArm) {
            state.CommitArmed = false;
            state.CommitWheel = wheel;
            state.CommitRing = state.ActiveRing;
            state.CommitSector = -1;
            state.CommitActivation = null;
            state.CommitLabel = string.Empty;
            state.CommitReason = "cancelled";
            state.ClosedFrames = 0;

            return;
        }

        var hovered = ((hoverSector >= 0) ? ring.Sectors[hoverSector] : null);

        state.CommitArmed = true;
        state.CommitWheel = wheel;
        state.CommitRing = state.ActiveRing;
        state.CommitSector = hoverSector;
        state.CommitActivation = hovered?.Activation;
        state.CommitLabel = (hovered?.Label ?? hovered?.Command ?? string.Empty);
        state.CommitReason = hoverReason;
        state.ClosedFrames = 0;
    }

    private static OverlayWheelSeat BuildSeat(SeatState state, BindingWheelView wheel, in WorldSeatView viewport, int hoverSector, float unit) {
        if (!ReferenceEquals(objA: state.RingCacheSource, objB: wheel)) {
            var rings = new OverlayWheelRing[wheel.Rings.Count];

            for (var ringIndex = 0; (ringIndex < rings.Length); ringIndex++) {
                var ring = wheel.Rings[ringIndex];
                var sectors = ring.Sectors.Select(selector: static sector => (sector.Label ?? sector.Command)).ToArray();

                rings[ringIndex] = new OverlayWheelRing(Label: (ring.Label ?? ring.PageId), Sectors: sectors);
            }

            state.RingCache = rings;
            state.RingCacheSource = wheel;
        }

        var centerX = (state.CenterKnown ? state.Center.X : ((viewport.Region.X + (viewport.Region.Width * 0.5f)) * viewport.Width));
        var centerY = (state.CenterKnown ? state.Center.Y : ((viewport.Region.Y + (viewport.Region.Height * 0.5f)) * viewport.Height));

        return new OverlayWheelSeat(
            Viewport: viewport.Region,
            CenterX: centerX,
            CenterY: centerY,
            InnerRadius: (unit * wheel.Style.DeadZoneFraction),
            RingWidth: (unit * wheel.Style.RingWidthFraction),
            ActiveRing: state.ActiveRing,
            HoveredSector: hoverSector,
            RotationRadians: (wheel.Style.RotationDegrees * (MathF.PI / 180f)),
            Clockwise: wheel.Style.Clockwise,
            Rings: state.RingCache
        );
    }

    private static void Close(int slot, SeatState state) {
        state.Wheel = null;
        state.Gesture.Close();
        state.AxisExcursionRing = -1;
        state.SpatialExcursionRing = -1;
        state.BankedNotches = 0f;

        if (state.CommitArmed && (++state.ClosedFrames > CommitGraceFrames)) {
            state.CommitArmed = false;
        }

        state.Status = ClosedStatus(slot: slot);
    }

    private static WorldWheelStatus ClosedStatus(int slot) => new(
        Slot: slot,
        Open: false,
        Id: string.Empty,
        Group: string.Empty,
        RingCount: 0,
        ActiveRing: -1,
        ActiveRingLabel: string.Empty,
        HoverSector: -1,
        HoverLabel: string.Empty,
        HoverCommand: null,
        HoverReason: "closed",
        PointerSelection: BindingWheelSpatialSelectionMode.Disabled,
        Placement: BindingWheelPlacement.ViewportCenter,
        RingSelection: BindingWheelRingSelectionMode.Explicit,
        Center: Vector2.Zero,
        CenterKnown: false
    );
}
