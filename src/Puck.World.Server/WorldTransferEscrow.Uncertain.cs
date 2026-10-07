namespace Puck.World.Server;

public sealed partial class WorldTransferEscrow {
    // Arrivals whose crossing record this activation's log answered Uncertain. Each cohort was unlanded, but its
    // record may already be durable, so this activation can neither embody it nor refuse it: it answers Uncertain for
    // the transfer and refuses any reservation for its travelers. The set lives only as long as the activation; the
    // recovered one answers from what its log holds.
    private readonly Dictionary<WorldTransferKey, WorldCrossingArrival> m_uncertain = new();

    private WorldTransferStatus HoldUncertain(WorldCrossingArrival arrival, string detail, out string reason) {
        m_uncertain[arrival.Key] = arrival;
        reason = $"transfer {arrival.Key.TransferId} arrival record is uncertain ({detail}); its travelers stay suspended until this authority recovers";
        return WorldTransferStatus.Uncertain;
    }
    private bool IsUncertain(WorldTransferKey key, out string reason) {
        if (!m_uncertain.ContainsKey(key: key)) {
            reason = string.Empty;
            return false;
        }
        reason = $"transfer {key.TransferId} arrival record is uncertain; only this authority's recovery resolves it";
        return true;
    }
    private bool IsSuspended(WorldTransferReservationRequest request, out string reason) {
        var key = new WorldTransferKey(
            SourceAuthority: request.SourceAuthority,
            TransferId: request.TransferId
        );

        if (IsUncertain(
            key: key,
            reason: out reason
        )) {
            return true;
        }
        foreach (var arrival in m_uncertain.Values) {
            foreach (var held in arrival.Request.Members) {
                for (var index = 0; (index < request.Members.Count); index++) {
                    if (
                        (held.Mobility is { } heldMobility) &&
                        (request.Members[index].Mobility?.Incarnation == heldMobility.Incarnation)
                    ) {
                        reason = $"traveler {(index + 1)} is suspended: transfer {arrival.Key.TransferId} left its arrival here uncertain";
                        return true;
                    }
                }
            }
        }
        return false;
    }
}
