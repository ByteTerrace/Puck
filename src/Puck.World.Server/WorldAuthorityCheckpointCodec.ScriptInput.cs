using Puck.Commands;
using Puck.Networking;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    // Empty timer slots have no actor. World is authority-local and remains forbidden on the live wire.
    internal static void WriteScriptActor(WireWriter writer, Principal actor) {
        writer.WriteBoolean(value: (actor != default));
        if (actor == default) { return; }
        WriteProgramOrPrincipal(
            actor: actor,
            writer: writer
        );
    }
    internal static Principal ReadScriptActor(ref WireReader reader) => (!reader.ReadBoolean() ? default : ReadProgramOrPrincipal(reader: ref reader));
}
