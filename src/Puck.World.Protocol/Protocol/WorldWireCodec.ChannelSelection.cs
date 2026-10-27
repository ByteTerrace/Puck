using Puck.Networking;

namespace Puck.World.Protocol;

public static partial class WorldWireCodec {
    /// <summary>Writes an authored logical selection. Physical slot masks never cross a grant persistence boundary.</summary>
    public static void WriteChannelSelection(WireWriter writer, WorldChannelSelection selection) {
        writer.WriteArray(selection.Channels, static (w, name) => w.WriteString(name));
        writer.WriteUInt64(selection.Registers);
    }

    /// <summary>Reads a bounded logical selection shared by submissions, replays and authority checkpoints.</summary>
    public static WorldChannelSelection ReadChannelSelection(ref WireReader reader) {
        var names = reader.ReadArray("channel selection", static (ref WireReader r) => r.ReadRequiredString("channel name"), WorldInputCatalog.MaximumLogicalChannels);
        var registers = reader.ReadUInt64();
        return new(names, registers);
    }
}
