namespace Puck.World;

public sealed partial class WorldInstanceHost {
    private byte CaptureFollowedSeats(string sourceInstance, int sourceSlot) {
        byte mask = 0;
        for (var slot = 0; slot < m_seats.SeatCount; slot++) {
            if (m_seats.RoutedEndpoint(slot)?.Identity == sourceInstance && m_seats.RoutedEntity(slot).Index == sourceSlot) {
                mask |= checked((byte)(1 << slot));
            }
        }
        return mask;
    }

    private bool TryPublishCommittedTransfer(InDoubtTransfer pending) {
        if (!m_instances.ContainsKey(pending.Transfer.SourceInstance)) { return false; }
        try {
            var transfer = pending.Transfer;
            PublishCommittedTransfer(in transfer, pending.TargetAuthority!.Value, pending.TargetName, pending.Landed);
            return true;
        } catch (Exception exception) when (exception is IOException or System.Net.Sockets.SocketException or OperationCanceledException) {
            if (!pending.PublicationFailureReported) {
                pending.PublicationFailureReported = true;
                Console.Error.WriteLine($"[world.transfer: transfer={pending.Transfer.TransferId} PUBLICATION-PENDING — commit confirmed; source completion retained ({exception.GetType().Name}: {exception.Message})]");
            }
            return false;
        }
    }
}
