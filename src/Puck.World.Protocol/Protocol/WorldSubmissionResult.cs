using Puck.Abstractions.Machines;
namespace Puck.World.Protocol;

/// <summary>
/// The closed completion-result union every <see cref="SubmissionEnvelope"/> resolves to — no <c>IServerLink</c>
/// submission returns a value directly any more; a local submitter gets this inline, on the tick thread, before its
/// <c>Submit*</c> call returns (a remote submitter will get it as a Completion frame once the wire lands). Most
/// payload kinds complete with the value-free <see cref="Ack"/>; mutation, session, query, and machine-operation submissions carry typed
/// data back, and each gets its own case rather than a shared "object result" box. A malformed or retiring ingress
/// can resolve as <see cref="Refusal"/> without fabricating a mutation binding.
/// </summary>
public abstract record WorldSubmissionResult {
    private WorldSubmissionResult() {
    }

    /// <summary>The value-free completion for non-mutation payloads that do not carry a typed reply. Mutation
    /// decisions use <see cref="Mutation"/>; malformed or retiring ingress uses <see cref="Refusal"/>. The legacy
    /// outcome (accepted/rejected, and why) is still reported LOUDLY on stderr and through <c>WorldServer.EchoTap</c>
    /// exactly as before; this case only says "the envelope finished draining."</summary>
    public sealed record Ack : WorldSubmissionResult {
        /// <summary>The single shared instance — value-free, so one instance serves every completion.</summary>
        public static readonly Ack Instance = new();
    }
    /// <summary>The server's reply to a <see cref="Protocol.WorldSubmissionPayload.Session"/> submission.</summary>
    /// <param name="Reply">The session reply.</param>
    public sealed record Session(SessionReply Reply) : WorldSubmissionResult;
    /// <summary>The server's composed answer to a <see cref="Protocol.WorldSubmissionPayload.Query"/> submission.</summary>
    /// <param name="Answer">The query answer.</param>
    public sealed record Query(QueryAnswer Answer) : WorldSubmissionResult;

    /// <summary>The typed applied/refused completion for a generic named-machine operation.</summary>
    /// <param name="Result">The provider result, including its optional value and diagnostic reason.</param>
    public sealed record MachineOperation(MachineOperationResult Result) : WorldSubmissionResult;

    /// <summary>The typed applied/refused completion for a <see cref="Protocol.WorldSubmissionPayload.Mutation"/>.</summary>
    /// <param name="Outcome">The actor-bound decision and independent persistence status.</param>
    public sealed record Mutation(WorldMutationOutcome Outcome) : WorldSubmissionResult;

    /// <summary>A named ingress or transport refusal for which no canonical mutation outcome could be formed.</summary>
    /// <param name="Code">The stable refusal code.</param>
    /// <param name="Detail">The human-readable refusal detail.</param>
    public sealed record Refusal(string Code, string Detail) : WorldSubmissionResult;
}
