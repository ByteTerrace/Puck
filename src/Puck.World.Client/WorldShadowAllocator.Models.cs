using System.Runtime.InteropServices;
using Puck.SignedDistance;

namespace Puck.World.Client;

/// <summary>A light's name, current table position, authored order and delivered-state shadow priority.</summary>
/// <param name="Name">The unique authored identity.</param>
/// <param name="LightIndex">The current frame's lights-table index, or -1 for a departed retained name; never identity.</param>
/// <param name="ListOrdinal">The authored order used for ties among non-holders and on fresh selection.</param>
/// <param name="Mode">The authored selection mode.</param>
/// <param name="Luminance">Finite nonnegative luminance resolved at a delivered tick.</param>
public readonly record struct WorldShadowCandidate(string Name, int LightIndex, int ListOrdinal, WorldShadowMode Mode, double Luminance);
/// <summary>The view's resolved shadow selection and handoff policy.</summary>
/// <param name="Slots">Stable slots, zero through four.</param>
/// <param name="FadeSlots">Additional incoming march slots, zero through two.</param>
/// <param name="FadeTicks">Authored fade duration in engine ticks.</param>
/// <param name="Overflow">The policy when a handoff cannot start.</param>
public readonly record struct WorldShadowSettings(int Slots, int FadeSlots, ulong FadeTicks, WorldShadowOverflow Overflow) {
    /// <summary>Reads the boot policy without inventing another default.</summary>
    /// <param name="render">The world's render row.</param>
    /// <returns>The authored policy.</returns>
    public static WorldShadowSettings From(WorldRenderDefaults render) => new(render.ShadowLights, render.ShadowFadeSlots, render.ShadowFadeTicks, render.ShadowOverflow);
}
/// <summary>A held slot; its owner is outgoing while its handoff remains active.</summary>
/// <param name="Slot">Stable slot index.</param>
/// <param name="Candidate">The light marched in this slot.</param>
/// <param name="Rank">One-based delivered priority rank; zero when absent from the delivered candidates.</param>
public readonly record struct WorldShadowSlot(int Slot, WorldShadowCandidate Candidate, int Rank);
/// <summary>The state and delivery reason stored in a handoff's four-byte flags lane.</summary>
[Flags]
public enum WorldShadowHandoffFlags : uint {
    /// <summary>This entry is vacant.</summary>
    None = 0,
    /// <summary>This entry owns an additional incoming march.</summary>
    Active = 1,
    /// <summary>Delivered selection changes the stable slot's owner.</summary>
    SelectionChanged = 2,
    /// <summary>The desired target was waiting at the preceding delivery.</summary>
    FromQueue = 4,
}
/// <summary>A 32-byte handoff entry. Current and previous delivered intervals each reserve F entries.</summary>
/// <param name="OutgoingLight">Outgoing index into the CPU name table, which retains departed participants.</param>
/// <param name="IncomingLight">Incoming index into the same table.</param>
/// <param name="Slot">Stable slot the incoming light replaces.</param>
/// <param name="Flags">Entry state and crossing reason.</param>
/// <param name="CrossingTick">Integer delivery that starts the handoff.</param>
/// <param name="DurationTicks">Its duration in integer engine ticks.</param>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct WorldShadowFade(int OutgoingLight, int IncomingLight, int Slot, WorldShadowHandoffFlags Flags, ulong CrossingTick, ulong DurationTicks) {
    /// <summary>Whether this entry is occupied.</summary>
    public bool Active => ((Flags & WorldShadowHandoffFlags.Active) != 0);
}
/// <summary>An active additional march, resolved solely from a stored crossing and the presented tick.</summary>
/// <param name="Slot">The outgoing stable slot.</param>
/// <param name="Outgoing">The outgoing named light.</param>
/// <param name="Incoming">The incoming named light.</param>
/// <param name="CrossingTick">The delivered crossing tick.</param>
/// <param name="DurationTicks">The handoff's integer duration.</param>
/// <param name="Weight">Exact tick-derived progress evaluated in double precision.</param>
/// <param name="Flags">The stored entry state and reason.</param>
public readonly record struct WorldShadowHandoff(int Slot, WorldShadowCandidate Outgoing, WorldShadowCandidate Incoming, ulong CrossingTick, ulong DurationTicks, double Weight, WorldShadowHandoffFlags Flags) {
    /// <summary>Gets the presentation-only float control for the counted GPU upload.</summary>
    public SdfShadowHandoff Control => new(Outgoing.LightIndex, Incoming.LightIndex, Slot, ((float)Weight));
}
/// <summary>The bounded resource keeping a current desired target from starting.</summary>
public enum WorldShadowQueueReason {
    /// <summary>Every configured fade entry is occupied.</summary>
    FadeCapacity,
    /// <summary>The named light participates in another slot's handoff.</summary>
    IdentityInUse,
    /// <summary>The target slot still has a handoff.</summary>
    SlotInHandoff,
}
/// <summary>A current desired assignment reconsidered at the next delivered boundary.</summary>
/// <param name="Slot">The slot the target would replace.</param>
/// <param name="Incoming">The current desired named light.</param>
/// <param name="Reason">The resource preventing its crossing.</param>
public readonly record struct WorldShadowQueued(int Slot, WorldShadowCandidate Incoming, WorldShadowQueueReason Reason);
/// <summary>Initialized output counts; active work is stable marches plus additional incoming marches.</summary>
/// <param name="StableCount">Held stable slots.</param>
/// <param name="FadeCount">Active additional incoming marches.</param>
/// <param name="QueuedCount">Waiting current targets.</param>
public readonly record struct WorldShadowReadout(int StableCount, int FadeCount, int QueuedCount) {
    /// <summary>Gets the complete active march count, bounded by K plus F.</summary>
    public int MarchSlots => (StableCount + FadeCount);
}
