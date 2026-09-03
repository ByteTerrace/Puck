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
        WorldPopulationNavigationCheckpoint? Navigation = null
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
    public sealed record WorldPopulationCheckpoint(int SimulatedCount, int Revision, byte SeatKit, IReadOnlyList<WorldPopulationEntryCheckpoint> Entries);

    /// <summary>Captures every active slot's simulation state. Asserts the per-tick pending-output lists are empty —
    /// guaranteed by <see cref="WorldServer.TryCaptureCheckpoint"/>'s capture point sitting between a completed
    /// <c>Step</c> and the next, never inside one.</summary>
    /// <exception cref="InvalidOperationException">A pending-output list is non-empty.</exception>
    public WorldPopulationCheckpoint Capture() {
        if (
            (m_effectOutputs.Count != 0) ||
            (m_designationOutputs.Count != 0) ||
            (m_generatorInvocations.Count != 0) ||
            (m_judgeInvocations.Count != 0) ||
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
            Revision: m_revision,
            SeatKit: m_seatKit,
            SimulatedCount: m_simulatedCount
        );
    }
    /// <summary>Restores every entity-table slot from a previously captured checkpoint. Every slot is cleared first —
    /// this replaces the live table wholesale rather than merging onto it. A captured entry that is
    /// <see cref="Entry.IsRemoteHuman"/> and not already <see cref="Entry.Parked"/> is parked as of
    /// <paramref name="tick"/>, exactly as <see cref="ApplyPeerDisconnected"/> parks a live disconnect — the
    /// connection that occupied it does not survive a restore (<see cref="WorldOutputHub"/>/<see cref="WorldTcpHost"/>
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

        // Restore is replacement. All caller-controlled entry and route addresses were preflighted above, before
        // this destructive phase starts.
        for (var index = 0; (index < Capacity); index++) {
            var entry = m_entries[index];

            entry.Active = false;
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
            var profile = ((captured.Profile is { } projection)
                ? WorldIdentity.FromProjection(
                    defaults: defaults,
                    projection: projection
                )
                : null
            );
            var body = BuildBodyForKit(
                kitIndex: captured.KitIndex,
                profile: profile
            );

            body.SetContactConfiguration(
                field: m_contactField,
                upPolicy: m_bodyUpPolicy,
                walkableThreshold: m_walkableThreshold
            );
            body.SetGravityField(field: m_gravityField);
            body.SetAttachmentPolicy(policy: m_fixedAttachment);
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
            };
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
    }

    /// <summary>Preflights caller-controlled population and route addresses without mutating live state.</summary>
    internal void ValidateCheckpoint(WorldPopulationCheckpoint checkpoint) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);

        var restored = new bool[Capacity];
        foreach (var captured in checkpoint.Entries) {
            if ((uint)captured.Index >= (uint)Capacity) {
                throw new InvalidOperationException(message: $"population checkpoint entry index {captured.Index} lies outside capacity {Capacity}.");
            }
            if (restored[captured.Index]) {
                throw new InvalidOperationException(message: $"population checkpoint repeats entry index {captured.Index}.");
            }
            restored[captured.Index] = true;
            ValidateNavigationCheckpoint(navigation: captured.Navigation);
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
            if (state.Status is not (WorldNavigationStatus.Unreachable or WorldNavigationStatus.SearchLimit or WorldNavigationStatus.PathLimit)) {
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
