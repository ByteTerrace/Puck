using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>captures</c> section's read-back surface: <c>world.captures</c> (the authored schedule) and
/// <c>world.state.hash</c> (a named deterministic state summary, defaulting to the historical capture digest
/// <see cref="WorldCaptureScheduler"/> stamps into a manifest entry).
/// </summary>
internal sealed class WorldCaptureCommandModule(WorldServer server) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.captures",
            description: "Reads the document's captures section (Immediate, no arguments): the resolved output directory and every station's declared tick schedule and palette-entry count. Absent captures reports an empty schedule, never a refusal.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.captures") is { } refusal)
            ? refusal
            : Describe())
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state.hash",
            description: "Reads a live deterministic state hash (Immediate): world.state.hash [capture|pose|world|authoritative]. The default capture scope is byte-for-byte compatible with manifests; authoritative additionally covers rule latches, body/identity action state, and field-lattice cells.",
            handler: (_, args) => Hash(args: args)
        );
    }

    private CommandResult Describe() {
        var captures = server.Definition.Captures;

        if (captures is not { Rows: { Count: > 0 } rows }) {
            return new CommandResult(Output: "[world.captures: no schedule authored]");
        }

        var directory = WorldCaptureRoot.Resolve(authored: captures.Directory);
        var lines = new List<string>(capacity: (1 + rows.Count)) {
            $"[world.captures: directory {directory} stations {rows.Count}]",
        };

        foreach (var row in rows) {
            var ticks = string.Join(
                separator: ",",
                values: row.Ticks
            );

            lines.Add(item: $"  {row.Station}: ticks=[{ticks}] palette={row.Palette.Count}");
        }

        return new CommandResult(Output: string.Join(
            separator: Environment.NewLine,
            values: lines
        ));
    }
    private CommandResult Hash(WireArgs args) {
        if (args.Count > 1) {
            return CommandResult.Error(output: "[world.state.hash: expected [capture|pose|world|authoritative]]");
        }

        var token = ((args.Count == 0) ? "capture" : args[0].ToString());
        var scope = token switch {
            "capture" => WorldStateHashScope.Capture,
            "pose" => WorldStateHashScope.Pose,
            "world" => WorldStateHashScope.World,
            "authoritative" => WorldStateHashScope.Authoritative,
            _ => ((WorldStateHashScope?)null),
        };

        if (scope is null) {
            return CommandResult.Error(output: $"[world.state.hash: unknown scope '{token}' — expected capture|pose|world|authoritative]");
        }

        var tick = (server.NextInputTick - 1UL);
        var hash = WorldCaptureScheduler.ComputeStateHash(
            server: server,
            scope: scope.Value,
            tick: tick
        );

        return new CommandResult(Output: ((args.Count == 0)
            ? string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[world.state.hash: tick={tick} hash={hash:x16}]"
            )
            : string.Create(
                provider: CultureInfo.InvariantCulture,
                handler: $"[world.state.hash: scope={token} tick={tick} hash={hash:x16}]"
        )));
    }
}
