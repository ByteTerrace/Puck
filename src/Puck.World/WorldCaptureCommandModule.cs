using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>captures</c> section's read-back surface: <c>world.captures</c> (the authored schedule).
/// </summary>
internal sealed class WorldCaptureCommandModule(WorldServer server, WorldStateRoot stateRoot, WorldCaptureRoot captureRoot) : ICommandModule {
    private CommandResult Describe(CommandContext context) {
        var captures = server.ReadView(context).Definition.Captures;

        if (captures is not { Rows: { Count: > 0 } rows }) {
            return new CommandResult(Output: "[world.captures: no schedule authored]");
        }

        var directory = captureRoot.Resolve(
            captures: captures,
            documentDirectory: server.ReadView(context).Definition.DocumentDirectory,
            stateRoot: stateRoot
        );
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
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Anyone,
            bindability: CommandBindability.Unbindable,
            name: "world.captures",
            description: "Reads the document's captures section (Immediate, no arguments): the resolved output directory and every station's declared tick schedule and palette-entry count. Absent captures reports an empty schedule, never a refusal.",
            handler: (context, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "world.captures"
            ) is { } refusal)
            ? refusal
            : Describe(context))
        );
    }
}
