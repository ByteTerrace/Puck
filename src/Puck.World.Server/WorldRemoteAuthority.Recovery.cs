using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldRemoteAuthority {
    private static WorldTransferStep DecodeCommitAnswer(WorldFederationAnswer answer, out WorldTransferStatus status, out string reason) {
        status = WorldTransferStatus.Missing;
        if (
            !answer.Ok ||
            (answer.Kind != WorldFederationResponse.Commit)
        ) {
            reason = answer.Describe();
            return WorldTransferStep.Unreachable;
        }
        if (!WorldFederationCodec.TryDecodeCommitReply(
            answer.Body.Span,
            out status,
            out reason,
            out var failure
        )) {
            status = WorldTransferStatus.Missing;
            reason = $"invalid commit reply: {failure}";
            return WorldTransferStep.Unreachable;
        }
        return WorldTransferStep.Answered;
    }
}
