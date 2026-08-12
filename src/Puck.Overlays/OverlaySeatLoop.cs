namespace Puck.Overlays;

internal interface IOverlaySeatEmitter<TSeat> {
    void EmitSeat(OverlayFrameBuilder builder, in TSeat seat);
}
internal static class OverlaySeatLoop {
    public static void Emit<TWriter, TSeat>(OverlayFrameBuilder builder, ReadOnlySpan<TSeat> seats, string writerName, TWriter writer)
        where TWriter : IOverlaySeatEmitter<TSeat> {
        OverlayChannelLeases.EnsureSeatCapacity(
            seatCount: seats.Length,
            writerName: writerName
        );

        for (var index = 0; (index < seats.Length); index++) {
            writer.EmitSeat(
                builder: builder,
                seat: in seats[index]
            );
        }
    }
}
