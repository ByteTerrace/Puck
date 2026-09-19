using Puck.World.Protocol;

namespace Puck.World.Server;

/// <summary>One buffered live-edit op, drained FIFO at the step boundary before intents. Each retains the submitting
/// envelope's connection and correlation identity, so its eventual <see cref="WorldEditEcho"/> — fired later, from
/// inside the pending drain rather than at submit time — still names the right submitter.</summary>
public abstract record WorldPendingOp {
    /// <summary>One buffered document mutation.</summary>
    /// <param name="Mutation">The mutation to apply.</param>
    /// <param name="ConnectionId">The submitting envelope's connection id.</param>
    /// <param name="CorrelationId">The submitting envelope's correlation id.</param>
    /// <param name="SourceAddonInstanceId">The mounted guest's own stable instance token, or <c>-1</c> for every
    /// non-addon submitter. Never a positional index: a queued removal or reorder draining ahead of this op must not
    /// deliver its completion to whatever guest now sits where the source guest used to.</param>
    /// <param name="ActOrdinal">The guest act this op completes, or <c>0</c> for a non-addon submitter. The op is
    /// routed to the addon runtime's completion, never applied there.</param>
    /// <param name="OutcomeObserved">The tape's completion field — non-null only for the one dispatch point the
    /// mutation tap already covers, invoked exactly once, right after this op's own apply outcome is known.</param>
    /// <param name="Binding">The submitted binding, when the mutation carries one.</param>
    /// <param name="Completion">The submitter's typed result callback, or <see langword="null"/>.</param>
    public sealed record Mutate(WorldMutation Mutation, int ConnectionId, long CorrelationId, long SourceAddonInstanceId = -1L, ushort ActOrdinal = 0, Action<bool>? OutcomeObserved = null, WorldMutationBinding? Binding = null, Action<WorldSubmissionResult>? Completion = null) : WorldPendingOp;
    /// <summary>One buffered whole-document rebuild-and-swap.</summary>
    /// <param name="Request">The rebuild request.</param>
    /// <param name="Principal">The acting principal.</param>
    /// <param name="ConnectionId">The submitting envelope's connection id.</param>
    /// <param name="CorrelationId">The submitting envelope's correlation id.</param>
    /// <param name="ExpectedContentHash">The candidate's CAS content hash, when the submitter pinned one.</param>
    /// <param name="PreparationFailure">The refusal the synchronous preparation already decided, when it failed.</param>
    public sealed record Rebuild(WorldRebuildRequest Request, WorldPrincipal Principal, int ConnectionId, long CorrelationId, string? ExpectedContentHash = null, string? PreparationFailure = null) : WorldPendingOp;
    /// <summary>One buffered journal undo.</summary>
    /// <param name="Count">How many journal entries to drop from the tail.</param>
    /// <param name="Principal">The acting principal.</param>
    /// <param name="ConnectionId">The submitting envelope's connection id.</param>
    /// <param name="CorrelationId">The submitting envelope's correlation id.</param>
    public sealed record Undo(int Count, WorldPrincipal Principal, int ConnectionId, long CorrelationId) : WorldPendingOp;
}
