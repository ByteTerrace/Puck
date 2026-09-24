using System.Numerics;
using Puck.Maths;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One entity-table slot's checkpointed simulation state — see <see cref="WorldPopulation.Capture"/>. Excludes
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
    WorldBodyTransferState DynamicState,
    WorldBodyIntegrationResidue Residue,
    WorldIdentityProjection? Profile,
    WorldPopulationNavigationCheckpoint? Navigation = null,
    WorldPopulationFlockCheckpoint Flock = default,
    WorldPopulationAutonomyCheckpoint Autonomy = default
);
