using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    /// <inheritdoc/>
    public string? TimelineResetRefusal(WorldServer source) {
        foreach (var instance in m_instances.Values) {
            if (!ReferenceEquals(instance.Server, source)) { continue; }
            foreach (var pending in m_pendingTransfers) {
                if (pending.SourceInstance == instance.Name || pending.Destination.Name == instance.Name) {
                    return "an inbound or outbound transfer is queued — drain transfers before replaying";
                }
            }
            foreach (var pending in m_inDoubtTransfers) {
                if (pending.Transfer.SourceInstance == instance.Name || pending.TargetName == instance.Name) {
                    return "an inbound or outbound transfer is in doubt — resolve its outcome before replaying";
                }
            }
            foreach (var pair in m_forwardedBodies) {
                if (ReferenceEquals(pair.Key.Server, source)) {
                    return "a departed traveler's forwarding route depends on this timeline — replay in an isolated session";
                }
            }
            // The tape owns one authority's inputs, not the host's transfer-id, resolver, or occupancy history.
            if (instance.NextTransferId != 0) {
                return "this host row has transfer history outside the tape — replay in an isolated session";
            }
            break;
        }
        return null;
    }
}
