using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldInstanceHost {
    /// <inheritdoc/>
    public string? TimelineResetRefusal(WorldServer source) {
        foreach (var instance in m_instances.Values) {
            if (!ReferenceEquals(
                objA: instance.Server,
                objB: source
            )) { continue; }
            foreach (var pending in m_pendingTransfers) {
                if (
                    (pending.SourceInstance == instance.Name) ||
                    (pending.Destination.Name == instance.Name)
                ) {
                    return "an inbound or outbound transfer is queued — drain transfers before replaying";
                }
            }
            foreach (var pending in m_inDoubtTransfers) {
                if (
                    (pending.Transfer.SourceInstance == instance.Name) ||
                    (pending.TargetName == instance.Name)
                ) {
                    return "an inbound or outbound transfer is in doubt — resolve its outcome before replaying";
                }
            }
            foreach (var pair in m_forwardedBodies) {
                if (ReferenceEquals(
                    objA: pair.Key.Server,
                    objB: source
                )) {
                    return "a departed traveler's forwarding route depends on this timeline — replay in an isolated session";
                }
            }
            // A rewind keeps this row's transfer counter, drained ids and occupancy latch: ids only climb, so none is
            // minted twice, and a replaying row's scan keeps its latch current while minting nothing.
            break;
        }
        return null;
    }
}
