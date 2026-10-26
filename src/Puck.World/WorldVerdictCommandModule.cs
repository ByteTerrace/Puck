using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The read-back surface for a test world's verdict rows: their gates, statuses and witnesses.
/// </summary>
internal sealed class WorldVerdictCommandModule(WorldServer server) : ICommandModule {
    private CommandResult Verdicts() {
        var definition = server.Definition;
        var rows = definition.State;
        var lines = new List<string>();

        for (var index = 0; (index < rows.Count); index++) {
            var row = rows[index];

            if ((row is null) || (row.Verdict is not { } verdict)) {
                continue;
            }

            var status = ReadCell(
                key: verdict.Status,
                row: row
            );
            var saw = new List<string>();

            foreach (var holder in rows) {
                if ((holder is null) || ((holder != row) && (holder.Witness != row.Name))) {
                    continue;
                }

                foreach (var cell in (holder.Cells ?? [])) {
                    if (
                        (cell is null) ||
                        ((holder == row) && (cell.Key == verdict.Status))
                    ) {
                        continue;
                    }

                    saw.Add(item: DescribeSeen(
                        key: cell.Key,
                        row: holder
                    ));
                }
            }

            lines.Add(item: $"  {row.Name}: {WorldVerdict.Describe(status: status)} gate=\"{verdict.Gate}\" saw=[{string.Join(
                separator: " ",
                values: saw
            )}]");
        }

        if (lines.Count == 0) {
            return new CommandResult(Output: "[world.verdicts: no verdict rows declared]");
        }

        lines.Insert(
            index: 0,
            item: $"[world.verdicts: {lines.Count} row(s) at tick {(server.NextInputTick - 1UL)}]"
        );

        return new CommandResult(Output: string.Join(
            separator: Environment.NewLine,
            values: lines
        ));
    }
    private string DescribeSeen(WorldStateRow row, CellName key) {
        _ = WorldStateReader.TryReadValue(arena: server.Arena,
            definition: server.Definition,
            engineTick: server.CompletedEngineTicks,
            key: key.Value,
            row: out _,
            rowName: row.Name.Value,
            tick: (server.NextInputTick - 1UL),
            value: out var value
        );

        return WorldVerdict.DescribeSeen(
            key: key.Value,
            value: value.HasValue ? value : CellValue.Int(0)
        );
    }
    private long ReadCell(WorldStateRow row, CellName key) {
        _ = WorldStateReader.TryReadValue(arena: server.Arena,
            definition: server.Definition,
            engineTick: server.CompletedEngineTicks,
            key: key.Value,
            row: out _,
            rowName: row.Name.Value,
            tick: (server.NextInputTick - 1UL),
            value: out var value
        );

        return ((value.HasValue && (value.Kind == CellKind.Int))
            ? value.AsInt
            : WorldVerdict.NotEvaluated
        );
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            audience: CommandAudience.Operator,
            bindability: CommandBindability.Unbindable,
            name: "world.verdicts",
            description: "Reads every verdict row (Immediate, no arguments): the row's name, its status (pass, fail, or never evaluated), the gate it claims, and the values its gate saw. A document declaring no verdict row reports none, never a refusal.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "world.verdicts"
            ) is { } refusal)
            ? refusal
            : Verdicts())
        );
    }
}
