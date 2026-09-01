using System.Numerics;
using Puck.Commands;
using Puck.Hosting;
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
/// <param name="SelectorDeadZone">The effective normalized Axis2D neutral threshold.</param>
/// <param name="SwitchFraction">The normalized magnitude required to replace a grace-held sector.</param>
/// <param name="SelectionGraceSeconds">The authored neutral dwell, in seconds, before an empty commit cancels.</param>
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
    float SelectorDeadZone,
    float SwitchFraction,
    float SelectionGraceSeconds,
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
    /// <summary>The label-row cell the hub reads while nothing is hovered — what releasing now does. The ONE reserved
    /// key in a wheel's label row; every other key is a sector id.</summary>
    public const string HubLabelKey = "cancel";

    private readonly WorldSeatBindings m_bindings;
    // The engine-tick capture clock the selection-grace window is measured against. The hovered sector it decides
    // flows through Arm -> BindingWheelCommitResult.Dispatch -> InputRouter.Activate into the seat's deterministic
    // lane, so the window must be counted on the base every other input timestamp shares — a private Stopwatch read
    // here would be a second, unsubstitutable time source sitting directly upstream of a simulation command.
    private readonly IInputClock m_clock;
    private readonly WorldCursorFeed m_cursor;
    private readonly WorldPointer m_pointer;
    private readonly PlayerRoster m_roster;
    private readonly Func<InputRouter> m_router;
    private readonly WorldIconTable m_icons;
    private readonly WheelStore m_store;
    private readonly WorldSeatViewports m_viewports;

    private long m_selectionSequence;

    private sealed class SeatState {
        public int ActiveRing;
        public float BankedNotches;
        public Vector2 Center;
        public bool CenterKnown;
        public int ClosedFrames;
        public BindingActivation? CommitActivation;
        public bool CommitArmed;
        public int CommitRing;
        public int CommitSector;
        public BindingWheelView? CommitWheel;
        // A relative aim source's (mouse motion's) accumulated deflection since the radial opened, in window client
        // pixels — the pointer's virtual position relative to the hub, read by the wheel's pointerSelection policy.
        public Vector2 Deflection;
        public bool DeflectionKnown;
        // Input may logically open a gesture before the next presentation tick installs Wheel. Keeping the logical
        // identity separate prevents a one-frame flick or relative delta from being cleared at presentation open.
        public BindingWheelView? GestureWheel;
        // The authored selection-grace window converted to engine ticks ONCE per gesture, and the tick the current
        // dead-centre dwell started on (valid only while GraceSinceKnown — tick 0 is a real reading).
        public ulong GraceSinceTick;
        public bool GraceSinceKnown;
        public ulong GraceTicks;
        public long PointerSequence;
        public BindingWheelView? RingCacheSource;
        // The sector text lives in a state row, so the cache is keyed on the definition delivery too: an applied
        // mutation bumps the revision and the labels re-resolve on the next frame.
        public WorldDefinition? RingCacheDefinition;
        public float RingScroll;
        public WorldWheelStatus Status;
        public BindingWheelView? Wheel;

        // The last sector a live selection highlighted; it drops back to -1 once the dead-centre dwell outlasts the
        // authored grace window (counted in engine ticks — see GraceSinceTick).
        public int GraceSector = -1;
        public int AxisExcursionRing = -1;
        public int SpatialExcursionRing = -1;
        public BindingWheelGestureState Gesture { get; } = new();
        public OverlayWheelRing[] RingCache = [];
        public string CommitLabel = string.Empty;
        // The outcome flash: the seat as last drawn while open, re-emitted after the wheel closes with the local
        // dispatch outcome's glow. Dispatched means handed to the input router; a later simulation/server refusal is
        // deliberately not overclaimed here.
        public OverlayWheelSeat LastSeat;
        public bool LastSeatKnown;
        public OverlayWheelOutcome FlashOutcome;
        public int FlashSector = -1;
        public long FlashSince;
        public float FlashSeconds;
        public float FlashEase;
        public string CommitReason = "closed";
    }

    private readonly SeatState[] m_state = new SeatState[PlayerRoster.MaxSlots];
    private readonly OverlayWheelSeat[] m_visible = new OverlayWheelSeat[PlayerRoster.MaxSlots];

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
    /// <param name="icons">The world's icon table, resolving a sector's icon name to atlas content.</param>
    /// <param name="clock">The engine-tick capture clock the selection-grace window is counted on — the process's
    /// one <see cref="IInputClock"/>, so the window shares the base every input timestamp already uses.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public WorldWheelFeed(WorldPointer pointer, PlayerRoster roster, WorldSeatBindings bindings, WorldCursorFeed cursor, WorldSeatViewports viewports, WheelStore store, Func<InputRouter> router, WorldIconTable icons, IInputClock clock) {
        ArgumentNullException.ThrowIfNull(argument: clock);
        ArgumentNullException.ThrowIfNull(argument: icons);
        ArgumentNullException.ThrowIfNull(argument: pointer);
        ArgumentNullException.ThrowIfNull(argument: roster);
        ArgumentNullException.ThrowIfNull(argument: bindings);
        ArgumentNullException.ThrowIfNull(argument: cursor);
        ArgumentNullException.ThrowIfNull(argument: viewports);
        ArgumentNullException.ThrowIfNull(argument: store);
        ArgumentNullException.ThrowIfNull(argument: router);

        m_bindings = bindings;
        m_clock = clock;
        m_cursor = cursor;
        m_pointer = pointer;
        m_roster = roster;
        m_icons = icons;
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
    private void Arm(int slot, SeatState state, BindingWheelView wheel, BindingWheelRingView ring, int hoverSector, string hoverReason) {
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

        var hovered = ((hoverSector >= 0)
            ? ring.Sectors[hoverSector]
            : null
        );

        if (state.CommitSector != hoverSector) {
            Console.Error.WriteLine(value: $"[player.wheel] hover -> {hoverSector} ({hoverReason}) axisKnown={state.Gesture.AxisKnown} axisNeutral={state.Gesture.AxisNeutral} axis={state.Gesture.Axis} axisSeq={state.Gesture.AxisSequence} pointerSeq={state.PointerSequence}");
        }

        state.CommitArmed = true;
        state.CommitWheel = wheel;
        state.CommitRing = state.ActiveRing;
        state.CommitSector = hoverSector;
        state.CommitActivation = hovered?.Activation;
        state.CommitLabel = (((hovered is not null) ? SectorCell(
            rowReference: wheel.LabelRow,
            slot: slot,
            sector: hovered
        ) : null) ?? (hovered?.Command ?? string.Empty));
        state.CommitReason = hoverReason;
        state.ClosedFrames = 0;
    }
    // A sector's presentation, read from authored state: the wheel names a row (label row, icon row), the sector's
    // own id is the cell key, the cell's value is the text or icon name. The rows are ordinary live state, so
    // renaming or re-iconing a sector on screen is a state write — the binding row itself carries no presentation,
    // only what it does and which sector it is.
    private string? SectorCell(int slot, string? rowReference, BindingWheelSectorView sector) {
        m_bindings.GetRoutedState(
            slot: slot,
            definition: out var definition,
            tick: out var tick
        );

        return (((sector.Id is { Length: > 0 } sectorId) && WorldStateBindingContext.TryParseRowReference(
            reference: rowReference,
            rowName: out var rowName
        ) && WorldStateReader.TryRead(
            definition: definition,
            key: sectorId,
            rawValue: out _,
            row: out _,
            rowName: rowName,
            text: out var text,
            tick: tick
        ))
            ? text
            : null
        );
    }
    private string? HubLabel(int slot, BindingWheelView wheel) {
        m_bindings.GetRoutedState(
            slot: slot,
            definition: out var definition,
            tick: out var tick
        );

        return ((WorldStateBindingContext.TryParseRowReference(
            reference: wheel.LabelRow,
            rowName: out var rowName
        ) && WorldStateReader.TryRead(
            definition: definition,
            key: HubLabelKey,
            rawValue: out _,
            row: out _,
            rowName: rowName,
            text: out var text,
            tick: tick
        ))
            ? text
            : null
        );
    }
    private static void BeginGesture(SeatState state, BindingWheelView wheel) {
        state.GestureWheel = wheel;
        state.Gesture.Open();
        state.Deflection = Vector2.Zero;
        state.DeflectionKnown = false;
        state.PointerSequence = 0L;
        state.GraceSector = -1;
        state.GraceSinceKnown = false;
        state.GraceSinceTick = 0UL;
        // Converted once, here, so the authored seconds never turn into ticks inside the per-frame decision.
        state.GraceTicks = BindingWheelGeometry.SelectionGraceTicks(
            seconds: wheel.Style.SelectionGraceSeconds,
            ticksPerSecond: EngineTicks.PerSecond
        );
        state.ActiveRing = wheel.Style.InitialRing;
        state.AxisExcursionRing = -1;
        state.SpatialExcursionRing = -1;
        state.RingScroll = 0f;
        state.BankedNotches = 0f;
        state.CenterKnown = false;
    }
    private OverlayWheelSeat BuildSeat(int slot, SeatState state, BindingWheelView wheel, in WorldSeatView viewport, int hoverSector, float unit) {
        m_bindings.GetRoutedState(
            slot: slot,
            definition: out var definition,
            tick: out _
        );

        if (
            !ReferenceEquals(
            objA: state.RingCacheSource,
            objB: wheel
        ) ||
            !ReferenceEquals(
                objA: state.RingCacheDefinition,
                objB: definition
            )
        ) {
            var rings = new OverlayWheelRing[wheel.Rings.Count];

            for (var ringIndex = 0; (ringIndex < rings.Length); ringIndex++) {
                var ring = wheel.Rings[ringIndex];
                var sectors = new OverlayWheelSector[ring.Sectors.Count];

                for (var sectorIndex = 0; (sectorIndex < sectors.Length); sectorIndex++) {
                    var sector = ring.Sectors[sectorIndex];

                    sectors[sectorIndex] = new OverlayWheelSector(
                        Icon: m_icons.ResolveIcon(name: SectorCell(
                            rowReference: wheel.IconRow,
                            slot: slot,
                            sector: sector
                        )),
                        Label: (SectorCell(
                            rowReference: wheel.LabelRow,
                            slot: slot,
                            sector: sector
                        ) ?? sector.Command)
                    );
                }

                rings[ringIndex] = new OverlayWheelRing(
                    Label: (ring.Label ?? ring.PageId),
                    Sectors: sectors
                );
            }

            state.RingCache = rings;
            state.RingCacheDefinition = definition;
            state.RingCacheSource = wheel;
        }

        var centerX = (state.CenterKnown
            ? state.Center.X
            : ((viewport.Region.X + (viewport.Region.Width * 0.5f)) * viewport.Width)
        );
        var centerY = (state.CenterKnown
            ? state.Center.Y
            : ((viewport.Region.Y + (viewport.Region.Height * 0.5f)) * viewport.Height)
        );

        return new OverlayWheelSeat(
            Viewport: viewport.Region,
            CenterX: centerX,
            CenterY: centerY,
            InnerRadius: (unit * wheel.Style.DeadZoneFraction),
            RingWidth: (unit * wheel.Style.RingWidthFraction),
            ActiveRing: state.ActiveRing,
            HoveredSector: hoverSector,
            SectorOffset: wheel.Style.SectorOffset,
            Rings: state.RingCache,
            HubLabel: (HubLabel(
                slot: slot,
                wheel: wheel
            ) ?? string.Empty)
        );
    }
    private static void Close(int slot, SeatState state) {
        if (state.Wheel is not null) {
            Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} closed (armed={state.CommitArmed}, sector={state.CommitSector}, '{state.CommitLabel}')");
        }

        state.Wheel = null;
        state.GestureWheel = null;
        state.Gesture.Close();
        state.AxisExcursionRing = -1;
        state.SpatialExcursionRing = -1;
        state.BankedNotches = 0f;

        if (
            state.CommitArmed &&
            (++state.ClosedFrames > CommitGraceFrames)
        ) {
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
        SelectorDeadZone: 0f,
        SwitchFraction: 0f,
        SelectionGraceSeconds: 0f,
        Center: Vector2.Zero,
        CenterKnown: false
    );
    private BindingWheelView? CurrentGestureWheel(int slot, SeatState state) {
        var wheel = m_bindings.WheelView(slot: slot);

        if (
            (wheel is not null) &&
            !ReferenceEquals(
            objA: state.GestureWheel,
            objB: wheel
        )
        ) {
            BeginGesture(
                state: state,
                wheel: wheel
            );
        }

        return wheel;
    }
    private static bool RequiresSpatialNeutral(BindingWheelView wheel) =>
        ((wheel.Style.PointerSelection == BindingWheelSpatialSelectionMode.Angle) ||
            ((wheel.Style.PointerSelection == BindingWheelSpatialSelectionMode.HitTarget) && (wheel.Excursion is not null)));
    private static BindingWheelSelection SelectAxis(SeatState state, BindingWheelView wheel) {
        var vector = state.Gesture.Axis;

        if (wheel.Excursion is not { } excursion) {
            return BindingWheelGeometry.SelectAxis(
                vector: vector,
                sectorCount: wheel.Rings[state.ActiveRing].Sectors.Count,
                style: wheel.Style
            );
        }

        var ring = BindingWheelGeometry.ResolveExcursionRing(
            excursion: excursion,
            previousRing: state.AxisExcursionRing,
            vector: vector
        );

        state.AxisExcursionRing = ring;

        if (ring < 0) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.DeadZone,
                Sector: -1
            );
        }

        state.ActiveRing = ring;

        return BindingWheelGeometry.SelectDirection(
            vector: vector,
            sectorCount: wheel.Rings[ring].Sectors.Count,
            style: wheel.Style
        );
    }
    private static BindingWheelSelection SelectPointer(SeatState state, BindingWheelView wheel, Vector2 pointer, Vector2 center, float unit) {
        var mode = wheel.Style.PointerSelection;
        var targetingVector = BindingWheelGeometry.ResolveSpatialTargetVector(
            mode: mode,
            position: pointer,
            neutral: state.Gesture.SpatialNeutral,
            hub: center
        );

        if (
            (mode == BindingWheelSpatialSelectionMode.Disabled) ||
            (wheel.Excursion is not { } excursion)
        ) {
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
        var normalized = BindingWheelGeometry.NormalizeSpatialExcursion(
            excursion: excursion,
            vector: neutralVector,
            viewportUnit: unit
        );
        var ring = BindingWheelGeometry.ResolveExcursionRing(
            excursion: excursion,
            previousRing: state.SpatialExcursionRing,
            vector: normalized
        );

        state.SpatialExcursionRing = ring;

        if (ring < 0) {
            return new BindingWheelSelection(
                Outcome: BindingWheelSelectionOutcome.DeadZone,
                Sector: -1
            );
        }

        state.ActiveRing = ring;

        return ((mode == BindingWheelSpatialSelectionMode.Angle)
            ? BindingWheelGeometry.SelectDirection(
                vector: normalized,
                sectorCount: wheel.Rings[ring].Sectors.Count,
                style: wheel.Style
            )
            : BindingWheelGeometry.SelectSpatial(
                vector: targetingVector,
                sectorCount: wheel.Rings[ring].Sectors.Count,
                ringCount: wheel.Rings.Count,
                style: wheel.Style,
                mode: mode,
                unit: unit
            )
        );
    }

    /// <summary>Commits the last resolved sector — the <c>player.wheel.commit</c> handler's whole act. A directional
    /// sample that completes before the first presentation frame resolves here from the gesture's retained peak.
    /// When another authored opener still presents the same radial, the release is deferred: the remaining opener
    /// owns the eventual commit.</summary>
    /// <param name="slot">The 0-based seat slot the commit targets.</param>
    /// <returns>The commit disposition, every failure distinguishable from the others.</returns>
    public BindingWheelCommitResult Commit(int slot) {
        if (((uint)slot) >= m_state.Length) {
            return BindingWheelCommitResult.NotArmed();
        }

        var state = m_state[slot];

        Console.Error.WriteLine(value: $"[player.wheel] commit requested (armed={state.CommitArmed}, open={(m_bindings.WheelView(slot: slot) is not null)}, sector={state.CommitSector}, '{state.CommitLabel}')");

        if (state.Gesture.Cancelled) {
            return BindingWheelCommitResult.Cancelled(
                reason: "cancelled",
                ring: state.CommitRing,
                sector: -1
            );
        }

        if (
            !state.CommitArmed &&
            state.Gesture.CanArm &&
            state.Gesture.AxisKnown &&
            (state.GestureWheel is { } gestureWheel) &&
            !ReferenceEquals(
            objA: state.Wheel,
            objB: gestureWheel
        )
        ) {
            var selection = SelectAxis(
                state: state,
                wheel: gestureWheel
            );
            // The same "is there a grace window at all" test the presentation path uses: an authored window shorter
            // than one engine tick is no window, and the two paths must agree on that or a commit could keep a
            // sector the frame before it refused to draw one.
            var cancelAtNeutral = (state.Gesture.AxisNeutral && (state.GraceTicks == 0UL));

            Arm(
                slot: slot,
                state: state,
                wheel: gestureWheel,
                ring: gestureWheel.Rings[state.ActiveRing],
                hoverSector: (cancelAtNeutral
                ? -1
                : selection.Sector),
                hoverReason: (cancelAtNeutral
                ? "dead-center"
                : selection.Reason)
            );
        }

        if (!state.CommitArmed) {
            return BindingWheelCommitResult.NotArmed();
        }

        var currentlyOpen = m_bindings.WheelView(slot: slot);

        if (
            (currentlyOpen is not null) &&
            string.Equals(
            a: currentlyOpen.Id,
            b: state.CommitWheel?.Id,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            return BindingWheelCommitResult.Deferred(
                label: state.CommitLabel,
                ring: state.CommitRing,
                sector: state.CommitSector
            );
        }

        state.CommitArmed = false;

        if (state.CommitActivation is not { } activation) {
            Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} release cancelled ({state.CommitReason})");
            Flash(
                outcome: OverlayWheelOutcome.Cancelled,
                sector: -1,
                state: state
            );

            return BindingWheelCommitResult.Cancelled(
                reason: state.CommitReason,
                ring: state.CommitRing,
                sector: -1
            );
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

        Flash(
            outcome: ((outcome.Status == BindingWheelCommitStatus.Dispatched)
                ? OverlayWheelOutcome.Dispatched
                : OverlayWheelOutcome.Errored),
            sector: state.CommitSector,
            state: state
        );

        return outcome;
    }
    /// <inheritdoc/>
    public void OnPointer(int slot) {
        if (((uint)slot) >= m_state.Length) {
            return;
        }

        var state = m_state[slot];
        var notches = m_pointer.TakeWheel(slot: slot);

        if (CurrentGestureWheel(
            slot: slot,
            state: state
        ) is not null) {
            state.BankedNotches += notches;
        }
    }
    /// <summary>Latches cancellation for the seat's current gesture — both the author-bound
    /// <c>player.wheel.cancel</c> act and the router's synthesized focus-loss cancellation (an alt-tab mid-hold must
    /// never commit a sector). The latch holds until the radial next opens, so no later presentation frame can
    /// re-arm the decision it cleared.</summary>
    /// <param name="slot">The 0-based seat slot the cancellation targets.</param>
    public void Revoke(int slot) {
        if (((uint)slot) < m_state.Length) {
            var state = m_state[slot];

            _ = CurrentGestureWheel(
                slot: slot,
                state: state
            );
            state.Gesture.Cancel();
            state.CommitArmed = false;
            Flash(
                outcome: OverlayWheelOutcome.Cancelled,
                sector: -1,
                state: state
            );
        }
    }
    // Starts the after-close fade; Tick re-emits the last drawn seat with the verdict glow, fading, until it elapses.
    private static void Flash(SeatState state, OverlayWheelOutcome outcome, int sector) {
        state.FlashOutcome = outcome;
        state.FlashSector = sector;
        state.FlashSince = ((state.LastSeatKnown && (state.FlashSeconds > 0f))
            ? System.Diagnostics.Stopwatch.GetTimestamp()
            : 0L
        );
    }
    /// <summary>Accepts an authored Axis2D selection binding for a seat. The open wheel's compiled dead zone admits
    /// a deliberate neutral selection. The retained peak is scoped to one excursion, so repeated flicks remain
    /// responsive while the authored switch threshold prevents an opposite return-spring rebound from beginning
    /// another excursion.</summary>
    public void Select(int slot, Vector2 axis, bool relative = false) {
        if (((uint)slot) >= m_state.Length) {
            return;
        }

        var state = m_state[slot];
        var wheel = CurrentGestureWheel(
            slot: slot,
            state: state
        );

        if (wheel is null) {
            return;
        }

        if (relative) {
            // A delta source accumulates from the open into a virtual pointer offset from the hub — mouse deflection
            // read exactly like a stick's, later than any stick aim by sequence so whichever moved last selects.
            state.Deflection += axis;
            state.DeflectionKnown = true;
            state.PointerSequence = ++m_selectionSequence;

            return;
        }

        _ = state.Gesture.TrySelect(
            axis: axis,
            sequence: ++m_selectionSequence,
            deadZoneSquared: wheel.SelectorDeadZoneSquared,
            switchThresholdSquared: wheel.SelectorSwitchThresholdSquared
        );
    }
    /// <summary>One seat's last composed status.</summary>
    /// <param name="slot">The 0-based seat slot to read.</param>
    /// <returns>That seat's status, or a closed status when the slot is out of range.</returns>
    public WorldWheelStatus StatusFor(int slot) => ((((uint)slot) < m_state.Length)
        ? m_state[slot].Status
        : ClosedStatus(slot: 0)
    );
    /// <summary>Composes every open seat's radial once per produced frame.</summary>
    public void Tick() {
        var pointerSlot = WorldPointerSlot.Resolve(roster: m_roster);
        var pointerStatus = m_cursor.Status;
        var visibleCount = 0;

        for (var slot = 0; (slot < m_state.Length); slot++) {
            var state = m_state[slot];
            var wheel = m_bindings.WheelView(slot: slot);
            var viewport = m_viewports.Seat(slot: slot);

            if (
                (wheel is null) ||
                !viewport.Present
            ) {
                Close(
                    slot: slot,
                    state: state
                );

                // The fade-out: the last drawn wheel, verdict glow on, opacity 1 - t^ease over the authored
                // seconds. Presentation time only — a wall clock here decides nothing the simulation sees.
                if (
                    (state.FlashSince != 0L) &&
                    state.LastSeatKnown &&
                    viewport.Present
                ) {
                    var elapsed = (float)System.Diagnostics.Stopwatch.GetElapsedTime(startingTimestamp: state.FlashSince).TotalSeconds;
                    var t = ((state.FlashSeconds > 0f)
                        ? (elapsed / state.FlashSeconds)
                        : 1f
                    );

                    if (t >= 1f) {
                        state.FlashSince = 0L;
                    } else {
                        m_visible[visibleCount++] = (state.LastSeat with {
                            Fade = (1f - MathF.Pow(
                                x: t,
                                y: state.FlashEase
                            )),
                            // A dispatched piece stays the hub's subject through the fade, so the hub reads what
                            // was handed off; a cancel reads the hub's own label.
                            HoveredSector = ((state.FlashOutcome == OverlayWheelOutcome.Dispatched)
                                ? state.FlashSector
                                : -1),
                            Outcome = state.FlashOutcome,
                            OutcomeSector = state.FlashSector,
                        });
                    }
                }

                continue;
            }

            var viewportCenter = new Vector2(
                x: ((viewport.Region.X + (viewport.Region.Width * 0.5f)) * viewport.Width),
                y: ((viewport.Region.Y + (viewport.Region.Height * 0.5f)) * viewport.Height)
            );
            var pointerAvailable = ((slot == pointerSlot) && (pointerStatus.Reason is not ("no-position" or "no-view")));
            var unit = MathF.Min(
                x: (viewport.Region.Width * viewport.Width),
                y: (viewport.Region.Height * viewport.Height)
            );

            var opened = !ReferenceEquals(
                objA: state.Wheel,
                objB: wheel
            );

            if (opened) {
                Console.Error.WriteLine(value: $"[player.wheel] seat {PlayerRoster.DisplayNumber(slot: slot)} opened '{wheel.Id}' (armed={state.CommitArmed}, carried='{state.CommitLabel}', grace={wheel.Style.SelectionGraceSeconds}s)");

                if (!ReferenceEquals(
                    objA: state.GestureWheel,
                    objB: wheel
                )) {
                    BeginGesture(
                        state: state,
                        wheel: wheel
                    );
                }

                state.Wheel = wheel;
                state.Center = BindingWheelGeometry.ResolveOpeningCenter(
                    placement: wheel.Style.Placement,
                    pointerAvailable: pointerAvailable,
                    pointer: pointerStatus.Frame,
                    viewportCenter: viewportCenter
                );
                state.CenterKnown = true;
            } else if (wheel.Style.RingSelection == BindingWheelRingSelectionMode.Explicit) {
                ApplyRingScroll(
                    state: state,
                    wheel: wheel
                );
            } else {
                state.BankedNotches = 0f;
            }

            // The accumulated relative aim as a virtual pointer: hub + deflection, the deflection mapped from window
            // client pixels into the frame the radial draws in (the per-axis frame/client scale the cursor feed
            // applies). Its neutral is the hub itself, captured at the open.
            var deflectionFrame = state.Deflection;

            if (
                (m_viewports.ClientWidth > 0) &&
                (m_viewports.ClientHeight > 0)
            ) {
                deflectionFrame = new Vector2(
                    x: (state.Deflection.X * (viewport.Width / ((float)m_viewports.ClientWidth))),
                    y: (state.Deflection.Y * (viewport.Height / ((float)m_viewports.ClientHeight)))
                );
            }

            var virtualPointer = (state.Center + deflectionFrame);

            if (
                state.DeflectionKnown &&
                RequiresSpatialNeutral(wheel: wheel)
            ) {
                _ = state.Gesture.TryCaptureSpatialNeutral(position: state.Center);
            }

            var hoverSector = -1;
            var hoverReason = "no-selection";
            var center = state.Center;
            var centerKnown = state.CenterKnown;

            if (state.Gesture.Cancelled) {
                hoverReason = "cancelled";
            } else if (
                state.Gesture.AxisKnown &&
                (state.Gesture.AxisSequence > state.PointerSequence)
            ) {
                var selection = SelectAxis(
                    state: state,
                    wheel: wheel
                );

                if (state.Gesture.AxisNeutral) {
                    if (
                        (state.GraceSector < 0) &&
                        !state.GraceSinceKnown &&
                        (state.GraceTicks > 0UL) &&
                        (selection.Sector >= 0)
                    ) {
                        state.GraceSector = selection.Sector;
                    }

                    hoverSector = -1;
                    hoverReason = "dead-center";
                } else {
                    hoverSector = selection.Sector;
                    hoverReason = selection.Reason;
                }
            } else if (
                state.DeflectionKnown &&
                centerKnown &&
                (!RequiresSpatialNeutral(wheel: wheel) || state.Gesture.SpatialNeutralKnown)
            ) {
                var selection = SelectPointer(
                    center: center,
                    pointer: virtualPointer,
                    state: state,
                    unit: unit,
                    wheel: wheel
                );

                hoverSector = selection.Sector;
                hoverReason = selection.Reason;
            }

            // Selection grace: a highlighted sector survives a dead-center reading for the authored window, and a
            // different sector replaces it only past the switch threshold.
            if (
                (hoverSector >= 0) &&
                (state.GraceSector >= 0) &&
                (hoverSector != state.GraceSector) &&
                state.Gesture.AxisKnown &&
                (state.Gesture.Axis.LengthSquared() < (wheel.Style.SwitchFraction * wheel.Style.SwitchFraction))
            ) {
                hoverSector = state.GraceSector;
                hoverReason = "sector";
            }

            if (hoverSector >= 0) {
                state.GraceSector = hoverSector;
                state.GraceSinceKnown = false;
            } else if (
                (state.GraceSector >= 0) &&
                (hoverReason == "dead-center") &&
                (state.GraceTicks > 0UL)
            ) {
                var now = m_clock.NowTicks;

                if (!state.GraceSinceKnown) {
                    state.GraceSinceKnown = true;
                    state.GraceSinceTick = now;
                }

                // Monotonic clock, so the difference never underflows.
                if ((now - state.GraceSinceTick) <= state.GraceTicks) {
                    hoverSector = state.GraceSector;
                    hoverReason = "sector";
                } else {
                    state.GraceSector = -1;
                }
            } else {
                state.GraceSector = -1;
            }

            var ring = wheel.Rings[state.ActiveRing];

            state.Center = center;
            state.CenterKnown = centerKnown;
            Arm(
                hoverReason: hoverReason,
                hoverSector: hoverSector,
                ring: ring,
                slot: slot,
                state: state,
                wheel: wheel
            );
            var drawn = BuildSeat(
                hoverSector: hoverSector,
                slot: slot,
                state: state,
                unit: unit,
                viewport: in viewport,
                wheel: wheel
            );

            m_visible[visibleCount++] = drawn;
            state.LastSeat = drawn;
            state.LastSeatKnown = true;
            state.FlashSince = 0L;
            state.FlashSeconds = wheel.Style.FadeOutSeconds;
            state.FlashEase = wheel.Style.FadeOutEase;
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
                SelectorDeadZone: MathF.Sqrt(x: wheel.SelectorDeadZoneSquared),
                SwitchFraction: wheel.Style.SwitchFraction,
                SelectionGraceSeconds: wheel.Style.SelectionGraceSeconds,
                Center: center,
                CenterKnown: centerKnown
            );
        }

        m_store.Publish(frame: new OverlayWheelFrame(Seats: m_visible.AsMemory(
            length: visibleCount,
            start: 0
        )));
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

        if (((uint)slot) >= m_state.Length) {
            return false;
        }

        var state = m_state[slot];
        var wheel = CurrentGestureWheel(
            slot: slot,
            state: state
        );

        if (wheel is null) {
            return false;
        }

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
}
