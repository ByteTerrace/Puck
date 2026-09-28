using Puck.World.Protocol;

namespace Puck.World.Server;

public sealed partial class WorldDocument {
    // The verb a rebuild answers under.
    private static string RebuildVerb(WorldRebuildKind kind) => kind switch {
        WorldRebuildKind.Reset => "world.reset",
        WorldRebuildKind.Load => "world.load",
        WorldRebuildKind.Reload => "world.reload",
        _ => throw new ArgumentOutOfRangeException(
            paramName: nameof(kind),
            actualValue: kind,
            message: $"no verb for rebuild kind '{kind}'."
        ),
    };

    /// <summary>Answers every buffered op with a refusal naming why the activation stopped, and empties the buffer:
    /// each op is answered the way a refusal at the tick boundary answers it — the edit echo, the tape's outcome, the
    /// addon's answer cell, and the submitter's typed completion.</summary>
    /// <param name="reason">Why the activation stopped.</param>
    internal void RefusePending(string reason) {
        while (m_pending.TryDequeue(result: out var op)) {
            switch (op) {
                case WorldPendingOp.Mutate mutate:
                    Reject(
                        connectionId: mutate.ConnectionId,
                        correlationId: mutate.CorrelationId,
                        mutation: mutate.Mutation,
                        reason: reason
                    );
                    mutate.OutcomeObserved?.Invoke(obj: false);

                    if (mutate.SourceAddonInstanceId >= 0L) {
                        Host.Addons?.CompleteMutation(
                            actOrdinal: mutate.ActOrdinal,
                            addonInstanceId: mutate.SourceAddonInstanceId,
                            applied: false
                        );
                    }

                    mutate.Completion?.Invoke(obj: ((mutate.Binding is { } binding)
                        ? new WorldSubmissionResult.Mutation(Outcome: WorldMutationOutcome.RefusedOutcome(
                            binding: binding,
                            code: WorldServer.StoppedCode,
                            detail: reason,
                            version: Host.DocumentVersion
                        ))
                        : new WorldSubmissionResult.Refusal(Code: WorldServer.StoppedCode, Detail: reason)));

                    break;
                case WorldPendingOp.Rebuild rebuild:
                    RejectRebuild(
                        connectionId: rebuild.ConnectionId,
                        correlationId: rebuild.CorrelationId,
                        reason: reason,
                        verb: RebuildVerb(kind: rebuild.Request.Kind)
                    );

                    break;
                case WorldPendingOp.Undo undo:
                    Host.EchoTap?.Invoke(obj: new WorldEditEcho(
                        ConnectionId: undo.ConnectionId,
                        CorrelationId: undo.CorrelationId,
                        Kind: WorldEditEchoKind.Mutation,
                        Message: $"undo refused: {reason}",
                        Rejected: true
                    ));

                    break;
            }
        }
    }
}
