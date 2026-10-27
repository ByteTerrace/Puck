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
/// <summary>The grant table's own checkpointed state — every table this class owns, the admitted sessions
/// included.</summary>
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
    int Revision
);
