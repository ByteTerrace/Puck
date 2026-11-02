using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One admitted session as a checkpoint carries it: the principal it acts as, the verdict's templates, the
/// body an embodiment assigned it, whether its observer has already ended (its end then applies at the next step), and
/// the input its viewer last forwarded, which the rules read until the next forward replaces it. The presses kept since
/// the last settled step are not carried: every step settles them before it closes, so none stands at a checkpoint
/// boundary. The rows a session holds ride the grant tables beside every other grantee's.</summary>
/// <param name="Session">The session principal, carrying its ordinal and epoch.</param>
/// <param name="Templates">The admission verdict's grant templates.</param>
/// <param name="BodyIndex">The body an embodiment assigned, or <see langword="null"/> for an unembodied session.</param>
/// <param name="BodyGeneration">The generation of that body when it was assigned.</param>
/// <param name="ObserverEnded">Whether the session's observer faulted or detached itself, so it acts no more.</param>
/// <param name="Input">The input the session's viewer last forwarded.</param>
public sealed record WorldSessionCheckpoint(
    Principal Session,
    IReadOnlyList<WorldAdmissionGrant> Templates,
    int? BodyIndex,
    int BodyGeneration,
    bool ObserverEnded,
    PlayerIntent Input
);
/// <summary>One handle table as a checkpoint carries it: the handles a guest holds name a slot index and generation,
/// so the slots, the generation counter and the grant revision the slots were projected at are state.</summary>
/// <param name="Principal">The addon or peer principal the table is for.</param>
/// <param name="Capability">The capability the table designates handles over.</param>
/// <param name="Slots">Each slot's designated subject and generation, in slot order.</param>
/// <param name="NextGeneration">The generation the next changed slot takes.</param>
/// <param name="BuiltAtRevision">The grant revision the slots were projected at; -1 for a table never projected.</param>
public sealed record WorldHandleTableCheckpoint(
    Principal Principal,
    WorldCapability Capability,
    IReadOnlyList<(GrantSubject Subject, int Generation)> Slots,
    int NextGeneration,
    int BuiltAtRevision
);
/// <summary>The grant table's own checkpointed state — every table this class owns, the admitted sessions and the
/// handle tables included.</summary>
public sealed record WorldGrantsCheckpoint(
    IReadOnlyList<WorldGrantsGranteeCheckpoint> Grantees,
    IReadOnlyList<(WorldCapability Capability, GrantSubject Subject, Grantee Holder)> Exclusive,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ushort Budget)> Budgets,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ushort Budget)> EventBudgets,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, long Ceiling)> HoldCeilings,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, WorldChannelSelection Reach)> ChannelReach,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, IReadOnlyList<(string Channel, long Ceiling)> Ceilings)> PoolCeilings,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, UInt128 Bits)> KindMasks,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ulong Bits)> WriteMasks,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject)> SeededSections,
    IReadOnlyList<(int BodyIndex, string Reason)> DriveGates,
    IReadOnlyList<(int Ordinal, int Epoch)> SessionEpochs,
    IReadOnlyList<WorldSessionCheckpoint> Sessions,
    IReadOnlyList<WorldHandleTableCheckpoint> HandleTables,
    int Revision
);
