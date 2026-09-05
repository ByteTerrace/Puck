using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldPopulation {
    /// <summary>One entity-table slot's checkpointed simulation state — see <see cref="Capture"/>. Excludes
    /// presentation-only fields (<c>LookIndex</c>) and read-back-only outcome strings, which the checkpoint's own
    /// exclusion rule (<see cref="WorldServer.TryCaptureCheckpoint"/>) leaves for the next write to that slot to set.</summary>
    public sealed record WorldPopulationEntryCheckpoint(
        int Index,
        byte KitIndex,
        Vector3 BodyColor,
        byte CatalogRig,
        WorldTargetDesignation[] Designations,
        int Generation,
        bool IsAuthorityTransferred,
        bool IsRemoteHuman,
        WorldMobilityIdentity? Mobility,
        int MobilityGeneration,
        bool Parked,
        long? ParkedUntilTick,
        string? PlacementId,
        FixedVector3 SpawnPosition,
        FixedQ4816 SpawnYaw,
        IReadOnlyList<WorldAdmissionGrant> AdmissionInstalledGrantTemplates,
        IReadOnlyList<(WorldCapability Capability, GrantSubject Subject)> AdmissionRevokedKeys,
        string IdentityDomain,
        string IdentitySubject,
        int ProducerAcquiredTarget,
        FixedQ4816 ProducerActivityPhase,
        FixedQ4816 ProducerActivityRate,
        FixedQ4816 ProducerPhase,
        FixedQ4816 ProducerPreferredAltitude,
        FixedQ4816 ProducerWeaveFrequency,
        long ProducerCurveArcRaw,
        string? ProducerActiveName,
        int ProducerActiveCurveIndex,
        FixedVector3 Position,
        FixedQ4816 Yaw,
        WorldBody.TransferState DynamicState,
        WorldBody.IntegrationResidue Residue,
        WorldIdentityProjection? Profile,
        WorldPopulationNavigationCheckpoint? Navigation = null,
        WorldPopulationFlockCheckpoint Flock = default,
        WorldPopulationAutonomyCheckpoint Autonomy = default
    );
    /// <summary>Cached local perception, timing residue, and observer-local attention stream.</summary>
    /// <param name="Seeded">Whether the neighbor contribution has been sampled for this producer.</param>
    /// <param name="Generation">The occupant generation that owns the attention stream.</param>
    /// <param name="Desired">The unclamped, weighted neighbor contribution; goal and heading are not cached.</param>
    /// <param name="RemainingTicks">Engine ticks until the next perception update.</param>
    /// <param name="SampleOrdinal">Observer-local rotating sample position.</param>
    /// <param name="Target">Last bounded sensed-target observation; never a live target-pose reference.</param>
    public readonly record struct WorldPopulationFlockCheckpoint(bool Seeded, int Generation, FixedVector3 Desired,
        ulong RemainingTicks, ulong SampleOrdinal, WorldFlockObservation? Target = null);
    /// <summary>One non-human body's phased motion/steering cadence and reusable producer image.</summary>
    public readonly record struct WorldPopulationAutonomyCheckpoint(
        ulong MotionPeriodTicks,
        ulong MotionElapsedTicks,
        ulong MotionRemainingTicks,
        ulong SteeringPeriodTicks,
        ulong SteeringElapsedTicks,
        ulong SteeringRemainingTicks,
        PlayerIntent SteeringIntent,
        bool SteeringSeeded
    );
    /// <summary>One body's cached deterministic route and producer binding.</summary>
    public readonly record struct WorldPopulationNavigationCheckpoint(
        int ActiveProducerDomainIndex,
        int DomainIndex,
        int GoalCell,
        int Waypoint,
        int ExpandedLast,
        WorldNavigationStatus Status,
        int[] Path
    );
    /// <summary>The population's own checkpointed state — see <see cref="Capture"/>.</summary>
    public sealed record WorldPopulationCheckpoint(int SimulatedCount, int Revision, byte SeatKit, IReadOnlyList<WorldPopulationEntryCheckpoint> Entries,
        int[] Generations, WorldNavigationSharedCheckpoint[]? SharedNavigation = null);

    /// <summary>Captures every active slot's simulation state. Asserts the per-tick pending-output lists are empty —
    /// guaranteed by <see cref="WorldServer.TryCaptureCheckpoint"/>'s capture point sitting between a completed
    /// <c>Step</c> and the next, never inside one.</summary>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    public WorldPopulationCheckpoint Capture() {
        if (
            (m_effectOutputs.Count != 0) ||
            (m_designationOutputs.Count != 0) ||
            (m_generatorInvocations.Count != 0) ||
            (m_durableStateOutputs.Count != 0)
        ) {
            throw new InvalidOperationException(message: "a population checkpoint requires every pending-output list to be empty — capture only between a completed Step and the next StepInstances.");
        }

        var entries = new List<WorldPopulationEntryCheckpoint>(capacity: Capacity);

        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            if (
                !entry.Active ||
                (entry.Body is not { } body)
            ) {
                continue;
            }

            var revokedKeys = new List<(WorldCapability, GrantSubject)>(capacity: entry.AdmissionRevokedKeys.Count);

            foreach (var key in entry.AdmissionRevokedKeys) {
                revokedKeys.Add(item: (key.Capability, key.Subject));
            }

            entries.Add(item: new WorldPopulationEntryCheckpoint(
                Index: index,
                KitIndex: entry.KitIndex,
                BodyColor: entry.BodyColor,
                CatalogRig: entry.CatalogRig,
                Designations: [.. entry.Designations],
                Generation: entry.Generation,
                IsAuthorityTransferred: entry.IsAuthorityTransferred,
                IsRemoteHuman: entry.IsRemoteHuman,
                Mobility: entry.Mobility,
                MobilityGeneration: entry.MobilityGeneration,
                Parked: entry.Parked,
                ParkedUntilTick: entry.ParkedUntilTick,
                PlacementId: entry.PlacementId,
                SpawnPosition: entry.SpawnPosition,
                SpawnYaw: entry.SpawnYaw,
                AdmissionInstalledGrantTemplates: [.. entry.AdmissionInstalledGrantTemplates],
                AdmissionRevokedKeys: revokedKeys,
                IdentityDomain: entry.IdentityDomain,
                IdentitySubject: entry.IdentitySubject,
                ProducerAcquiredTarget: entry.ProducerState.AcquiredTarget,
                ProducerActivityPhase: entry.ProducerState.ActivityPhase,
                ProducerActivityRate: entry.ProducerState.ActivityRate,
                ProducerPhase: entry.ProducerState.Phase,
                ProducerPreferredAltitude: entry.ProducerState.PreferredAltitude,
                ProducerWeaveFrequency: entry.ProducerState.WeaveFrequency,
                ProducerCurveArcRaw: entry.ProducerState.CurveArcRaw,
                ProducerActiveName: entry.ProducerState.ActiveProducerName,
                ProducerActiveCurveIndex: entry.ProducerState.ActiveProducerCurveIndex,
                Flock: new WorldPopulationFlockCheckpoint(entry.ProducerState.FlockSeeded, entry.ProducerState.FlockGeneration,
                    entry.ProducerState.FlockDesired, entry.ProducerState.FlockRemainingTicks, entry.ProducerState.FlockSampleOrdinal,
                    entry.ProducerState.FlockTarget),
                Autonomy: new WorldPopulationAutonomyCheckpoint(
                    MotionPeriodTicks: entry.AutonomyState.MotionPeriodTicks,
                    MotionElapsedTicks: entry.AutonomyState.MotionElapsedTicks,
                    MotionRemainingTicks: entry.AutonomyState.MotionRemainingTicks,
                    SteeringPeriodTicks: entry.AutonomyState.SteeringPeriodTicks,
                    SteeringElapsedTicks: entry.AutonomyState.SteeringElapsedTicks,
                    SteeringRemainingTicks: entry.AutonomyState.SteeringRemainingTicks,
                    SteeringIntent: entry.AutonomyState.SteeringIntent,
                    SteeringSeeded: entry.AutonomyState.SteeringSeeded
                ),
                Position: body.FixedPosition,
                Yaw: body.FixedYaw,
                DynamicState: body.CaptureTransferState(),
                Residue: body.CaptureIntegrationResidue(),
                Profile: body.Profile?.Project(),
                Navigation: new WorldPopulationNavigationCheckpoint(
                    ActiveProducerDomainIndex: entry.ProducerState.ActiveProducerNavigationDomainIndex,
                    DomainIndex: entry.NavigationState.DomainIndex,
                    GoalCell: entry.NavigationState.GoalCell,
                    Waypoint: entry.NavigationState.Waypoint,
                    ExpandedLast: entry.NavigationState.ExpandedLast,
                    Status: entry.NavigationState.Status,
                    Path: [.. entry.NavigationState.Path.AsSpan(start: 0, length: entry.NavigationState.PathLength)]
                )
            ));
        }

        return new WorldPopulationCheckpoint(
            Entries: entries,
            Generations: m_entries.Select(entry => entry.Generation).ToArray(),
            SharedNavigation: m_navigation.CaptureShared(),
            Revision: m_revision,
            SeatKit: m_seatKit,
            SimulatedCount: m_simulatedCount
        );
    }
    /// <summary>Restores every entity-table slot from a previously captured checkpoint. Every slot is cleared first —
    /// this replaces the live table wholesale rather than merging onto it. A captured entry that is
    /// <see cref="Entry.IsRemoteHuman"/> and not already <see cref="Entry.Parked"/> is parked as of
    /// <paramref name="tick"/>, exactly as <see cref="ApplyPeerDisconnected"/> parks a live disconnect — the
    /// connection that occupied it does not survive a restore (<see cref="WorldOutputHub"/>/<see cref="WorldPeerHost"/>
    /// state is excluded from the checkpoint), so a captured non-parked remote human is always stale: nothing will
    /// ever again feed a fresh federated intent into that slot under this process's own tables. Only the BODY parks;
    /// the generation's grant rows are released by <c>WorldServer.RestoreCheckpoint</c> right after the grant table
    /// restores, on the same argument the disconnect arm applies.</summary>
    /// <param name="checkpoint">The captured state to restore.</param>
    /// <param name="defaults">The player defaults a restored profiled body's identity resolves colors against.</param>
    /// <param name="tick">The restored current tick — the basis a freshly parked <see cref="Entry.ParkedUntilTick"/>
    /// is stamped from.</param>
    public void Restore(WorldPopulationCheckpoint checkpoint, WorldPlayerDefaults defaults, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        ArgumentNullException.ThrowIfNull(argument: defaults);

        ValidateCheckpoint(checkpoint: checkpoint);
        m_navigation.RestoreShared(checkpoint.SharedNavigation);

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
            entry.Mobility = null;
            entry.MobilityGeneration = 0;
            entry.AdmissionInstalledGrantTemplates = [];
            entry.AdmissionRevokedKeys = [];
            entry.IdentityDomain = string.Empty;
            entry.IdentitySubject = string.Empty;
            entry.NavigationState.Clear();
            ClearDesignations(entry: entry);
        }

        foreach (var captured in checkpoint.Entries) {
            var entry = m_entries[captured.Index];
            var bodyKitIndex = ((captured.Index < LocalSeatCount) ? checkpoint.SeatKit : captured.KitIndex);
            var profile = ((captured.Profile is { } projection)
                ? WorldIdentity.FromProjection(
                    defaults: defaults,
                    projection: projection
                )
                : null
            );
            var body = BuildBodyForKit(
                kitIndex: bodyKitIndex,
                profile: profile
            );

            body.SetContactConfiguration(
                field: m_contactField,
                upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
            );
            body.SetGravityField(field: m_gravityField);
            body.Pose(
                position: captured.Position,
                yawRadians: captured.Yaw,
                pitchRadians: FixedQ4816.Zero,
                rollRadians: FixedQ4816.Zero
            );
            body.ApplyTransferState(state: captured.DynamicState);
            body.ApplyIntegrationResidue(residue: captured.Residue);

            var designationCount = Math.Min(
                val1: captured.Designations.Length,
                val2: entry.Designations.Length
            );

            for (var slot = 0; (slot < designationCount); slot++) {
                entry.Designations[slot] = captured.Designations[slot];
            }

            entry.Body = body;
            entry.Active = true;
            entry.KitIndex = captured.KitIndex;
            entry.BodyColor = captured.BodyColor;
            entry.CatalogRig = captured.CatalogRig;
            entry.Generation = captured.Generation;
            entry.IsAuthorityTransferred = captured.IsAuthorityTransferred;
            entry.IsRemoteHuman = captured.IsRemoteHuman;
            entry.Mobility = captured.Mobility;
            entry.MobilityGeneration = captured.MobilityGeneration;
            entry.Parked = captured.Parked;
            entry.ParkedUntilTick = captured.ParkedUntilTick;
            entry.PlacementId = captured.PlacementId;
            entry.SpawnPosition = captured.SpawnPosition;
            entry.SpawnYaw = captured.SpawnYaw;
            entry.AdmissionInstalledGrantTemplates = captured.AdmissionInstalledGrantTemplates;
            entry.AdmissionRevokedKeys = [.. captured.AdmissionRevokedKeys.Select(selector: static key => (key.Capability, key.Subject))];
            entry.IdentityDomain = captured.IdentityDomain;
            entry.IdentitySubject = captured.IdentitySubject;
            entry.ProducerState = new BodyProducerState {
                AcquiredTarget = captured.ProducerAcquiredTarget,
                ActiveProducerCurveIndex = captured.ProducerActiveCurveIndex,
                ActiveProducerName = captured.ProducerActiveName,
                ActiveProducerNavigationDomainIndex = (captured.Navigation?.ActiveProducerDomainIndex ?? -1),
                ActivityPhase = captured.ProducerActivityPhase,
                ActivityRate = captured.ProducerActivityRate,
                CurveArcRaw = captured.ProducerCurveArcRaw,
                Phase = captured.ProducerPhase,
                PreferredAltitude = captured.ProducerPreferredAltitude,
                WeaveFrequency = captured.ProducerWeaveFrequency,
                FlockSeeded = captured.Flock.Seeded,
                FlockGeneration = captured.Flock.Generation,
                FlockDesired = captured.Flock.Desired,
                FlockRemainingTicks = captured.Flock.RemainingTicks,
                FlockSampleOrdinal = captured.Flock.SampleOrdinal,
                FlockTarget = captured.Flock.Target,
            };
            entry.AutonomyState = new BodyAutonomyState {
                MotionPeriodTicks = captured.Autonomy.MotionPeriodTicks,
                MotionElapsedTicks = captured.Autonomy.MotionElapsedTicks,
                MotionRemainingTicks = captured.Autonomy.MotionRemainingTicks,
                SteeringPeriodTicks = captured.Autonomy.SteeringPeriodTicks,
                SteeringElapsedTicks = captured.Autonomy.SteeringElapsedTicks,
                SteeringRemainingTicks = captured.Autonomy.SteeringRemainingTicks,
                SteeringIntent = captured.Autonomy.SteeringIntent,
                SteeringSeeded = captured.Autonomy.SteeringSeeded,
            };
            if (captured.ProducerActiveName is { } activeName && m_kits[bodyKitIndex].Producers.TryGetValue(activeName, out var binding)) {
                entry.ProducerState.FlockBinding = binding;
            }
            if (captured.Navigation is { } navigation) {
                entry.NavigationState.DomainIndex = navigation.DomainIndex;
                entry.NavigationState.GoalCell = navigation.GoalCell;
                entry.NavigationState.Waypoint = navigation.Waypoint;
                entry.NavigationState.ExpandedLast = navigation.ExpandedLast;
                entry.NavigationState.Status = navigation.Status;
                entry.NavigationState.PathLength = navigation.Path.Length;
                if (navigation.Path.Length != 0) {
                    navigation.Path.AsSpan().CopyTo(destination: entry.NavigationState.WritablePath());
                }
            }

            if (
                entry.IsRemoteHuman &&
                !entry.Parked
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
    }

    /// <summary>Preflights caller-controlled population shape, counts, kits, cadence, and route addresses without mutating live state.</summary>
    internal void ValidateCheckpoint(WorldPopulationCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        if (checkpoint.Entries is null) {
            throw new InvalidOperationException("population checkpoint entries are null.");
        }
        if ((uint)checkpoint.SeatKit >= (uint)m_kits.Length) {
            throw new InvalidOperationException($"population checkpoint seat kit {checkpoint.SeatKit} lies outside {m_kits.Length} compiled kits.");
        }
        if (checkpoint.SimulatedCount < 0 || checkpoint.SimulatedCount > PeerCapacity) {
            throw new InvalidOperationException($"population checkpoint simulated count {checkpoint.SimulatedCount} lies outside 0..{PeerCapacity}.");
        }
        m_navigation.ValidateShared(checkpoint.SharedNavigation);
        if (checkpoint.Generations is null || checkpoint.Generations.Length != Capacity || checkpoint.Generations.Any(generation => generation < 0)) {
            throw new InvalidOperationException("population checkpoint must carry every slot's nonnegative generation.");
        }

        var restored = new WorldPopulationEntryCheckpoint?[Capacity];
        foreach (var captured in checkpoint.Entries) {
            if ((uint)captured.Index >= (uint)Capacity) {
                throw new InvalidOperationException(message: $"population checkpoint entry index {captured.Index} lies outside capacity {Capacity}.");
            }
            if (restored[captured.Index] is not null) {
                throw new InvalidOperationException(message: $"population checkpoint repeats entry index {captured.Index}.");
            }
            restored[captured.Index] = captured;
            if ((uint)captured.KitIndex >= (uint)m_kits.Length) {
                throw new InvalidOperationException($"population checkpoint entry {captured.Index} names kit {captured.KitIndex} outside {m_kits.Length} compiled kits.");
            }
            if (captured.Generation != checkpoint.Generations[captured.Index]) {
                throw new InvalidOperationException("population checkpoint entry generation disagrees with the slot image.");
            }
            if (captured.Flock.RemainingTicks > 120UL * FixedTickConversion.TicksPerSecond ||
                captured.Flock.Desired.Length > FixedQ4816.FromDouble(3.0001) ||
                (captured.Flock.Seeded && captured.Flock.Generation != captured.Generation)) {
                throw new InvalidOperationException("population checkpoint carries invalid flock steering or cadence state.");
            }
            if (captured.Flock.Target is { } observed &&
                ((uint)observed.Index >= (uint)Capacity || observed.Index == captured.Index || observed.Generation < 0 ||
                 observed.Generation > checkpoint.Generations[observed.Index])) {
                throw new InvalidOperationException("population checkpoint carries an invalid flock target observation.");
            }
            if (captured.Residue.Carrying < -1 || captured.Residue.Carrying >= Capacity || captured.Residue.Carrying == captured.Index) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries an invalid carry index {captured.Residue.Carrying}.");
            }
            if (captured.Residue.CarriedBy < -1 || captured.Residue.CarriedBy >= Capacity || captured.Residue.CarriedBy == captured.Index) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} names an invalid carrier index {captured.Residue.CarriedBy}.");
            }
            var bodyKitIndex = ((captured.Index < LocalSeatCount) ? checkpoint.SeatKit : captured.KitIndex);

            ValidateAutonomyCheckpoint(captured.Autonomy, m_kits[bodyKitIndex]);
            ValidateNavigationCheckpoint(navigation: captured.Navigation);
        }

        foreach (var captured in checkpoint.Entries) {
            var carrying = captured.Residue.Carrying;
            var carriedBy = captured.Residue.CarriedBy;

            if (
                (carrying >= 0) &&
                (carriedBy >= 0)
            ) {
                throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} cannot be both a carrier and carried.");
            }

            if (carrying >= 0) {
                if (
                    (restored[carrying] is not { } target) ||
                    (target.Residue.CarriedBy != captured.Index) ||
                    (target.Residue.Carrying >= 0) ||
                    (m_kits[((captured.Index < LocalSeatCount) ? checkpoint.SeatKit : captured.KitIndex)].Carry is null) ||
                    (m_kits[((target.Index < LocalSeatCount) ? checkpoint.SeatKit : target.KitIndex)].Rigid is null)
                ) {
                    throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} carries body {carrying} without one valid mirrored carry relationship.");
                }
            }

            if (carriedBy >= 0) {
                if (
                    (restored[carriedBy] is not { } carrier) ||
                    (carrier.Residue.Carrying != captured.Index) ||
                    (carrier.Residue.CarriedBy >= 0) ||
                    (m_kits[((carrier.Index < LocalSeatCount) ? checkpoint.SeatKit : carrier.KitIndex)].Carry is null) ||
                    (m_kits[((captured.Index < LocalSeatCount) ? checkpoint.SeatKit : captured.KitIndex)].Rigid is null)
                ) {
                    throw new InvalidOperationException(message: $"population checkpoint entry {captured.Index} names carrier {carriedBy} without one valid mirrored carry relationship.");
                }
            }
        }
    }

    private static void ValidateAutonomyCheckpoint(WorldPopulationAutonomyCheckpoint state, in FixedWorldKit kit) {
        var maximum = FixedTickConversion.DurationEngineTicks(seconds: FixedQ4816.One);

        static bool ValidCadence(ulong period, ulong elapsed, ulong remaining, ulong maximum) => ((period == 0UL)
            ? ((elapsed == 0UL) && (remaining == 0UL))
            : ((period <= maximum) && (elapsed < period) && (remaining >= 1UL) && (remaining <= period))
        );

        if (
            !ValidCadence(state.MotionPeriodTicks, state.MotionElapsedTicks, state.MotionRemainingTicks, maximum) ||
            !ValidCadence(state.SteeringPeriodTicks, state.SteeringElapsedTicks, state.SteeringRemainingTicks, maximum)
        ) {
            throw new InvalidOperationException("population checkpoint carries invalid autonomous cadence state.");
        }
        if (
            (state.MotionPeriodTicks != 0UL && state.MotionPeriodTicks != kit.AutonomousMotionTicks) ||
            (state.SteeringPeriodTicks != 0UL && state.SteeringPeriodTicks != kit.AutonomousSteeringTicks)
        ) {
            throw new InvalidOperationException("population checkpoint autonomous cadence does not match its compiled kit.");
        }
        if (
            state.SteeringSeeded &&
            (state.MotionPeriodTicks == 0UL) &&
            (state.SteeringPeriodTicks == 0UL)
        ) {
            throw new InvalidOperationException("population checkpoint carries cached autonomous steering without an authored cadence.");
        }
        if (!state.SteeringSeeded && (state.SteeringIntent != default)) {
            throw new InvalidOperationException("population checkpoint carries an unseeded autonomous steering image.");
        }
    }

    private void ValidateNavigationCheckpoint(WorldPopulationNavigationCheckpoint? navigation) {
        if (navigation is not { } state) {
            return;
        }
        if (state.Path is null) {
            throw new InvalidOperationException(message: "population checkpoint navigation path is null.");
        }
        if (!Enum.IsDefined(value: state.Status)) {
            throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' is not defined.");
        }
        if (state.ActiveProducerDomainIndex < -1 || state.ActiveProducerDomainIndex >= m_navigation.Count) {
            throw new InvalidOperationException(message: $"population checkpoint producer navigation domain {state.ActiveProducerDomainIndex} lies outside the compiled domain table.");
        }
        if (state.DomainIndex < -1 || state.DomainIndex >= m_navigation.Count) {
            throw new InvalidOperationException(message: $"population checkpoint navigation domain {state.DomainIndex} lies outside the compiled domain table.");
        }
        if (state.Path.Length > WorldNavigationCapacity.MaxPathNodes) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path carries {state.Path.Length} nodes; the maximum is {WorldNavigationCapacity.MaxPathNodes}.");
        }
        if (state.DomainIndex < 0) {
            if (state.GoalCell != -1 || state.Path.Length != 0 || state.Waypoint != 0 || state.ExpandedLast != 0) {
                throw new InvalidOperationException(message: "population checkpoint navigation state carries route data without a domain.");
            }
            if (state.Status is not (WorldNavigationStatus.None or WorldNavigationStatus.NoTarget or WorldNavigationStatus.OutsideDomain)) {
                throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' requires a domain.");
            }
            return;
        }

        var domain = m_navigation[state.DomainIndex];
        if (domain.Sharing is null && state.Status is WorldNavigationStatus.Pending or WorldNavigationStatus.CapacityLimited) {
            throw new InvalidOperationException(message: "population checkpoint shared navigation status requires a shared domain.");
        }
        if (domain.Sharing is not null && state.ExpandedLast != 0) {
            throw new InvalidOperationException(message: "population checkpoint shared search work belongs to its domain, not a body.");
        }
        if (state.ActiveProducerDomainIndex != state.DomainIndex) {
            throw new InvalidOperationException(message: $"population checkpoint route domain {state.DomainIndex} does not match active producer domain {state.ActiveProducerDomainIndex}.");
        }
        if ((uint)state.GoalCell >= (uint)domain.CellCount) {
            throw new InvalidOperationException(message: $"population checkpoint navigation goal {state.GoalCell} lies outside domain '{domain.Name}'.");
        }
        if (state.ExpandedLast < 0 || state.ExpandedLast > domain.Tuning.MaxExpandedNodes) {
            throw new InvalidOperationException(message: $"population checkpoint navigation expansion count {state.ExpandedLast} exceeds domain '{domain.Name}' budget {domain.Tuning.MaxExpandedNodes}.");
        }
        if (state.Path.Length > domain.Tuning.MaxPathNodes) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path carries {state.Path.Length} nodes; domain '{domain.Name}' permits {domain.Tuning.MaxPathNodes}.");
        }
        if (state.Waypoint < 0 || state.Waypoint > state.Path.Length) {
            throw new InvalidOperationException(message: $"population checkpoint navigation waypoint {state.Waypoint} lies outside its {state.Path.Length}-node path.");
        }
        if (state.Path.Length == 0) {
            if (state.Status is not (WorldNavigationStatus.Unreachable or WorldNavigationStatus.SearchLimit or WorldNavigationStatus.PathLimit or WorldNavigationStatus.Pending or WorldNavigationStatus.CapacityLimited)) {
                throw new InvalidOperationException(message: $"population checkpoint navigation status '{state.Status}' requires a stored path.");
            }
        } else {
            if (state.Waypoint == 0) {
                throw new InvalidOperationException(message: "population checkpoint navigation path has not advanced past its start node.");
            }
            if (state.Status is not (WorldNavigationStatus.Active or WorldNavigationStatus.Arrived)) {
                throw new InvalidOperationException(message: $"population checkpoint stored path cannot carry status '{state.Status}'.");
            }
        }
        for (var index = 0; index < state.Path.Length; index++) {
            if ((uint)state.Path[index] >= (uint)domain.CellCount) {
                throw new InvalidOperationException(message: $"population checkpoint navigation path node {state.Path[index]} at index {index} lies outside domain '{domain.Name}'.");
            }
        }
        if (state.Path.Length != 0 && state.Path[^1] != state.GoalCell) {
            throw new InvalidOperationException(message: $"population checkpoint navigation path ends at {state.Path[^1]}, not goal {state.GoalCell}.");
        }
    }
}
