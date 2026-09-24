using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The grant table's own checkpointed state — every table this class owns.</summary>
public sealed record WorldGrantsCheckpoint(
    IReadOnlyList<WorldGrantsGranteeCheckpoint> Grantees,
    IReadOnlyList<(WorldCapability Capability, GrantSubject Subject, Grantee Holder)> Exclusive,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ushort Budget)> Budgets,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ushort Budget)> EventBudgets,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, long Ceiling)> HoldCeilings,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ulong Bits)> ChannelReach,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, IReadOnlyList<(int Ordinal, long Ceiling)> Ceilings)> PoolCeilings,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, UInt128 Bits)> KindMasks,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject, ulong Bits)> WriteMasks,
    IReadOnlyList<(Grantee Grantee, WorldCapability Capability, GrantSubject Subject)> SeededSections,
    IReadOnlyList<(int BodyIndex, string Reason)> DriveGates,
    int Revision
);
