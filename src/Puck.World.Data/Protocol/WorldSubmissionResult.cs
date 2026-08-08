namespace Puck.World.Protocol;

/// <summary>
/// The closed completion-result union every <see cref="SubmissionEnvelope"/> resolves to — no <c>IServerLink</c>
/// submission returns a value directly any more; a local submitter gets this inline, on the tick thread, before its
/// <c>Submit*</c> call returns (a remote submitter will get it as a Completion frame once the wire lands). Most
/// payload kinds complete with the value-free <see cref="Ack"/>; <see cref="Protocol.WorldSubmissionPayload.Session"/>
/// and <see cref="Protocol.WorldSubmissionPayload.Query"/> are the two kinds that carry data back, and each gets its
/// own typed case rather than a shared "object result" box.
/// </summary>
public abstract record WorldSubmissionResult {
    private WorldSubmissionResult() {
    }

    /// <summary>The value-free completion — every payload kind except <see cref="Session"/>/<see cref="Query"/>
    /// resolves to this. The outcome (accepted/rejected, and why) is still reported LOUDLY on stderr and through
    /// <c>WorldServer.EchoTap</c> exactly as before; this case only says "the envelope finished draining."</summary>
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
}
