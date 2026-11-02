using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldStateCommandModule {
    private static CommandResult DescribeHash(WorldServer server, WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[world.state.hash: expected [capture|pose|world|authoritative]]");
        }
        var token = args.Count == 0 ? "capture" : args[0].ToString();
        var scope = token switch {
            "capture" => WorldStateHashScope.Capture,
            "pose" => WorldStateHashScope.Pose,
            "world" => WorldStateHashScope.World,
            "authoritative" => WorldStateHashScope.Authoritative,
            _ => (WorldStateHashScope?)null,
        };
        if (scope is null) {
            return CommandResult.Error(output: $"[world.state.hash: unknown scope '{token}' — expected capture|pose|world|authoritative]");
        }
        ulong tick;
        ulong hash;

        // The tick and its hash are read at one settled boundary, the one every state hash folds under.
        lock (server.AuthorityGate) {
            tick = (server.NextInputTick - 1UL);
            hash = WorldStateHashComposition.Hash(scope: scope.Value, server: server, tick: tick);
        }
        return new CommandResult(Output: args.Count == 0
            ? string.Create(CultureInfo.InvariantCulture, $"[world.state.hash: tick={tick} hash={hash:x16}]")
            : string.Create(CultureInfo.InvariantCulture, $"[world.state.hash: scope={token} tick={tick} hash={hash:x16}]"));
    }
}
