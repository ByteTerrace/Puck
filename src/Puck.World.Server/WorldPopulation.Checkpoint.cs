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
        FixedVector3 Position,
        FixedQ4816 Yaw,
        WorldBody.TransferState DynamicState,
        WorldBody.IntegrationResidue Residue,
        WorldIdentityProjection? Profile
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
                Position: body.FixedPosition,
                Yaw: body.FixedYaw,
                DynamicState: body.CaptureTransferState(),
                Residue: body.CaptureIntegrationResidue(),
                Profile: body.Profile?.Project()
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
    /// ever again feed a fresh federated intent into that slot under this process's own tables, and treating it as
    /// still live would let park-with-grace's own accepted-reconnect window silently vanish under a restart.</summary>
    /// <param name="checkpoint">The captured state to restore.</param>
    /// <param name="defaults">The player defaults a restored profiled body's identity resolves colors against.</param>
    /// <param name="tick">The restored current tick — the basis a freshly parked <see cref="Entry.ParkedUntilTick"/>
    /// is stamped from.</param>
    public void Restore(WorldPopulationCheckpoint checkpoint, WorldPlayerDefaults defaults, ulong tick) {
        ArgumentNullException.ThrowIfNull(argument: checkpoint);
        ArgumentNullException.ThrowIfNull(argument: defaults);

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

            body.SetContactField(field: m_contactField);
            body.SetGravityField(field: m_gravityField);
            body.SetWaterline(level: m_waterline);
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
                ActivityPhase = captured.ProducerActivityPhase,
                ActivityRate = captured.ProducerActivityRate,
                Phase = captured.ProducerPhase,
                PreferredAltitude = captured.ProducerPreferredAltitude,
                WeaveFrequency = captured.ProducerWeaveFrequency,
            };

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
}
