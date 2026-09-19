namespace Puck.World.Server;

public sealed partial class WorldPersistence {
    // The replay tape holds ExecuteAuthorityOperation across this check and the reset. A federation reserve or
    // commit cannot create a new obligation between the check and replacement of the authority checkpoint.
    internal string? ReplayTimelineResetRefusal() {
        if (Host.Extensions.ExternalOperationsInFlight != 0) {
            return "external operations are in flight — wait for dispatch to settle or replay in an isolated session";
        }
        if (Host.TransferEscrow.Counts != default) {
            return "transfer transactions or mobility credentials depend on this timeline — replay in an isolated session";
        }
        for (var index = 0; (index < Host.Population.Capacity); index++) {
            if (
                Host.Population.IsAdmittedPeer(bodyIndex: index) ||
                Host.Population.PeerAuthorityTransferred(bodyIndex: index)
            ) {
                return "a remote connection or transferred body depends on this timeline — replay in an isolated session";
            }
        }
        return Host.TransferForwarder?.TimelineResetRefusal(source: Host);
    }
}
