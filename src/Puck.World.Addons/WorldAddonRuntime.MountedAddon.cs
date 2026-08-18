using Puck.Scripting;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World.Addons;

public sealed partial class WorldAddonRuntime {
    // One mounted guest's whole host-side state. Every per-tick buffer is allocated once here, at mount, and reused with
    // a count — a tick allocates nothing on this path.
    private sealed class MountedAddon {
        public MountedAddon(AddonInstance instance, IReadOnlyList<WorldCapabilityRequest>? requests, int populationCapacity, IReadOnlyList<WorldAddonMemoryWatch>? memoryWatches = null) {
            ActBody = new int[AddonAbi.MaxOutCells];
            // A pose answer is the widest response shipping, so the worst case is every output cell being a query.
            Answers = new AddonInCell[(AddonAbi.MaxOutCells * AddonAbi.RequestVerbs.BodyPoseAnswerParts)];
            Batch = new AddonInCell[AddonAbi.MaxInCells];
            Contributions = new BodyContribution[AddonAbi.MaxOutCells];
            DisclosedDrive = [];
            DisclosedObserve = [];
            // Per-body query dispatch count THIS TICK — a query's subject is always a Body (ResolveQueries refuses
            // anything else as a stale handle before it reaches the budget check), so a body-indexed array is the
            // zero-alloc counter. Reset once per tick in StageBatch, beside AnswerCount.
            DispatchCounts = new int[populationCapacity];
            EventCounts = new Dictionary<GrantSubject, int>();
            // The Drive twin of DispatchCounts — per-body act dispatch count THIS TICK, reset once per tick in
            // FoldActs (pump point 2 runs before StageBatch's own reset, which serves pump point 3's Observe meter).
            DriveDispatchCounts = new int[populationCapacity];
            Instance = instance;
            // The host-owned copy destination for stage 5's pointer-safety read — sized to the payload ceiling so
            // one buffer serves every act this guest ever submits, reused tick to tick, never per-act allocated.
            MutationPayloadBuffer = new byte[AddonAbi.MaxMutationPayloadBytes];
            // The RESERVATION buffer: one slot per SubmitMutation act decoded THIS tick, reserved at whole-batch
            // decode time (TickAddons -> ResolveMutations) before EmitDisclosures/MergeAnswers ever see the
            // remaining budget. Sized to MaxOutCells, the same worst case Answers uses, because the ABI handshake
            // already proves a guest's whole batch (every kind of act combined) cannot exceed its declared outCap.
            ReservedAnswers = new AddonInCell[AddonAbi.MaxOutCells];
            Pending = new AddonInCell[AddonAbi.MaxInCells];
            Principal = WorldPrincipal.Addon(name: instance.Name);
            Pump = new AddonSimulationPump();
            Requests = requests;
            ResponseChannel = ResolveResponseChannel(instance: instance);
            MemoryWatches = memoryWatches;
            MemoryWatchState = (((memoryWatches is { Count: > 0 })
                ? new WatchState[memoryWatches.Count]
                : null));
        }

        /// <summary>One machine-memory watch's own state: whether a baseline value has been observed yet (the first
        /// successful peek establishes it without emitting an edge — there is no "previous" to compare against), and
        /// the last observed value.</summary>
        public readonly record struct WatchState(bool Initialized, long Value);

        /// <summary>Gets the body each staged act resolved to, by act index, or <see cref="NoBody"/>.</summary>
        public int[] ActBody { get; }
        public int AnswerCount { get; set; }
        /// <summary>Gets this tick's unsorted, unbudgeted answer cells.</summary>
        public AddonInCell[] Answers { get; }
        /// <summary>Gets the composed input batch handed to <c>puck_on_tick</c>.</summary>
        public AddonInCell[] Batch { get; }
        /// <summary>Gets the collision begin/end cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong CollisionEventsDelivered { get; set; }
        public int ContributionCount { get; set; }
        /// <summary>Gets the per-body folded contributions, kept sorted ascending by body index.</summary>
        public BodyContribution[] Contributions { get; }
        public bool Disclosed { get; set; }
        public GrantSubject[] DisclosedDrive { get; set; }
        // The instance Generation (AddonInstance.Generation) the disclosure above was computed against. A
        // disable/enable cycle re-instantiates the SAME AddonInstance object in place (Puck.Scripting.AddonHost
        // reuses it; only AddonHost.Reload swaps the object), so DisclosedRevision alone cannot tell a live,
        // still-known guest apart from one whose linear memory was just wiped. -1 so the very first resolve always
        // projects.
        public int DisclosedGeneration { get; set; } = -1;
        public GrantSubject[] DisclosedObserve { get; set; }
        // -1 so the very first resolve always projects, even against a fresh table whose own Revision starts at 0.
        public int DisclosedRevision { get; set; } = -1;
        public bool DisclosureOverflowReported { get; set; }
        public bool DiscrepancyReported { get; set; }
        // Latches the once-per-episode QuotaExhausted host line separately from every other Reported flag above —
        // this one names a per-row budget and its offending subject, which none of the shared-latch discrepancy
        // lines do, so it earns its own gate rather than silently sharing (and starving) DiscrepancyReported's.
        public bool DispatchBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's per-body Observe query dispatch count, indexed by body index — the meter
        /// <see cref="WorldGrants.TryGetBudget"/>'s per-row budget is charged against. Cleared once per tick in
        /// <c>StageBatch</c>, the same sweep that resets <see cref="AnswerCount"/>.</summary>
        public int[] DispatchCounts { get; }
        // The Drive twins of DispatchBudgetExhaustedReported/MissingBudgetReported, beside them for the same reason
        // (a shared latch with the Observe pair would let one capability's line starve the other's).
        public bool DriveDispatchBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's per-body Drive act dispatch count, indexed by body index — the Drive twin of
        /// <see cref="DispatchCounts"/>. Cleared once per tick in <c>FoldActs</c>, since pump point 2 runs before
        /// <c>StageBatch</c>'s own reset.</summary>
        public int[] DriveDispatchCounts { get; }
        public bool DriveMissingBudgetReported { get; set; }
        /// <summary>Gets the world-event and memory-watch cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong EventCellsDelivered { get; set; }
        /// <summary>Gets this tick's event-cell charge count by Observe subject. Cleared once per tick in
        /// <c>StageBatch</c>.</summary>
        public Dictionary<GrantSubject, int> EventCounts { get; }
        /// <summary>Gets this tick's lifetime event-gap counter (the overflow doctrine's per-mount summary — see
        /// <c>Scripting.AddonAbi.ObservationVerbs.EventGap</c>'s own doc) — saturating, never reset. Every edge
        /// dropped because its per-row event budget or the ring ran out of room (world events or a memory-watch change
        /// alike) adds here.</summary>
        public ulong EventGapCount { get; set; }
        public bool FaultReported { get; set; }
        /// <summary>Gets a value indicating whether an admitted execution of this guest has ever been attempted — set unconditionally the
        /// first time <see cref="TickAddons"/> reaches the point of driving it (regardless of whether the tick
        /// traps), never cleared. The boot-anchored replay arm predicate: offline replay
        /// creates fresh guests at sim-counter zero, so a guest's accumulated memory/tick state before this latch
        /// was set is exactly what a recording begun after it cannot re-establish.</summary>
        public bool HasEverPumped { get; set; }
        /// <summary>Gets the guest instance.</summary>
        public AddonInstance Instance { get; }
        /// <summary>Gets the <see cref="EventGapCount"/> value last actually staged into a batch — an <c>EventGap</c>
        /// cell is only worth re-emitting when the count has moved since the last one that fit.</summary>
        public ulong LastReportedEventGap { get; set; }
        // The cost surface: fuel consumed is measured every tick. LastTickFuelConsumed is this tick's spend (0 on a
        // tick the guest did not run — faulted, disabled, or skipped enabled-but-unadmitted); TotalFuelConsumed is a
        // LIFETIME figure for this guest's name, since it was first mounted — SetEnabled never touches this object
        // (a disable/enable mutates the same Instance in place), and Reload's wholesale MountedAddon replacement
        // carries the prior value forward explicitly rather than re-zeroing it. DIAGNOSTIC ONLY: read by the
        // world.addons verb, never by simulation state and never on a hashed path. Saturates at ulong.MaxValue
        // rather than wrapping.
        public ulong LastTickFuelConsumed { get; set; }
        /// <summary>Gets the per-watch last-observed state, parallel to <see cref="MemoryWatches"/> by index; null when the
        /// row declares no watches.</summary>
        public WatchState[]? MemoryWatchState { get; }
        /// <summary>Gets the row's machine-memory watch declarations (the fifth event family — addon-scoped, unlike the
        /// other four; see <see cref="WorldEventFeed"/>'s own remarks). Null/empty means none.</summary>
        public IReadOnlyList<WorldAddonMemoryWatch>? MemoryWatches { get; }
        // Mirrors DispatchBudgetExhaustedReported for the SAME reason, beside it: the missing-budget host line in
        // ResolveQueries names a per-row subject too, so it gets its own latch rather than sharing (and risking
        // starvation from) DiscrepancyReported's.
        public bool MissingBudgetReported { get; set; }
        public bool MutateByteBudgetExhaustedReported { get; set; }
        /// <summary>Gets this tick's running mutation-payload byte total for this addon, metered against
        /// <see cref="AddonAbi.MaxMutationBytesPerTickPerAddon"/>.</summary>
        public int MutateBytesThisTick { get; set; }
        // The Mutate twins of DispatchBudgetExhaustedReported/MissingBudgetReported, beside them for the identical
        // reason (a shared latch across capabilities would let one starve another's line).
        public bool MutateDispatchBudgetExhaustedReported { get; set; }
        public bool MutateMissingBudgetReported { get; set; }
        /// <summary>Gets the host-owned scratch buffer stage 5's pointer-safety copy reads a <c>SubmitMutation</c>
        /// payload into, reused every act and every tick.</summary>
        public byte[] MutationPayloadBuffer { get; }
        // The Generation twin of OverflowedAtRevision, for the identical reason DisclosedGeneration exists beside
        // DisclosedRevision — a fresh store still deserves one honest attempt to fit, even if the previous
        // (now-wiped) instance last overflowed at the same grant-table revision.
        public int OverflowedAtGeneration { get; set; } = -1;
        // The revision an oversized disclosure set overflowed at — re-projection waits for the next grant-table
        // write, the only event that can shrink the set (-1 = never overflowed).
        public int OverflowedAtRevision { get; set; } = -1;
        /// <summary>Gets the cells staged for the next tick's batch (disclosures, then answers).</summary>
        public AddonInCell[] Pending { get; }
        public int PendingCount { get; set; }
        /// <summary>Gets the mount-bound acting identity — never carried on a record.</summary>
        public WorldPrincipal Principal { get; }
        /// <summary>Gets the adapter crossing that drives this guest and validates its vocabulary.</summary>
        public AddonSimulationPump Pump { get; }
        public bool QuotaDropReported { get; set; }
        /// <summary>Gets the row's manifest — the left half of requests ∧ grants. Null means the row asked for nothing, so
        /// nothing materializes.</summary>
        public IReadOnlyList<WorldCapabilityRequest>? Requests { get; }
        /// <summary>Gets this tick's reserved answer cells — one per <c>SubmitMutation</c> act
        /// decoded this tick, reserved at whole-batch decode time before <c>EmitDisclosures</c>/<c>MergeAnswers</c>
        /// ever see the remaining budget. <see cref="ResolveMutations"/> sets each slot's <c>Verdict</c> the
        /// moment it is known (synchronously for a stage 1-5 refusal; later, via <c>CompleteMutation</c>, for an
        /// act that reached the pending queue) — <see cref="AddonVerdict.None"/> is the "still pending" sentinel
        /// between decode and drain within the same Step.</summary>
        public AddonInCell[] ReservedAnswers { get; }
        /// <summary>Gets how many of <see cref="ReservedAnswers"/> are live this tick.</summary>
        public int ReservedCount { get; set; }
        /// <summary>Gets the guest's declared Response channel index, or <c>-1</c> when it declares none.</summary>
        public int ResponseChannel { get; }
        /// <summary>Gets the route-engaged/disengaged cells delivered across this mount's lifetime; diagnostic only.</summary>
        public ulong RouteEventsDelivered { get; set; }
        public bool StaleHandleReported { get; set; }
        // A LIFETIME count of answer groups MergeAnswers found no cell for at all (the ring's own hard ceiling —
        // never recoverable by a bigger squeeze), the same saturating-ulong shape as TotalFuelConsumed and for the
        // identical reason: the stderr line QuotaDropReported gates is per-episode and scrolls away, so without a
        // durable counter an operator who was not watching the console at that instant has no way to learn this ever
        // happened. DIAGNOSTIC ONLY, read by world.addons — never simulation state, never on a hashed path.
        public ulong TotalAnswersDropped { get; set; }
        public ulong TotalFuelConsumed { get; set; }
        public bool UndeliverableReported { get; set; }
        public bool UnrequestedActReported { get; set; }
    }
}
