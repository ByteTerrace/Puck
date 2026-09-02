using System.Globalization;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>captures</c> section's read-back surface: <c>world.captures</c> (the authored schedule) and
/// <c>world.state.hash</c> (the deterministic pose+state-cell summary <see cref="WorldCaptureScheduler"/> stamps
/// into a manifest entry, exposed live for a script to assert against without waiting on a scheduled tick).
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
            description: "Reads the live deterministic state hash (Immediate, no arguments): the same FNV-1a summary WorldCaptureScheduler stamps into a manifest entry — WorldReplaySnapshot.HashState's active-body pose fold, then every state.world row's declared cells (document order) chained onto it at the current tick.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(args: args, verb: "world.state.hash") is { } refusal)
            ? refusal
            : Hash())
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
    private CommandResult Hash() {
        var tick = (server.NextInputTick - 1UL);
        var hash = WorldCaptureScheduler.ComputeStateHash(
            server: server,
            tick: tick
        );

        return new CommandResult(Output: string.Create(
            provider: CultureInfo.InvariantCulture,
            handler: $"[world.state.hash: tick={tick} hash={hash:x16}]"
        ));
    }
}
