using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteRoutedInput(WireWriter writer, WorldRoutedInput route) {
        writer.WriteInt32(value: route.SourceBody); writer.WriteInt32(value: route.SourceGeneration);
        writer.WriteInt32(value: route.TargetBody); writer.WriteInt32(value: route.TargetGeneration);
        writer.WriteInt32(value: route.PopulationRevision); WritePrincipal(writer, route.Principal);
        writer.WriteUInt64(value: route.Revision); writer.WriteUInt64(value: route.SourceEpoch); writer.WriteUInt64(value: route.TargetEpoch);
        writer.WriteUInt16(value: route.Mapping); WorldWireCodec.WriteIntent(writer, route.Image);
    }
    private static WorldRoutedInput ReadRoutedInput(ref WireReader reader) => new(
        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
        WorldWireCodec.ReadPrincipal(ref reader), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadUInt16(), WorldWireCodec.ReadIntent(reader: ref reader));
}
