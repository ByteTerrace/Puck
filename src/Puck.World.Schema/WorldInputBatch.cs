using Puck.World.Protocol;

namespace Puck.World;

/// <summary>The lifetime of one registered semantic input source.</summary>
public enum WorldInputContributionKind : byte {
    /// <summary>A physical or synthetic held level.</summary>
    Level,
    /// <summary>A destination-owned latch independent of its opener's current page.</summary>
    Toggle,
    /// <summary>This step's displacement, never retained at a barrier.</summary>
    Relative,
}
/// <summary>An admitted bounded source contract, captured with the input owner.</summary>
/// <param name="Handle">Owner-local wire handle.</param>
/// <param name="Channel">Logical channel id.</param>
/// <param name="Kind">Held, latched, or displacement.</param>
/// <param name="Name">Stable owner-local semantic source identity, independent of its wire handle.</param>
/// <param name="Binding">Canonical effective binding contract; changed meaning invalidates continuation.</param>
public readonly record struct WorldInputSource(ushort Handle, ushort Channel, WorldInputContributionKind Kind, string Name, string Binding = "");
/// <summary>One entry in the complete sparse source image behind a submitted level image.</summary>
/// <param name="Handle">Previously admitted source handle.</param>
/// <param name="Channel">Registered logical action.</param>
/// <param name="Kind">Registered source lifetime.</param>
/// <param name="RawValue">Raw sample in the channel's representation.</param>
/// <param name="Activation">Owner/epoch-scoped activation shared by sibling outputs.</param>
/// <param name="NeutralObserved">An explicit neutral observation preceded this source's final value in the batch.</param>
public readonly record struct WorldInputContribution(ushort Handle, ushort Channel, WorldInputContributionKind Kind, long RawValue, ulong Activation, bool NeutralObserved = false);
/// <summary>One atomic input admission. Producers borrow preallocated buffers until the synchronous admission returns.</summary>
/// <param name="Revision">Authority-issued mapping revision.</param>
/// <param name="Epoch">Authority-issued input epoch.</param>
/// <param name="Mapping">Compiled tuple id.</param>
/// <param name="Tick">Processing tick.</param>
/// <param name="Image">Final held/displacement image, checked against contributions and events.</param>
/// <param name="Contributions">Complete sparse source image, including explicit neutral observations.</param>
/// <param name="Events">All transitions, in capture/sequence order.</param>
/// <param name="NumericCount">Exactly the selected mapping's used numeric extent.</param>
/// <param name="CaptureLost">The producer lost an atomic capture and requests an unconditional authority reset.</param>
public readonly record struct WorldInputBatch(uint Revision, ulong Epoch, ushort Mapping, ulong Tick,
    PlayerIntent Image, ReadOnlyMemory<WorldInputContribution> Contributions, ReadOnlyMemory<WorldInputEvent> Events, byte NumericCount = 0, bool CaptureLost = false);
/// <summary>A source's accepted state, persisted through replay and checkpoint.</summary>
/// <param name="Source">Registered contract.</param>
/// <param name="RawValue">Accepted level or latch.</param>
/// <param name="Activation">Last admitted physical activation.</param>
/// <param name="AwaitingNeutral">Released/rebound direct input cannot rearm through omission.</param>
public readonly record struct WorldInputSourceState(WorldInputSource Source, long RawValue, ulong Activation, bool AwaitingNeutral);
/// <summary>Original held identity, preserved across admitted continuity and history eviction.</summary>
/// <param name="Channel">Logical action.</param>
/// <param name="Press">Original event identity.</param>
/// <param name="Origin">Immutable original press and its calibrated time.</param>
/// <param name="Consumed">Whether the initiating edge has been consumed.</param>
public readonly record struct WorldInputHold(ushort Channel, ulong Press, WorldInputRecord Origin, bool Consumed);
/// <summary>A recorded barrier, distinct from a gameplay release.</summary>
/// <param name="Interval">Exact simulation commit interval.</param>
/// <param name="Epoch">New epoch.</param>
/// <param name="From">Old mapping.</param>
/// <param name="To">New mapping.</param>
/// <param name="ConsumedActivations">Physical activations whose edges are spent by the transition, in first-read order.</param>
/// <param name="Reset">Whether this was an unconditional owner reset.</param>
/// <param name="Decisions">Named action outcomes and original held identities at this boundary.</param>
public readonly record struct WorldInputSwitch(WorldInputInterval Interval, ulong Epoch, ushort From, ushort To, ReadOnlyMemory<ulong> ConsumedActivations, bool Reset,
    ReadOnlyMemory<WorldInputContinuation> Decisions) {
    /// <summary>At most one cause per retained event or independently continuing hold.</summary>
    public const int MaximumConsumedActivations = (ushort.MaxValue + ChannelLimits.MaxChannels);

    /// <summary>Simulation commit tick.</summary>
    public ulong Tick => Interval.Tick;
}
/// <summary>Why a previously active action continued or ended at an authoritative boundary.</summary>
public enum WorldInputContinuationReason : byte {
    /// <summary>The directed transition admitted this continuing action.</summary>
    Kept,
    /// <summary>The destination does not admit this action.</summary>
    Inactive,
    /// <summary>The directed transition did not declare continuity.</summary>
    Unlisted,
    /// <summary>The physical activation changed its destination set.</summary>
    Rebound,
    /// <summary>An unconditional owner reset ended the action.</summary>
    Reset,
}
/// <summary>One action's admitted values and original hold at a boundary; raw units come from its logical declaration.</summary>
public readonly record struct WorldInputContinuation(ushort Channel, long PreviousRaw, long CurrentRaw,
    WorldInputContinuationReason Reason, ulong HoldPress, Puck.Maths.FixedQ4816 HeldSince);
