using Puck.Maths;
using Puck.Networking;
using Puck.Physics.Navigation;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>The slots one continuation hash segment holds: the population's continuation digest folds one digest per
    /// segment of this many consecutive entity indices, each over its active slots' own digests.</summary>
    /// <remarks>It is the parallel tick's body partition, so a partitioned fold takes whole segments.</remarks>
    public const int HashSegmentSlots = 32;

    /// <summary>Folds the population's simulation continuation, excluding the rendered look, so state the pose hash
    /// does not read (a velocity, an integration remainder, a channel timer) still separates two runs. Called where
    /// <see cref="Capture"/> may be, between a completed step and the next; the authoritative hash takes it on every
    /// tick a replay records or a history captures. Each active slot's digest folds the digests of its sections
    /// (<see cref="WorldPopulationEntryCheckpoint.VisitSections"/>), each section encoded by its own static encoder
    /// from a view of the live slot; every slot folds into its segment of <see cref="HashSegmentSlots"/>, and the
    /// segments into the hash. What no slot digest holds is folded beside the segments, read afresh on every fold:
    /// the resting bodies' contact-field observations and each profiled slot's projection, by content. The register
    /// values are the arena's, whose own component folds them. The fold writes into scratch the population reuses,
    /// so it allocates nothing once that scratch has grown to the population's shape.</summary>
    /// <param name="hash">The hash to fold into.</param>
    /// <param name="recompute">Whether to encode every section afresh and read no holder's cached digest: the fold a
    /// cached one is checked against.</param>
    /// <remarks>A section a body keeps behind a holder (<see cref="WorldBody.HeldDigests"/>) folds by the digest its
    /// holder hands out, which is cached while the section is unchanged. The larger sections the step writes directly
    /// fold through the slot's <see cref="WorldContinuationMemo"/>, which encodes one only when its content differs
    /// from the content its cached digest was taken from; the rest encode afresh on every fold. A recompute reads
    /// neither cache.</remarks>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public void AppendContinuationHash(ref Fnv1aHash hash, bool recompute = false) {
        RequireCapturePoint();
        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.ContinuationFolds);
        _ = m_slotNotes.Publish();

        var digests = ((m_foldDigests.Length == Capacity) ? m_foldDigests : (m_foldDigests = new ulong[Capacity]));
        var present = ((m_foldPresent.Length == Capacity) ? m_foldPresent : (m_foldPresent = new bool[Capacity]));
        var writer = (m_continuationWriter ??= new WireWriter());
        var work = (m_foldWork ??= new WorldContinuationFoldWork());
        var beside = WorldAuthorityCheckpointCodec.ContinuationBeside.Create();

        WorldTickWork.Process.Add(amount: Capacity, kind: WorldTickWork.ContinuationSlots);
        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            present[index] = (entry is { Active: true, Body: not null });
            digests[index] = 0UL;
            if (entry is { Active: true, Body: { } body }) {
                var held = (recompute ? 0U : body.HeldDigests(digests: work.HeldDigests, work: work.Held, writer: writer));

                EntryCheckpoint(alias: true, body: body, entry: entry, held: (held != 0U), image: out var image, index: index);
                if (!recompute) { held |= FoldMemo(index: index).Digest(checkedNotes: m_slotNotes, digests: work.HeldDigests, image: ref image, work: work.Held, writer: writer); }
                digests[index] = WorldAuthorityCheckpointCodec.ContinuationSlotDigest(entry: ref image, held: held, heldDigests: work.HeldDigests, work: work, writer: writer);
                beside.Add(entry: in image, work: work, writer: writer);
                work.Slots++;
            }
        }
        WorldAuthorityCheckpointCodec.FoldContinuationSegments(digests: digests, hash: ref hash, present: present);
        beside.Fold(hash: ref hash);
        work.Publish();
    }
    /// <summary>Folds the simulation continuation of the named slots that hold an active body: their count, then, in
    /// the order given, each one's slot digest (the fold of its sections' digests, as
    /// <see cref="AppendContinuationHash(ref Fnv1aHash, bool)"/> takes it) and the digest of its tail, encoded afresh. A
    /// scope compares one body's whole image and not the arena or the population, so its fold reads the register
    /// values, the profile and the body's own contact-field observation with the body. The fold visits the named
    /// slots alone, never the rest of the population.</summary>
    /// <param name="hash">The hash to fold into.</param>
    /// <param name="bodies">The slots to fold, ascending.</param>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    [System.Runtime.CompilerServices.SkipLocalsInit]
    public void AppendContinuationHash(ref Fnv1aHash hash, ReadOnlySpan<int> bodies) {
        RequireCapturePoint();
        WorldTickWork.Process.Add(amount: 1L, kind: WorldTickWork.ContinuationFolds);
        WorldTickWork.Process.Add(amount: bodies.Length, kind: WorldTickWork.ContinuationSlots);

        var count = 0;
        var writer = (m_continuationWriter ??= new WireWriter());
        var work = (m_foldWork ??= new WorldContinuationFoldWork());

        foreach (var index in bodies) {
            if ((((uint)index) < ((uint)Capacity)) && (m_entries[index] is { Active: true, Body: not null })) { count++; }
        }
        hash.Add(value: ((uint)count));
        foreach (var index in bodies) {
            if ((((uint)index) < ((uint)Capacity)) && (m_entries[index] is { Active: true, Body: { } body } entry)) {
                var held = body.HeldDigests(digests: work.HeldDigests, work: work.Held, writer: writer);
                var memo = FoldMemo(index: index);

                EntryCheckpoint(alias: true, body: body, entry: entry, held: (held != 0U), image: out var image, index: index);
                held |= memo.Digest(checkedNotes: m_slotNotes, digests: work.HeldDigests, image: ref image, work: work.Held, writer: writer);
                hash.Add(value: WorldAuthorityCheckpointCodec.ContinuationSlotDigest(entry: ref image, held: held, heldDigests: work.HeldDigests, work: work, writer: writer));
                hash.Add(value: memo.DigestTail(checkedNotes: m_slotNotes, slot: index, tail: in image.Tail, work: work, writer: writer));
                work.Slots++;
            }
        }
        work.Publish();
    }

    // Which slots were written since the last fold: every body's holders note through a binding to these. The fold
    // publishes them; nothing follows the versions yet.
    private readonly SlotNotes m_slotNotes;

    /// <summary>Gets the notes the bodies' state holders note their writes in.</summary>
    internal SlotNotes SlotNotes => m_slotNotes;

    // The fold's per-slot scratch and its work counts, reused from one fold to the next.
    private ulong[] m_foldDigests = [];
    private bool[] m_foldPresent = [];
    private WorldContinuationFoldWork? m_foldWork;
    // Each slot's digests of the sections no body holder keeps, cached over equal content (WorldContinuationMemo).
    private WorldContinuationMemo?[] m_foldMemos = [];

    // The slot's memo, made on its first fold. A memo answers by content alone, so it serves a slot's next occupant too.
    private WorldContinuationMemo FoldMemo(int index) {
        if (m_foldMemos.Length < Capacity) { Array.Resize(array: ref m_foldMemos, newSize: Capacity); }
        return (m_foldMemos[index] ??= new WorldContinuationMemo());
    }

    /// <summary>Captures every active slot's simulation state. Asserts the per-tick pending-output lists are empty —
    /// guaranteed by <see cref="WorldServer.TryCaptureCheckpoint"/>'s capture point sitting between a completed
    /// <c>Step</c> and the next, never inside one.</summary>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    public WorldPopulationCheckpoint Capture() =>
        new(
            Entries: CaptureEntries(),
            Generations: m_entries.Select(selector: entry => entry.Generation).ToArray(),
            EventBodies: m_eventBodies.ToArray(),
            SleepingGravity: [.. m_sleepGravity.OrderBy(keySelector: static pair => pair.Key).Select(selector: static pair => (pair.Key, pair.Value))],
            AttachedSolids: MovingSolids?.CaptureAttached(),
            Revision: m_revision,
            SeatKit: m_seatKit,
            SimulatedCount: m_simulatedCount
        );

    // Every active slot's checkpoint entry, in slot order: the part of a population checkpoint a body owns.
    private List<WorldPopulationEntryCheckpoint> CaptureEntries() {
        RequireCapturePoint();

        var entries = new List<WorldPopulationEntryCheckpoint>(capacity: Capacity);

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (entry is { Active: true, Body: { } body }) {
                EntryCheckpoint(alias: false, body: body, entry: entry, held: false, image: out var image, index: index);
                entries.Add(item: image);
            }
        }

        return entries;
    }
    /// <summary>Captures the checkpoint entries of the named slots only, in the order given, as <see cref="Capture"/>
    /// would hold them. The work is the named slots', never the whole population's.</summary>
    /// <param name="indices">The slots to capture.</param>
    /// <param name="entries">The entries, one for each slot, when every slot is active.</param>
    /// <returns><see langword="false"/>, capturing nothing, when a named slot holds no active body.</returns>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    public bool TryCaptureEntries(ReadOnlySpan<int> indices, out WorldPopulationEntryCheckpoint[] entries) {
        RequireCapturePoint();
        foreach (var index in indices) {
            if (((uint)index >= (uint)Capacity) || (m_entries[index] is not { Active: true, Body: not null })) {
                entries = [];
                return false;
            }
        }
        entries = new WorldPopulationEntryCheckpoint[indices.Length];
        for (var position = 0; (position < indices.Length); position++) {
            var index = indices[position];
            var entry = m_entries[index];

            EntryCheckpoint(alias: false, body: entry.Body!, entry: entry, held: false, image: out entries[position], index: index);
        }
        return true;
    }
    /// <summary>Captures the exact continuation of every remote human's own body after a tick, each named by the owner
    /// the authority answers that body's input under, for the owner's prediction of it. Allocates nothing when no remote
    /// human stands in the world, and captures nothing between a step and its completion.</summary>
    /// <param name="tick">The completed tick the continuations are taken after.</param>
    /// <param name="engineTick">The engine-tick boundary <paramref name="tick"/> completed at.</param>
    /// <returns>One continuation a remote human, in slot order.</returns>
    public Puck.World.Protocol.WorldOwnBody[] CaptureOwnBodies(ulong tick, ulong engineTick) {
        if ((CountRemoteHumans() == 0) || !m_outputs.IsEmpty || (m_durableStateOutputs.Count != 0)) {
            return [];
        }

        var bodies = new List<Puck.World.Protocol.WorldOwnBody>();
        WorldAttachedSolidCheckpoint[]? attached = null;

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (entry is not { Active: true, IsRemoteHuman: true, Parked: false, Body: { } body }) {
                continue;
            }

            EntryCheckpoint(alias: false, body: body, entry: entry, held: false, image: out var image, index: index);
            attached ??= (MovingSolids?.CaptureAttached() ?? []);
            bodies.Add(item: new(
                Body: index,
                EngineTick: engineTick,
                Entry: WorldAuthorityCheckpointCodec.EncodeOwnBody(attached: attached, entry: in image),
                Generation: entry.Generation,
                Owner: PeerPrincipal(index: index),
                Tick: tick
            ));
        }

        return [.. bodies];
    }
    private void RequireCapturePoint() {
        if (
            (!m_outputs.IsEmpty) ||
            (m_durableStateOutputs.Count != 0)
        ) {
            throw new InvalidOperationException(message: "a population checkpoint requires every pending-output list to be empty — capture only between a completed Step and the next StepInstances.");
        }
    }
    // The one statement of what a slot's checkpoint entry holds: the body's sections, the entry's sections and the
    // tail. A fold's view may leave out the sections the body's holders digest (held). A capture copies every table; a view aliases the slot's live tables and assembles the rest into scratch
    // the body and the population reuse, valid until the next view.
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void EntryCheckpoint(int index, Entry entry, WorldBody body, bool alias, bool held, out WorldPopulationEntryCheckpoint image) {
        IReadOnlyList<(WorldCapability, GrantSubject)> revokedKeys;

        if (alias && (entry.AdmissionRevokedKeys.Count == 0)) {
            revokedKeys = [];
        } else {
            List<(WorldCapability, GrantSubject)> sorted;

            if (alias) {
                (sorted = (m_viewRevokedKeys ??= [])).Clear();
            } else {
                sorted = new List<(WorldCapability, GrantSubject)>(capacity: entry.AdmissionRevokedKeys.Count);
            }

            foreach (var key in entry.AdmissionRevokedKeys) {
                sorted.Add(item: (key.Capability, key.Subject));
            }
            sorted.Sort(comparison: static (left, right) => {
                var capability = Comparer<WorldCapability>.Default.Compare(x: left.Item1, y: right.Item1);

                return ((capability != 0) ? capability : WorldGrants.CompareSubjects(a: left.Item2, b: right.Item2));
            });
            revokedKeys = sorted;
        }

        image.Index = index;
        body.CaptureSections(alias: alias, held: held, registers: out var registers, sections: out image.Body);
        image.Admission = new(
            AdmissionTrust: entry.AdmissionTrust,
            AdmissionTier: entry.AdmissionTier,
            Generation: entry.Generation,
            IdentityDomain: entry.IdentityDomain,
            IdentitySubject: entry.IdentitySubject,
            InstalledGrantTemplates: (alias
                ? entry.AdmissionInstalledGrantTemplates
                : [.. entry.AdmissionInstalledGrantTemplates]),
            IsAuthorityTransferred: entry.IsAuthorityTransferred,
            IsRemoteHuman: entry.IsRemoteHuman,
            KitIndex: entry.KitIndex,
            Mobility: entry.Mobility,
            MobilityGeneration: entry.MobilityGeneration,
            Parked: entry.Parked,
            ParkedUntilTick: entry.ParkedUntilTick,
            PlacementId: entry.PlacementId,
            RevokedKeys: revokedKeys,
            SelectedKit: entry.SelectedKit,
            SpawnPosition: entry.SpawnPosition,
            SpawnYaw: entry.SpawnYaw,
            Titled: entry.Titled,
            TravelTurn: entry.TravelTurn
        );
        image.Designations = new(Targets: (alias
            ? entry.Designations
            : entry.Designations.AsSpan().ToArray()));
        if (held) {
            System.Runtime.CompilerServices.Unsafe.SkipInit(value: out image.Look);
        } else {
            image.Look = new(
                BodyColor: entry.BodyColor,
                CatalogRig: entry.CatalogRig,
                SelectedLook: entry.SelectedLook
            );
        }
        image.Producer = new(
            AcquiredTarget: entry.ProducerState.AcquiredTarget,
            ActiveCurveIndex: entry.ProducerState.ActiveProducerCurveIndex,
            ActiveName: entry.ProducerState.ActiveProducerName,
            ActiveNavigationDomainIndex: entry.ProducerState.ActiveProducerNavigationDomainIndex,
            ActivityPhase: entry.ProducerState.ActivityPhase,
            ActivityPhaseRemainder: entry.ProducerState.ActivityPhaseRemainder,
            ActivityRate: entry.ProducerState.ActivityRate,
            CurveArcRaw: entry.ProducerState.CurveArcRaw,
            CurveArcRemainder: entry.ProducerState.CurveArcRemainder,
            Flock: new WorldPopulationFlockCheckpoint(
                Desired: entry.ProducerState.FlockDesired,
                Generation: entry.ProducerState.FlockGeneration,
                RemainingTicks: entry.ProducerState.FlockRemainingTicks,
                SampleOrdinal: entry.ProducerState.FlockSampleOrdinal,
                Seeded: entry.ProducerState.FlockSeeded,
                Target: entry.ProducerState.FlockTarget
            ),
            Phase: entry.ProducerState.Phase,
            PhaseRemainder: entry.ProducerState.PhaseRemainder,
            PreferredAltitude: entry.ProducerState.PreferredAltitude,
            WeaveFrequency: entry.ProducerState.WeaveFrequency,
            StandDown: entry.ProducerState.StandDown
        );
        image.Steering = new(
            Intent: entry.AutonomyState.SteeringIntent,
            Seeded: entry.AutonomyState.SteeringSeeded
        );
        image.Cadence = new(
            MotionElapsedTicks: entry.AutonomyState.MotionElapsedTicks,
            MotionPeriodTicks: entry.AutonomyState.MotionPeriodTicks,
            MotionRemainingTicks: entry.AutonomyState.MotionRemainingTicks,
            SteeringElapsedTicks: entry.AutonomyState.SteeringElapsedTicks,
            SteeringPeriodTicks: entry.AutonomyState.SteeringPeriodTicks,
            SteeringRemainingTicks: entry.AutonomyState.SteeringRemainingTicks
        );
        image.Navigation = new(
            DomainIndex: entry.NavigationState.DomainIndex,
            ExpandedLast: entry.NavigationState.ExpandedLast,
            GoalCell: entry.NavigationState.GoalCell,
            Status: entry.NavigationState.Status,
            Waypoint: entry.NavigationState.Waypoint
        );

        var path = entry.NavigationState.Path.AsMemory(length: entry.NavigationState.PathLength, start: 0);

        image.Route = new(Path: (alias ? path : path.ToArray()));
        // Only a parked rigid body or an asleep one compares its observation with the field, to decide whether to wake;
        // an awake body overwrites it (ParkRigidIsland, UpdateSleepEligibility) before any read. Its observation is
        // current by construction, so the image never carries a stale stamp that would tie a body's continuation to
        // field changes elsewhere in the world (a scoped replay compares that image across drives). The parked rigid
        // bodies observe the field together, through the population's own observation, which a bounded change that
        // reaches none of them advances without visiting any. The versions compared are process-local rebuild
        // counters, so the image keeps only whether they agreed.
        image.Tail = new(
            ContactFieldObservationCurrent: ((image.Body.Clocks.RigidSleepRootPlusOne != 0)
                ? (m_sleepFieldVersion == ContactFieldVersion)
                : ((image.Body.Clocks.AsleepSinceTick == 0UL) || (body.LastContactFieldVersion == ContactFieldVersion))),
            Profile: body.Profile?.Project(),
            Registers: registers
        );
    }

    /// <summary>Restores every entity-table slot from a previously captured checkpoint. Every slot is cleared first —
    /// this replaces the live table wholesale rather than merging onto it. A captured entry that is
    /// <see cref="Entry.IsRemoteHuman"/> and not already <see cref="Entry.Parked"/> is parked as of
    /// <paramref name="tick"/>, exactly as <see cref="ApplyPeerDisconnected"/> parks a live disconnect — the
    /// connection that occupied it does not survive a restore (<see cref="WorldOutputHub"/>/<see cref="WorldPeerHost"/>
    /// state is excluded from the checkpoint). A validated captured federation continuation instead preserves its
    /// logical input episode; its socket lease is not restored, and a new attachment resets that episode. Only the BODY parks;
    /// the generation's grant rows are released by <c>WorldServer.RestoreCheckpoint</c> right after the grant table
    /// restores, on the same argument the disconnect arm applies.</summary>
    /// <param name="checkpoint">The captured state to restore.</param>
    /// <param name="defaults">The player defaults a restored profiled body's identity resolves colors against.</param>
    /// <param name="tick">The restored current tick — the basis a freshly parked <see cref="Entry.ParkedUntilTick"/>
    /// is stamped from.</param>
    /// <param name="continuingFederatedBodies">Validated body indices whose captured federation input resumes.</param>
    public void Restore(WorldPopulationCheckpoint checkpoint, WorldPlayerDefaults defaults, ulong tick, IReadOnlySet<int>? continuingFederatedBodies = null) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        ArgumentNullException.ThrowIfNull(argument: defaults);

        ValidateCheckpoint(checkpoint: checkpoint);
        if (MovingSolids is { } attached) { attached.RestoreAttached(rows: (checkpoint.AttachedSolids ?? [])); }

        // Restore is replacement. All caller-controlled entry and route addresses were preflighted above, before
        // this destructive phase starts.
        m_activeCarryCount = 0;
        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            entry.Active = false;
            entry.Generation = checkpoint.Generations[index];
            entry.ProducerState = new BodyProducerState {
                AcquiredTarget = -1,
                ActiveProducerCurveIndex = -1,
                ActiveProducerNavigationDomainIndex = -1,
            };
            entry.AutonomyState.Clear();
            entry.Body = null;
            entry.Parked = false;
            entry.ParkedUntilTick = null;
            entry.IsRemoteHuman = false;
            entry.IsAuthorityTransferred = false;
            entry.PlacementId = null;
            entry.Titled = false;
            entry.Mobility = null;
            entry.MobilityGeneration = 0;
            entry.AdmissionInstalledGrantTemplates = [];
            entry.AdmissionRevokedKeys = [];
            entry.IdentityDomain = string.Empty;
            entry.IdentitySubject = string.Empty;
            entry.AdmissionTrust = null;
            entry.NavigationState.Clear();
            ClearDesignations(entry: entry);
        }

        foreach (var captured in checkpoint.Entries) {
            var entry = m_entries[captured.Index];

            RestoreEntryBody(captured: in captured, defaults: defaults, index: captured.Index, seatKit: checkpoint.SeatKit);

            var admission = captured.Admission;
            var producer = captured.Producer;
            var navigation = captured.Navigation;
            var route = captured.Route.Path.Span;

            captured.Designations.Targets.Span[..Math.Min(val1: captured.Designations.Targets.Length, val2: entry.Designations.Length)].CopyTo(destination: entry.Designations);
            entry.KitIndex = admission.KitIndex;
            entry.SelectedKit = admission.SelectedKit;
            entry.SelectedLook = captured.Look.SelectedLook;
            entry.BodyColor = captured.Look.BodyColor;
            entry.CatalogRig = captured.Look.CatalogRig;
            entry.TravelTurn = admission.TravelTurn;
            entry.Generation = admission.Generation;
            entry.IsAuthorityTransferred = admission.IsAuthorityTransferred;
            entry.IsRemoteHuman = admission.IsRemoteHuman;
            entry.Mobility = admission.Mobility;
            entry.MobilityGeneration = admission.MobilityGeneration;
            entry.Parked = admission.Parked;
            entry.ParkedUntilTick = admission.ParkedUntilTick;
            entry.PlacementId = admission.PlacementId;
            entry.Titled = admission.Titled;
            entry.SpawnPosition = admission.SpawnPosition;
            entry.SpawnYaw = admission.SpawnYaw;
            entry.AdmissionInstalledGrantTemplates = admission.InstalledGrantTemplates;
            entry.AdmissionRevokedKeys = [.. admission.RevokedKeys.Select(selector: static key => (key.Capability, key.Subject))];
            entry.IdentityDomain = admission.IdentityDomain;
            entry.IdentitySubject = admission.IdentitySubject;
            entry.AdmissionTrust = admission.AdmissionTrust;
            entry.AdmissionTier = admission.AdmissionTier;
            entry.ProducerState = new BodyProducerState {
                AcquiredTarget = producer.AcquiredTarget,
                ActiveProducerCurveIndex = producer.ActiveCurveIndex,
                ActiveProducerName = producer.ActiveName,
                ActiveProducerNavigationDomainIndex = producer.ActiveNavigationDomainIndex,
                ActivityPhase = producer.ActivityPhase,
                ActivityPhaseRemainder = producer.ActivityPhaseRemainder,
                ActivityRate = producer.ActivityRate,
                CurveArcRaw = producer.CurveArcRaw,
                CurveArcRemainder = producer.CurveArcRemainder,
                FlockDesired = producer.Flock.Desired,
                FlockGeneration = producer.Flock.Generation,
                FlockRemainingTicks = producer.Flock.RemainingTicks,
                FlockSampleOrdinal = producer.Flock.SampleOrdinal,
                FlockSeeded = producer.Flock.Seeded,
                FlockTarget = producer.Flock.Target,
                Phase = producer.Phase,
                PhaseRemainder = producer.PhaseRemainder,
                PreferredAltitude = producer.PreferredAltitude,
                StandDown = producer.StandDown,
                WeaveFrequency = producer.WeaveFrequency,
            };
            entry.AutonomyState = new BodyAutonomyState {
                MotionElapsedTicks = captured.Cadence.MotionElapsedTicks,
                MotionPeriodTicks = captured.Cadence.MotionPeriodTicks,
                MotionRemainingTicks = captured.Cadence.MotionRemainingTicks,
                SteeringElapsedTicks = captured.Cadence.SteeringElapsedTicks,
                SteeringIntent = captured.Steering.Intent,
                SteeringPeriodTicks = captured.Cadence.SteeringPeriodTicks,
                SteeringRemainingTicks = captured.Cadence.SteeringRemainingTicks,
                SteeringSeeded = captured.Steering.Seeded,
            };
            if (
                (producer.ActiveName is { } activeName) &&
                m_kits[CapturedKitIndex(captured: captured, seatKit: checkpoint.SeatKit)].Producers.TryGetValue(
                key: activeName,
                value: out var binding
            )
            ) {
                entry.ProducerState.FlockBinding = binding;
            }
            entry.NavigationState.DomainIndex = navigation.DomainIndex;
            entry.NavigationState.GoalCell = navigation.GoalCell;
            entry.NavigationState.Waypoint = navigation.Waypoint;
            entry.NavigationState.ExpandedLast = navigation.ExpandedLast;
            entry.NavigationState.Status = navigation.Status;
            entry.NavigationState.PathLength = route.Length;
            if (route.Length != 0) {
                route.CopyTo(destination: entry.NavigationState.WritablePath());
            }
            if (navigation.DomainIndex >= 0) {
                // Even a failed search retains its domain, goal, and status. The source baked that domain
                // before searching; preserve the same retention proof after restore, including an empty path.
                m_navigation[navigation.DomainIndex].EnsureBaked();
            }

            if (
                entry.IsRemoteHuman &&
                !entry.Parked &&
                !(continuingFederatedBodies?.Contains(item: captured.Index) ?? false)
            ) {
                if (m_reconnectGraceTicks.IsNever) {
                    entry.Parked = true;
                    entry.ParkedUntilTick = null;
                } else if (m_reconnectGraceTicks.IsZero) {
                    entry.Body = null;
                    entry.Active = false;
                    entry.IsRemoteHuman = false;
                    entry.IsAuthorityTransferred = false;
                    entry.PlacementId = null;
                    entry.AdmissionInstalledGrantTemplates = [];
                    entry.AdmissionRevokedKeys.Clear();
                    entry.IdentityDomain = string.Empty;
                    entry.IdentitySubject = string.Empty;
                    entry.AdmissionTrust = null;
                } else {
                    entry.Parked = true;
                    entry.ParkedUntilTick = unchecked((((long)tick) + m_reconnectGraceTicks.Ticks));
                }
            }
        }

        m_simulatedCount = checkpoint.SimulatedCount;
        m_seatKit = checkpoint.SeatKit;
        m_revision = checkpoint.Revision;
        RebuildCarryRelationships();
        RestoreSleepingIslands(checkpoint: checkpoint);
        m_bodyCellDefinition = null;
        m_factAllSlotsDirty = true;
    }

    /// <summary>Preflights caller-controlled population shape, counts, kits, cadence, and route addresses without mutating live state.</summary>
    // Builds a slot's body from a captured entry and writes the entry's body sections and registers into it: the body
    // half of a restore, shared by a whole checkpoint's restore and a client's prediction of its own body, which
    // restores an entry another authority captured into a slot of its own.
    private WorldBody RestoreEntryBody(in WorldPopulationEntryCheckpoint captured, WorldPlayerDefaults defaults, int index, byte seatKit) {
        var entry = m_entries[index];
        var profile = ((captured.Tail.Profile is { } projection)
            ? WorldIdentity.FromProjection(
                defaults: defaults,
                projection: projection
            )
            : null
        );
        var body = BuildBodyForKit(
            index: index,
            kitIndex: CapturedKitIndex(captured: captured, seatKit: seatKit),
            profile: profile
        );

        body.SetContactConfiguration(
            field: m_contactField,
            upPolicy: m_bodyUpPolicy,
            walkableThreshold: m_walkableThreshold
        );
        body.SetGravityField(field: m_gravityField);
        // Reconstruct live geometry before restoring latches. The next register catch-up must be a no-op
        // when scale/selection already matched at capture, and a real change when a write was still pending.
        body.SetScale(value: captured.Body.Pose.Scale);
        body.Pose(
            position: captured.Body.Pose.Position,
            yawRadians: captured.Body.Pose.Yaw,
            pitchRadians: FixedQ4816.Zero,
            rollRadians: FixedQ4816.Zero
        );
        // The slot is marked occupied before its body's state is written: occupancy admits the action-state
        // lane ordinal, and the captured registers are written into it.
        entry.Body = body;
        entry.Active = true;
        body.RestoreSections(
            contactFieldObservationCurrent: captured.Tail.ContactFieldObservationCurrent,
            contactFieldVersion: ContactFieldVersion,
            registers: captured.Tail.Registers.Span,
            sections: in captured.Body
        );
        ApplyRigidContactImages(index: index, images: captured.Body.Contacts.Slots.Span);
        return body;
    }
    /// <summary>Restores into a local seat slot the body another authority captured, for a client's prediction of its own
    /// body: the slot holds that body's exact sections and registers, under its captured kit, and no other entry or
    /// relationship changes. The population must be one no authority steps.</summary>
    /// <param name="slot">The 0-based local seat slot.</param>
    /// <param name="captured">The captured entry, in this population's document.</param>
    /// <param name="defaults">The document's player defaults, which an identity projection resolves against.</param>
    /// <returns>The restored body.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="slot"/> is not a local seat slot.</exception>
    /// <param name="attached">The world's attached solid rows as the capturing authority's step left them, which the
    /// body's step reads where it stands on or against one.</param>
    public WorldBody RestorePredicted(int slot, in WorldPopulationEntryCheckpoint captured, WorldPlayerDefaults defaults, IReadOnlyList<WorldAttachedSolidCheckpoint> attached) {
        ArgumentOutOfRangeException.ThrowIfNegative(value: slot);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(value: slot, other: LocalSeatCount);
        ArgumentNullException.ThrowIfNull(argument: defaults);
        ArgumentNullException.ThrowIfNull(argument: attached);
        if ((MovingSolids is { } moving) && (attached.Count != 0)) {
            moving.ValidateAttached(rows: attached);
            moving.RestoreAttached(rows: attached);
        }
        m_entries[slot].Active = false;
        m_entries[slot].Body = null;
        return RestoreEntryBody(captured: in captured, defaults: defaults, index: slot, seatKit: m_seatKit);
    }
    internal void ValidateCheckpoint(WorldPopulationCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        if (MovingSolids is { } attached) { attached.ValidateAttached(rows: (checkpoint.AttachedSolids ?? [])); }
        if ((checkpoint.Entries is null) || (checkpoint.EventBodies is null) || (checkpoint.SleepingGravity is null)) {
            throw new InvalidOperationException(message: "population checkpoint entries are null.");
        }
        // An empty document has no kits and retains the constructor's zero selection sentinel.
        // It can round-trip only an empty population; any actual body still requires a compiled kit.
        var emptyKitSelection = ((m_kits.Length == 0) && (checkpoint.SeatKit == 0) && (checkpoint.Entries.Count == 0) && (checkpoint.SimulatedCount == 0));

        if (
            !emptyKitSelection &&
            (((uint)checkpoint.SeatKit) >= ((uint)m_kits.Length))
        ) {
            throw new InvalidOperationException(message: $"population checkpoint seat kit {checkpoint.SeatKit} lies outside {m_kits.Length} compiled kits.");
        }
        if (
            (checkpoint.SimulatedCount < 0) ||
            (checkpoint.SimulatedCount > PeerCapacity)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint simulated count {checkpoint.SimulatedCount} lies outside 0..{PeerCapacity}.");
        }
        if (
            (checkpoint.Generations is null) ||
            (checkpoint.Generations.Length != Capacity) ||
            checkpoint.Generations.Any(predicate: generation => (generation < 0))
        ) {
            throw new InvalidOperationException(message: "population checkpoint must carry every slot's nonnegative generation.");
        }

        var restored = new WorldPopulationEntryCheckpoint?[Capacity];

        foreach (var captured in checkpoint.Entries) {
            if (((uint)captured.Index) >= ((uint)Capacity)) {
                throw new InvalidOperationException(message: $"population checkpoint entry index {captured.Index} lies outside capacity {Capacity}.");
            }
            if (restored[captured.Index] is not null) {
                throw new InvalidOperationException(message: $"population checkpoint repeats entry index {captured.Index}.");
            }
            restored[captured.Index] = captured;
            if (((uint)captured.Admission.KitIndex) >= ((uint)m_kits.Length)) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} names kit {captured.Admission.KitIndex} outside {m_kits.Length} compiled kits.");
            }
            if ((captured.Admission.SelectedKit < -1) || (captured.Admission.SelectedKit >= m_kits.Length) || (captured.Body.Pose.Scale <= FixedQ4816.Zero)) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} has invalid selected geometry.");
            }
            if (captured.Admission.Generation != checkpoint.Generations[captured.Index]) {
                throw new InvalidOperationException(message: "population checkpoint entry generation disagrees with the slot image.");
            }
            var flock = captured.Producer.Flock;

            if (
                (flock.RemainingTicks > (120UL * FixedTickConversion.TicksPerSecond)) ||
                (flock.Desired.Length > FixedQ4816.FromDouble(value: 3.0001)) ||
                (flock.Seeded && (flock.Generation != captured.Admission.Generation))
            ) {
                throw new InvalidOperationException(message: "population checkpoint carries invalid flock steering or cadence state.");
            }
            if (
                (flock.Target is { } observed) &&
                ((((uint)observed.Index) >= ((uint)Capacity)) || (observed.Index == captured.Index) || (observed.Generation < 0) ||
                 (observed.Generation > checkpoint.Generations[observed.Index]))
            ) {
                throw new InvalidOperationException(message: "population checkpoint carries an invalid flock target observation.");
            }
            if (
                (captured.Body.Motion.Carrying < -1) ||
                (captured.Body.Motion.Carrying >= Capacity) ||
                (captured.Body.Motion.Carrying == captured.Index)
            ) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries an invalid carry index {captured.Body.Motion.Carrying}.");
            }
            if (
                (captured.Body.Motion.CarriedBy < -1) ||
                (captured.Body.Motion.CarriedBy >= Capacity) ||
                (captured.Body.Motion.CarriedBy == captured.Index)
            ) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} names an invalid carrier index {captured.Body.Motion.CarriedBy}.");
            }
            static bool OutsideSecond(long remainder) => ((remainder <= -((long)FixedTickConversion.TicksPerSecond)) || (remainder >= ((long)FixedTickConversion.TicksPerSecond)));

            if (OutsideSecond(remainder: captured.Producer.PhaseRemainder) || OutsideSecond(remainder: captured.Producer.ActivityPhaseRemainder)) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries a producer phase remainder outside one engine second.");
            }
            var bodyKitIndex = CapturedKitIndex(captured: captured, seatKit: checkpoint.SeatKit);

            ValidateAutonomyCheckpoint(
                state: in captured.Cadence,
                kit: in m_kits[bodyKitIndex],
                steering: in captured.Steering
            );
            ValidateNavigationCheckpoint(activeProducerDomainIndex: captured.Producer.ActiveNavigationDomainIndex, path: captured.Route.Path.Span, state: in captured.Navigation);
        }

        var previousEvent = -1;

        foreach (var index in checkpoint.EventBodies) {
            if ((index <= previousEvent) || (((uint)index) >= ((uint)Capacity))) {
                throw new InvalidOperationException(message: "population checkpoint has invalid event discovery work.");
            }
            previousEvent = index;
        }
        var previousGravity = -1;
        var sleepingCount = checkpoint.Entries.Count(predicate: static entry => (entry.Body.Clocks.RigidSleepRootPlusOne > 0));

        if (checkpoint.SleepingGravity.Count != sleepingCount) {
            throw new InvalidOperationException(message: "population checkpoint must retain every sleeper's solved gravity.");
        }
        foreach (var row in checkpoint.SleepingGravity) {
            if ((row.Body <= previousGravity) || (((uint)row.Body) >= ((uint)Capacity)) ||
                (restored[row.Body] is not { Body.Clocks.RigidSleepRootPlusOne: > 0 })) {
                throw new InvalidOperationException(message: "population checkpoint has invalid sleeping gravity.");
            }
            previousGravity = row.Body;
        }
        foreach (var captured in checkpoint.Entries) {
            ValidateRigidContactImages(captured: captured, restored: restored, seatKit: checkpoint.SeatKit);
        }

        foreach (var captured in checkpoint.Entries) {
            var carrying = captured.Body.Motion.Carrying;
            var carriedBy = captured.Body.Motion.CarriedBy;
            var sleeping = captured.Body.Clocks.RigidSleepRootPlusOne;

            if ((sleeping < 0) || (sleeping > Capacity) ||
                ((sleeping > 0) && ((sleeping > (captured.Index + 1)) ||
                    (restored[(sleeping - 1)] is not { } root) || (root.Body.Clocks.RigidSleepRootPlusOne != sleeping) ||
                    !captured.Body.Motion.RigidResting || (carriedBy >= 0) ||
                    (m_kits[CapturedKitIndex(captured: captured, seatKit: checkpoint.SeatKit)].Rigid is null)))) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} has an invalid sleeping rigid island.");
            }

            if (
                (carrying >= 0) &&
                (carriedBy >= 0)
            ) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} cannot be both a carrier and carried.");
            }

            if (carrying >= 0) {
                if (
                    (restored[carrying] is not { } target) ||
                    (target.Body.Motion.CarriedBy != captured.Index) ||
                    (target.Body.Motion.Carrying >= 0) ||
                    (m_kits[CapturedKitIndex(captured: captured, seatKit: checkpoint.SeatKit)].Carry is null) ||
                    (m_kits[CapturedKitIndex(captured: target, seatKit: checkpoint.SeatKit)].Rigid is null)
                ) {
                    throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries body {carrying} without one valid mirrored carry relationship.");
                }
            }

            if (carriedBy >= 0) {
                if (
                    (restored[carriedBy] is not { } carrier) ||
                    (carrier.Body.Motion.Carrying != captured.Index) ||
                    (carrier.Body.Motion.CarriedBy >= 0) ||
                    (m_kits[CapturedKitIndex(captured: carrier, seatKit: checkpoint.SeatKit)].Carry is null) ||
                    (m_kits[CapturedKitIndex(captured: captured, seatKit: checkpoint.SeatKit)].Rigid is null)
                ) {
                    throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} names carrier {carriedBy} without one valid mirrored carry relationship.");
                }
            }
        }
    }

    private byte CapturedKitIndex(in WorldPopulationEntryCheckpoint captured, byte seatKit) => ((captured.Admission.SelectedKit >= 0)
        ? checked((byte)captured.Admission.SelectedKit) : (((captured.Index < LocalSeatCount) || captured.Admission.IsRemoteHuman) ? seatKit : captured.Admission.KitIndex));
    private static void ValidateAutonomyCheckpoint(in EntryCadenceSection state, in EntrySteeringSection steering, in FixedWorldKit kit) {
        var maximum = FixedTickConversion.TicksPerSecond;

        static bool ValidCadence(ulong period, ulong elapsed, ulong remaining, ulong maximum) => ((period == 0UL)
            ? ((elapsed == 0UL) && (remaining == 0UL))
            : ((period <= maximum) && (elapsed < period) && (remaining >= 1UL) && (remaining <= period))
        );

        if (
            !ValidCadence(
            state.MotionPeriodTicks,
            state.MotionElapsedTicks,
            state.MotionRemainingTicks,
            maximum
        ) ||
            !ValidCadence(
            state.SteeringPeriodTicks,
            state.SteeringElapsedTicks,
            state.SteeringRemainingTicks,
            maximum
        )
        ) {
            throw new InvalidOperationException(message: "population checkpoint carries invalid autonomous cadence state.");
        }
        if (
            ((state.MotionPeriodTicks != 0UL) && (state.MotionPeriodTicks != kit.AutonomousMotionTicks)) ||
            ((state.SteeringPeriodTicks != 0UL) && (state.SteeringPeriodTicks != kit.AutonomousSteeringTicks))
        ) {
            throw new InvalidOperationException(message: "population checkpoint autonomous cadence does not match its compiled kit.");
        }
        if (
            steering.Seeded &&
            (state.MotionPeriodTicks == 0UL) &&
            (state.SteeringPeriodTicks == 0UL)
        ) {
            throw new InvalidOperationException(message: "population checkpoint carries cached autonomous steering without an authored cadence.");
        }
        if (
            !steering.Seeded &&
            (steering.Intent != default)
        ) {
            throw new InvalidOperationException(message: "population checkpoint carries an unseeded autonomous steering image.");
        }
    }
    private void ValidateNavigationCheckpoint(in EntryNavigationSection state, ReadOnlySpan<int> path, int activeProducerDomainIndex) {
        if (!Enum.IsDefined(value: state.Status)) {
            throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' is not defined.");
        }
        if (
            (activeProducerDomainIndex < -1) ||
            (activeProducerDomainIndex >= m_navigation.Count)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint producer navigation domain {activeProducerDomainIndex} lies outside the compiled domain table.");
        }
        if (
            (state.DomainIndex < -1) ||
            (state.DomainIndex >= m_navigation.Count)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint navigation domain {state.DomainIndex} lies outside the compiled domain table.");
        }
        if (path.Length > WorldNavigationCapacity.MaxPathNodes) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path carries {path.Length} nodes; the maximum is {WorldNavigationCapacity.MaxPathNodes}.");
        }
        if (state.DomainIndex < 0) {
            if (
                (state.GoalCell != -1) ||
                (path.Length != 0) ||
                (state.Waypoint != 0) ||
                (state.ExpandedLast != 0)
            ) {
                throw new InvalidOperationException(message: "population checkpoint navigation state carries route data without a domain.");
            }
            if (state.Status is not (NavigationStatus.None or NavigationStatus.NoTarget or NavigationStatus.OutsideDomain)) {
                throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' requires a domain.");
            }
            return;
        }

        var domain = m_navigation[state.DomainIndex];

        if (activeProducerDomainIndex != state.DomainIndex) {
            throw new InvalidOperationException(message: $"population checkpoint route domain {state.DomainIndex} does not match active producer domain {activeProducerDomainIndex}.");
        }
        // A goal-field follower holds no route, goal cell or search work of its own: its next read derives everything
        // from its pose and its goal.
        if (domain.Tuning.Shared) {
            if ((state.GoalCell != -1) || (path.Length != 0) || (state.Waypoint != 0) || (state.ExpandedLast != 0)) {
                throw new InvalidOperationException(message: $"population checkpoint goal-field follower of domain '{domain.Name}' carries route data.");
            }
            if (state.Status is not (NavigationStatus.Active or NavigationStatus.Arrived or NavigationStatus.Unreachable)) {
                throw new InvalidOperationException(message: $"population checkpoint goal-field follower cannot carry status '{state.Status}'.");
            }
            return;
        }
        if (((uint)state.GoalCell) >= ((uint)domain.CellCount)) {
            throw new InvalidOperationException(message: $"population checkpoint navigation goal {state.GoalCell} lies outside domain '{domain.Name}'.");
        }
        if (
            (state.ExpandedLast < 0) ||
            (state.ExpandedLast > domain.Tuning.MaxExpandedNodes)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint navigation expansion count {state.ExpandedLast} exceeds domain '{domain.Name}' budget {domain.Tuning.MaxExpandedNodes}.");
        }
        if (path.Length > domain.Tuning.MaxPathNodes) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path carries {path.Length} nodes; domain '{domain.Name}' permits {domain.Tuning.MaxPathNodes}.");
        }
        if (
            (state.Waypoint < 0) ||
            (state.Waypoint > path.Length)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint navigation waypoint {state.Waypoint} lies outside its {path.Length}-node path.");
        }
        if (path.Length == 0) {
            if (state.Status is not (NavigationStatus.Unreachable or NavigationStatus.SearchLimit or NavigationStatus.PathLimit)) {
                throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' requires a stored path.");
            }
        } else {
            if (state.Waypoint == 0) {
                throw new InvalidOperationException(message: "population checkpoint navigation path has not advanced past its start node.");
            }
            if (state.Status is not (NavigationStatus.Active or NavigationStatus.Arrived)) {
                throw new InvalidOperationException(message: $"population checkpoint stored path cannot carry status '{state.Status}'.");
            }
        }
        for (var index = 0; (index < path.Length); index++) {
            if (((uint)path[index]) >= ((uint)domain.CellCount)) {
                throw new InvalidOperationException(message: $"population checkpoint navigation path node {path[index]} at index {index} lies outside domain '{domain.Name}'.");
            }
        }
        if (
            (path.Length != 0) &&
            (path[^1] != state.GoalCell)
        ) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path ends at {path[^1]}, not goal {state.GoalCell}.");
        }
    }
}
