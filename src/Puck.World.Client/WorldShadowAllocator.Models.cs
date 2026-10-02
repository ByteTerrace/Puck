namespace Puck.World.Client;

/// <summary>A body whose luminance has already been resolved at the delivered integer engine tick.</summary>
/// <param name="Name">The immutable authored identity, unique within the candidate set.</param>
/// <param name="ListOrdinal">The unique authored list ordinal, used to break luminance ties.</param>
/// <param name="LightIndex">The current resolved light-table index.</param>
/// <param name="Mode">The body's authored shadow selection policy.</param>
/// <param name="Luminance">Finite, nonnegative tick-state luminance; never a presented-fraction value.</param>
public readonly record struct WorldShadowCandidate(string Name, int ListOrdinal, int LightIndex, WorldShadowMode Mode, double Luminance);
/// <summary>The already-resolved tier policy. This component declares no authored defaults.</summary>
/// <param name="Slots">Stable march slots, from zero through four.</param>
/// <param name="FadeSlots">Additional incoming march slots, from zero through two.</param>
/// <param name="FadeTicks">Engine ticks in a handoff; zero makes every crossing instant.</param>
/// <param name="Overflow">The policy when no handoff is available.</param>
public readonly record struct WorldShadowSettings(int Slots, int FadeSlots, ulong FadeTicks, WorldShadowOverflow Overflow);
/// <summary>One held stable slot. During a handoff its candidate is the outgoing participant.</summary>
/// <param name="Slot">The stable slot index, independent from the entry's index in a returned span.</param>
/// <param name="Candidate">The body marched in this held slot.</param>
public readonly record struct WorldShadowSlot(int Slot, WorldShadowCandidate Candidate);
/// <summary>An additional incoming march, paired with one outgoing stable slot.</summary>
/// <param name="Slot">The stable slot this handoff will replace.</param>
/// <param name="Outgoing">The body retained in that stable slot until completion.</param>
/// <param name="Incoming">The gaining body, marched in one extra slot.</param>
/// <param name="CrossingTick">The delivered integer tick that detected the crossing.</param>
/// <param name="Weight">Clamped presented progress from zero to one, independent of frames rendered.</param>
public readonly record struct WorldShadowHandoff(int Slot, WorldShadowCandidate Outgoing, WorldShadowCandidate Incoming, ulong CrossingTick, double Weight);
/// <summary>Why a desired assignment cannot start at this delivered tick.</summary>
public enum WorldShadowQueueReason {
    /// <summary>Every allowed handoff is already occupied.</summary>
    FadeCapacity,
    /// <summary>The desired body still participates in a different active handoff.</summary>
    IdentityInUse,
    /// <summary>This stable slot is still completing its previous handoff.</summary>
    SlotInHandoff,
}
/// <summary>A currently desired assignment waiting to be considered at a later delivered tick.</summary>
/// <param name="Slot">The stable slot the desired assignment would replace.</param>
/// <param name="Incoming">The current desired body; stale targets are removed on each delivery.</param>
/// <param name="Reason">The bounded resource keeping this target queued.</param>
public readonly record struct WorldShadowQueued(int Slot, WorldShadowCandidate Incoming, WorldShadowQueueReason Reason);
/// <summary>Counts in the caller-owned output spans; each count describes their initialized prefix.</summary>
/// <param name="StableCount">Held stable entries.</param>
/// <param name="HandoffCount">Additional incoming entries whose presented weight is below one.</param>
/// <param name="QueuedCount">Current queued assignments.</param>
/// <param name="MarchSlots">Exactly stable entries plus additional handoff entries, never more than K plus F.</param>
public readonly record struct WorldShadowReadout(int StableCount, int HandoffCount, int QueuedCount, int MarchSlots);
