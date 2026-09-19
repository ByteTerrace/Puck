using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The read-back surface for the two halves of a test world: <c>world.schedule</c> (the authored command schedule,
/// its export tick, and what each row's submission answered) and <c>world.verdicts</c> (every verdict row, its
/// gate, its status, and the values its gate saw).
/// </summary>
internal sealed class WorldScheduleCommandModule(WorldServer server) : ICommandModule {
    private CommandResult Describe() {
        if (server.Definition.Schedule is not { } schedule) {
            return new CommandResult(Output: "[world.schedule: no schedule authored]");
        }

        var rows = schedule.Rows;
        var lines = new List<string>(capacity: (1 + rows.Count)) {
            (WorldScheduleRoot.IsArmed
                ? $"[world.schedule: armed directory {WorldScheduleRoot.Resolve(authored: schedule.Directory)} rows {rows.Count} settle {schedule.SettleTicks} export tick {schedule.ExportTick}]"
                : $"[world.schedule: unarmed — this boot passed no --schedule-dir, so no row is submitted and no export is written; rows {rows.Count} settle {schedule.SettleTicks} export tick {schedule.ExportTick}]"),
        };

        foreach (var row in rows) {
            lines.Add(item: $"  tick={row.Tick} {row.Principal}: {row.Command}");
        }

        return new CommandResult(Output: string.Join(
            separator: Environment.NewLine,
            values: lines
        ));
    }
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
        _ = WorldStateReader.TryReadValue(
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
            kind: row.Kind,
            raw: ((value.HasValue && (value.Kind is CellKind.Bool or CellKind.Fixed or CellKind.Int))
                ? value.Raw
                : 0L
            )
        );
    }
    private long ReadCell(WorldStateRow row, CellName key) {
        _ = WorldStateReader.TryReadValue(
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
            bindability: CommandBindability.Unbindable,
            name: "world.schedule",
            description: "Reads the document's schedule section (Immediate, no arguments): whether this boot armed it (--schedule-dir) or left it inert, the resolved output directory, the settle margin, the derived export tick, and every scheduled row's tick, principal and command line. Absent schedule reports an empty schedule, never a refusal.",
            handler: (_, args) => ((CommandResult.RequireNoArguments(
                args: args,
                verb: "world.schedule"
            ) is { } refusal)
            ? refusal
            : Describe())
        );
        yield return CommandDefinition.WithWireArgs(
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
