using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    private static void WriteRoutedInput(WireWriter writer, WorldRoutedInput route) {
        writer.WriteInt32(route.SourceBody); writer.WriteInt32(route.SourceGeneration);
        writer.WriteInt32(route.TargetBody); writer.WriteInt32(route.TargetGeneration);
        writer.WriteInt32(route.PopulationRevision); WritePrincipal(writer, route.Principal);
        writer.WriteUInt64(route.Revision); writer.WriteUInt64(route.SourceEpoch); writer.WriteUInt64(route.TargetEpoch);
        writer.WriteUInt16(route.Mapping); WorldWireCodec.WriteIntent(writer, route.Image);
    }

    private static WorldRoutedInput ReadRoutedInput(ref WireReader reader) => new(
        reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(),
        WorldWireCodec.ReadPrincipal(ref reader), reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadUInt64(),
        reader.ReadUInt16(), WorldWireCodec.ReadIntent(ref reader));
}
