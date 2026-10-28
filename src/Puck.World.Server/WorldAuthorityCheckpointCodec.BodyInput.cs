using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteBodyInputState(WireWriter writer, WorldBodyInputState state) {
        WorldWireCodec.WriteIntent(writer, state.Submitted); writer.WriteBoolean(value: state.HasSubmitted);
        WorldWireCodec.WriteIntent(writer, state.Held);
        WorldWireCodec.WriteIntent(writer, state.Producer); writer.WriteBoolean(value: state.HasProducer);
        WorldWireCodec.WriteIntent(writer, state.TransferHeld); writer.WriteBoolean(value: state.HasTransferHeld);
        WorldWireCodec.WriteIntent(writer, state.ReadHeld); WorldWireCodec.WriteIntent(writer, state.ReadComposed);
        WorldWireCodec.WriteIntent(writer, state.ReadScalar); writer.WriteUInt16(value: state.Mapping); writer.WriteUInt64(value: state.Epoch);
    }
    private static WorldBodyInputState ReadBodyInputState(ref WireReader reader) => new(
        WorldWireCodec.ReadIntent(reader: ref reader), reader.ReadBoolean(), WorldWireCodec.ReadIntent(reader: ref reader),
        WorldWireCodec.ReadIntent(reader: ref reader), reader.ReadBoolean(), WorldWireCodec.ReadIntent(reader: ref reader), reader.ReadBoolean(),
        WorldWireCodec.ReadIntent(reader: ref reader), WorldWireCodec.ReadIntent(reader: ref reader), WorldWireCodec.ReadIntent(reader: ref reader),
        reader.ReadUInt16(), reader.ReadUInt64());
}
