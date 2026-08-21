using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>The seam <see cref="WorldServer"/> and <see cref="WorldReplaySnapshot"/> pump the mounted addon guests
/// through — every member either project calls on the concrete host today. <c>Addons.WorldAddonRuntime</c> is the
/// one implementation; it also carries <c>DescribeCost</c> on its concrete surface, deliberately not on this
/// interface, because it returns <c>Puck.Scripting</c> shapes (<c>AddonCostReport</c> embeds <c>AddonState</c>)
/// that this project must not name — its caller holds the concrete type as a composition-root DI dependency
/// instead. <see cref="IDisposable"/> because <see cref="WorldReplaySnapshot"/>'s offline shadow-world re-drive
/// owns a fresh host per drive (<c>using var addons = addonHostFactory(...)</c>).</summary>
public interface IWorldAddonHost : IDisposable {
    /// <summary>Whether any mounted guest has ever had an admitted <c>TickAddons</c> pump — the boot-anchored replay
    /// arm predicate.</summary>
    bool AnyEverPumped { get; }
    /// <summary>The number of guests currently mounted — the sizing bound
    /// <see cref="WorldServer.AttachAddons"/> reads for its per-tick contention tracking.</summary>
    int MountedCount { get; }
    /// <summary>Every mounted guest's recorded-at-mount identity, in mount order.</summary>
    IReadOnlyList<WorldAddonReceipt> Receipts { get; }

    /// <summary>Resolves Drive handles, checks authority, and submits the folded intent for every mounted guest —
    /// the second of the three tick-boundary pump points, run after the intent drain.</summary>
    /// <param name="tick">The tick about to advance.</param>
    void ApplyContributions(ulong tick);
    /// <summary>Publishes a plan <see cref="TryPrepare"/> already proved fallible-free by reference adoption alone —
    /// no I/O, allocation, compilation, guest execution, type dispatch beyond the plan's own downcast, or
    /// recoverable failure. Never prints narration and never disposes a superseded guest; see <see cref="Finish"/>
    /// for both, which the caller runs afterward, once its own document/journal publication is itself durable.</summary>
    /// <param name="plan">A plan <see cref="TryPrepare"/> returned and the caller has not yet disposed.</param>
    void Commit(IWorldAddonPreparedPlan plan);
    /// <summary>Emits a committed plan's deferred narration (mount lines, capability disclosures) and disposes
    /// every guest the plan superseded. Called exactly once per committed plan, strictly after
    /// <see cref="Commit"/> and after the caller's own document/journal publication, so neither can still be
    /// unwound by what this step does — a plan that never reached <see cref="Commit"/> must never reach this
    /// method either.</summary>
    /// <param name="plan">The plan a prior <see cref="Commit"/> call already published.</param>
    void Finish(IWorldAddonPreparedPlan plan);
    /// <summary>Stages the wire verdict for one addon-sourced mutation into its originating guest's reserved answer
    /// cell, at drain time — never the mutation's application, which already ran through the same door a console
    /// mutation runs through. Addressed by the mounted instance's own stable token rather than its current position
    /// among mounted guests, so a queued removal or reorder that drains first cannot deliver the completion to a
    /// different guest.</summary>
    /// <param name="addonInstanceId">The mounted addon instance token the mutation was decoded from.</param>
    /// <param name="actOrdinal">The guest-assigned act ordinal the answer cell is keyed by.</param>
    /// <param name="applied">Whether the document-apply pipeline accepted the decoded mutation.</param>
    void CompleteMutation(long addonInstanceId, ushort actOrdinal, bool applied);
    /// <summary>Reports the channels a co-driving grant confers on <paramref name="principal"/> that the guest never
    /// declares, or <see langword="null"/> when every granted channel is declared, the principal names no mounted
    /// guest, or the grant carries no channel mask.</summary>
    /// <param name="principal">The principal the grant confers on.</param>
    /// <param name="reach">The grant's channel reach, when it carries one.</param>
    /// <param name="channels">The world's channel table, for naming the ordinals.</param>
    string? DescribeUndeclaredGrantedChannels(WorldPrincipal principal, ChannelReachMask? reach, WorldChannelTable channels);
    /// <summary>Writes the guest's input ring, runs <c>puck_on_tick</c>, and decodes/vocabulary-validates its output
    /// — the first of the three tick-boundary pump points, run at the very top.</summary>
    /// <param name="tick">The tick about to advance.</param>
    void TickAddons(ulong tick);
    /// <summary>Prepares the whole addon-runtime delta between <paramref name="current"/> and
    /// <paramref name="candidate"/> — module resolve, hash pin, compile, ABI admit, instantiate, and <c>puck_init</c>
    /// against every changed or newly-enabled row's own private memory, plus every allocation the resulting runtime
    /// state needs. Nothing observable moves: an unchanged live guest is neither recompiled nor re-admitted, and
    /// nothing is registered, disclosed, or journaled until <see cref="Commit"/> runs. An enabled row that cannot
    /// prepare refuses the WHOLE call — the caller's mutation, undo entry, or boot install — with the candidate
    /// document untouched; a disabled row is never compiled.</summary>
    /// <param name="current">The live definition the currently-mounted guests were prepared under — a candidate row
    /// reuses its guest only when it is STRUCTURALLY equal (every field, including <c>Requests</c>/<c>MemoryWatches</c>
    /// content) to the row that guest was last prepared under AND every other preparation dependency (the channel
    /// table content) is unchanged; <see langword="null"/> when there is no prior state to reuse from (a fresh boot
    /// install).</param>
    /// <param name="candidate">The candidate definition being considered for install.</param>
    /// <param name="plan">The disposable prepared plan, on success; the caller commits it or disposes it — never
    /// both, and never neither.</param>
    /// <param name="reason">Why preparation refused, on failure.</param>
    /// <returns><see langword="true"/> when every enabled row prepared.</returns>
    bool TryPrepare(WorldDefinition? current, WorldDefinition candidate, out IWorldAddonPreparedPlan? plan, out string? reason);
    /// <summary>Resolves each guest's disclosures, world-event pushes, and queued asks/pose queries — pump point 3,
    /// after the population advances and before the snapshot is emitted. This is the pinned
    /// drain point: a verdict, a minted handle, and a pose all reflect the grant table and the authoritative state as of
    /// the step of the tick the record was written in. Disclosures are pushed first (the guest's bootstrap — enumeration
    /// is itself a capability, so a guest cannot know a body index until the host hands it one), then world events
    /// (four families plus the guest's own machine-memory watches), then asks and pose queries are answered, and the
    /// whole result is budgeted into the guest's input batch for the next tick.</summary>
    /// <param name="tick">The tick that just advanced.</param>
    void ResolveReads(ulong tick);
}
