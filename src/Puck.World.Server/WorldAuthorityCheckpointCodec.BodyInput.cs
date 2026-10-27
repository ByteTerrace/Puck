using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteBodyInputState(WireWriter writer, WorldBodyInputState state) {
        WorldWireCodec.WriteIntent(writer, state.Submitted); writer.WriteBoolean(state.HasSubmitted);
        WorldWireCodec.WriteIntent(writer, state.Held);
        WorldWireCodec.WriteIntent(writer, state.Producer); writer.WriteBoolean(state.HasProducer);
        WorldWireCodec.WriteIntent(writer, state.TransferHeld); writer.WriteBoolean(state.HasTransferHeld);
        WorldWireCodec.WriteIntent(writer, state.ReadHeld); WorldWireCodec.WriteIntent(writer, state.ReadComposed);
        WorldWireCodec.WriteIntent(writer, state.ReadScalar); writer.WriteUInt16(state.Mapping); writer.WriteUInt64(state.Epoch);
    }

    private static WorldBodyInputState ReadBodyInputState(ref WireReader reader) => new(
        WorldWireCodec.ReadIntent(ref reader), reader.ReadBoolean(), WorldWireCodec.ReadIntent(ref reader),
        WorldWireCodec.ReadIntent(ref reader), reader.ReadBoolean(), WorldWireCodec.ReadIntent(ref reader), reader.ReadBoolean(),
        WorldWireCodec.ReadIntent(ref reader), WorldWireCodec.ReadIntent(ref reader), WorldWireCodec.ReadIntent(ref reader),
        reader.ReadUInt16(), reader.ReadUInt64());
}
