using Puck.Commands;
using Puck.Networking;
using Puck.World.Protocol;

namespace Puck.World.Server;

public static partial class WorldAuthorityCheckpointCodec {
    // Empty timer slots have no actor. World is authority-local and remains forbidden on the live wire.
    private static void WriteScriptActor(WireWriter writer, Principal actor) {
        writer.WriteBoolean(actor != default);
        if (actor == default) { return; }
        writer.WriteBoolean(actor.Kind == PrincipalKind.World);
        if (actor.Kind != PrincipalKind.World && !WorldWireCodec.TryWritePrincipal(writer, actor)) {
            throw new ArgumentException("script input actor is invalid");
        }
    }
    private static Principal ReadScriptActor(ref WireReader reader) => !reader.ReadBoolean() ? default :
        reader.ReadBoolean() ? Principal.World : WorldWireCodec.ReadPrincipal(ref reader);
}
