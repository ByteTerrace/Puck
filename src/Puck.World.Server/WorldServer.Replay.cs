namespace Puck.World.Server;

public sealed partial class WorldServer {
    // The replay tape holds ExecuteAuthorityOperation across this check and the reset. A federation reserve or
    // commit cannot create a new obligation between the check and replacement of the authority checkpoint.
    internal string? ReplayTimelineResetRefusal() {
        if (m_social is { } social && (social.FrozenObserverCount != 0 || social.ImportReservationCount != 0)) {
            return "social ownership holds or import reservations are unresolved — resolve the transfer before replaying";
        }
        if (m_transferEscrow.Counts != default) {
            return "transfer transactions or mobility credentials depend on this timeline — replay in an isolated session";
        }
        for (var index = 0; index < m_population.Capacity; index++) {
            if (m_population.IsAdmittedPeer(index) || m_population.PeerAuthorityTransferred(index)) {
                return "a remote connection or transferred body depends on this timeline — replay in an isolated session";
            }
        }
        return TransferForwarder?.TimelineResetRefusal(this);
    }
}
