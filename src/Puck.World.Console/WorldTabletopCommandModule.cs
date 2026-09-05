using System.Globalization;
using System.Text;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The tabletop primitive's read-back — <c>world.tabletop</c> echoes every placement carrying a
/// <see cref="WorldPlacementBoard"/> facet: its anchored frame, its <c>occupancy</c> row's live cells, and any
/// author-named <c>turn</c>/<c>verdict</c>/<c>move</c>/<c>plan</c> convenience rows the game bound alongside it.
/// </summary>
/// <remarks>Read-only. A board's rows are authored/mutated through the same ordinary state doors (<c>world.row.set
/// state</c>, rule effects) every other row uses — this module only names which rows belong to which tabletop.</remarks>
public sealed class WorldTabletopCommandModule(IWorldConsoleAuthority authority) : ICommandModule {
    private static string DescribeFixed(FixedQ4816 value) => ((double)value).ToString(format: "0.#####", provider: CultureInfo.InvariantCulture);
    private static void AppendCells(StringBuilder text, WorldStateRow? row, string label) {
        _ = text.Append(value: ' ').Append(value: label).Append(value: '=');
        if (row is null) {
            _ = text.Append(value: "(unbound)");
            return;
        }
        var cells = row.Cells;
        if (cells is null || cells.Count == 0) {
            _ = text.Append(value: "(empty)");
            return;
        }
        _ = text.Append(value: '{');
        for (var index = 0; index < cells.Count; index++) {
            var cell = cells[index];
            if (index > 0) {
                _ = text.Append(value: ',');
            }
            _ = text.Append(value: cell.Key.Value).Append(value: ':').Append(value: (row.Kind == CellKind.Text ? (cell.Text ?? "") : cell.Value.ToString(CultureInfo.InvariantCulture)));
        }
        _ = text.Append(value: '}');
    }
    private static string Describe(WorldServer server, string? filter) {
        var definition = server.Definition;
        var echo = CommandEcho.Open(verb: "world.tabletop");
        var matched = 0;

        foreach (var placement in definition.Placements) {
            if (placement.Board is not { } board) {
                continue;
            }

            if (
                (filter is { } only) &&
                !string.Equals(a: placement.Id, b: only, comparisonType: StringComparison.Ordinal)
            ) {
                continue;
            }

            matched++;

            var topology = WorldTopologyCompilation.Find(definition, board.Topology);
            var occupancy = WorldDefinitionRows.FindStateRow(definition.State, board.Occupancy);

            var text = new StringBuilder(value: "tabletop '").Append(value: placement.Id).Append(value: '\'')
                .Append(value: " topology=").Append(value: board.Topology);

            if (topology is null) {
                _ = text.Append(value: " frame=(unresolved)");
            } else {
                _ = text.Append(value: " origin=(").Append(value: DescribeFixed(topology.Origin.X)).Append(value: ',')
                    .Append(value: DescribeFixed(topology.Origin.Y)).Append(value: ',')
                    .Append(value: DescribeFixed(topology.Origin.Z)).Append(value: ')')
                    .Append(value: " cellSize=").Append(value: DescribeFixed(topology.CellSize))
                    .Append(value: " width=").Append(value: topology.Width)
                    .Append(value: " depth=").Append(value: topology.Depth);
            }

            AppendCells(text: text, row: occupancy, label: "occupancy");

            if (board.Turn is { } turnName) {
                AppendCells(text: text, row: WorldDefinitionRows.FindStateRow(definition.State, turnName), label: "turn");
            }

            if (board.Verdict is { } verdictName) {
                AppendCells(text: text, row: WorldDefinitionRows.FindStateRow(definition.State, verdictName), label: "verdict");
            }

            if (board.Move is { } moveName) {
                AppendCells(text: text, row: WorldDefinitionRows.FindStateRow(definition.State, moveName), label: "move");
            }

            if (board.Plan is { } planName) {
                AppendCells(text: text, row: WorldDefinitionRows.FindStateRow(definition.State, planName), label: "plan");
            }

            _ = echo.Text(text: text.ToString()).Segment();
        }

        if (matched == 0) {
            _ = echo.Text(text: ((filter is { } missing)
                ? $"no tabletop '{missing}'"
                : "(no tabletops)"
            ));
        }

        _ = echo.Field(key: "tick", value: (server.NextInputTick - 1UL));

        return echo.Close();
    }

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.tabletop",
            description: "Echoes every placement carrying a board facet — its anchored frame (origin, cellSize, width, depth), its occupancy row's live cells, and any bound turn/verdict/move/plan rows: world.tabletop [placementId]. With a placement id, echoes only that tabletop.",
            handler: (context, args) => {
                if (args.Count > 1) {
                    return CommandResult.Error(output: "[world.tabletop: expected [placementId]]");
                }

                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.tabletop"
                )) {
                    return error;
                }

                return new CommandResult(Output: Describe(
                    filter: ((args.Count == 1)
                    ? args[0].ToString()
                    : null
                ),
                    server: server
                ));
            }
        );
    }
}
