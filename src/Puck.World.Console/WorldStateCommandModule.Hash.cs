using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldStateCommandModule {
    private static readonly CommandForms WorldStateHashForms = CommandForms.Of(verb: "world.state.hash", "[capture|pose|world|authoritative|components]");
    private static CommandResult DescribeHash(WorldServer server, WireArgs args) {
        if (args.Count > 1) {
            return WorldStateHashForms.Refuse();
        }
        var token = args.Count == 0 ? "capture" : args[0].ToString();
        if (token == "components") {
            return DescribeComponents(server: server);
        }
        var scope = token switch {
            "capture" => WorldStateHashScope.Capture,
            "pose" => WorldStateHashScope.Pose,
            "world" => WorldStateHashScope.World,
            "authoritative" => WorldStateHashScope.Authoritative,
            _ => (WorldStateHashScope?)null,
        };
        if (scope is null) {
            return WorldStateHashForms.Refuse(got: token);
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
    // Each authoritative component's own digest, in fold order: what names the component two runs disagree in.
    private static CommandResult DescribeComponents(WorldServer server) {
        ulong tick;
        ulong[] digests;

        lock (server.AuthorityGate) {
            tick = (server.NextInputTick - 1UL);
            digests = WorldStateHashComposition.AuthoritativeComponents(server: server, tick: tick);
        }

        var parts = new string[digests.Length];

        for (var index = 0; (index < digests.Length); index++) {
            parts[index] = string.Create(CultureInfo.InvariantCulture, $"{WorldStateHashComposition.Authoritative[index]}={digests[index]:x16}");
        }

        return new CommandResult(Output: string.Create(CultureInfo.InvariantCulture, $"[world.state.hash: components tick={tick} {string.Join(separator: ' ', value: parts)}]"));
    }
}
