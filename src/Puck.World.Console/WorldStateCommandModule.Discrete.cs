using System.Globalization;
using System.Text.Json;
using Puck.Commands;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

public sealed partial class WorldStateCommandModule {
    private IEnumerable<CommandDefinition> DiscreteCommands() {
        yield return CommandDefinition.Verb(
            name: "world.state.observe",
            bindability: CommandBindability.Unbindable,
            description: "Reads the literal state observations admitted for the authenticated caller. Hidden cells and identities are omitted.",
            valueKind: CommandValueKind.Digital,
            routing: CommandRouting.Immediate,
            handler: context => {
                var result = default(CommandResult);

                void Complete(QueryAnswer answer) => result = new(Output: answer.Text) { IsError = answer.Refused };
                if (link is IPrincipalServerLink stamped) {
                    stamped.Query(
                        new WorldQuery.StateObservations(),
                        context.Principal,
                        Complete
                    );
                } else {
                    link.Query(
                        new WorldQuery.StateObservations(),
                        Complete
                    );
                }
                return result;
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.state.transform",
            bindability: CommandBindability.Unbindable,
            description: "Applies one atomic state transform: world.state.transform <transform-json>. Checks edit authority over every touched row.",
            routing: CommandRouting.Simulation,
            handler: (context, args) => SubmitTransform(
                context,
                args,
                guarded: false
            )
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.state.act",
            bindability: CommandBindability.Unbindable,
            description: "Submits a phase-guarded state operation: world.state.act <phase-row> <sequence> <transform-json>. Refuses stale, ineligible, ready or expired actions.",
            routing: CommandRouting.Simulation,
            handler: (context, args) => SubmitTransform(
                context,
                args,
                guarded: true
            )
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.topologies",
            bindability: CommandBindability.Unbindable,
            description: "Prints named physical and discrete topology declarations and compiled addressing costs.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (!authority.TryResolveServerWithoutArguments(
                    args: in args,
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.topologies"
                )) {
                    return error;
                }
                var lines = new List<string>();

                foreach (var topology in (server.Definition.StateRaw?.Lattices ?? [])) {
                    var compiled = WorldTopologyCompilation.Find(
                        definition: server.Definition,
                        name: topology.Name
                    );
                    var directionCount = (compiled?.DirectionCount ?? 0);
                    var names = ((compiled is null)
                        ? "none"
                        : string.Join(
                            separator: ",",
                            values: Enumerable.Range(
                                count: directionCount,
                                start: 0
                            ).Select(selector: compiled.DirectionName)
                        )
                    );
                    var normalized = TopologyCompilation.Normalize(topology: topology);
                    var origin = ((compiled is null)
                        ? "none"
                        : $"({((double)compiled.Origin.X):0.####},{((double)compiled.Origin.Y):0.####},{((double)compiled.Origin.Z):0.####})"
                    );

                    lines.Add(item: $"[world.topology '{topology.Name}' kind={topology.Kind} cells={(compiled?.CellCount ?? ((topology.Kind == TopologyKind.Hex)
                        ? (1 + ((3 * normalized.Radius) * (normalized.Radius + 1)))
                        : ((normalized.Width * normalized.Depth) * normalized.Layers)))} directions={directionCount} names={names} wrap={normalized.Wrap} origin={origin}]");
                }
                return new CommandResult(Output: string.Join(
                    separator: Environment.NewLine,
                    values: lines
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.patterns",
            bindability: CommandBindability.Unbindable,
            description: "Echoes every compiled pattern language: kind, refined letters, machine states against the row's budget, and the attribute row a zone source reads.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (!authority.TryResolveServerWithoutArguments(
                    args: in args,
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.patterns"
                )) {
                    return error;
                }
                return new CommandResult(Output: server.DescribePatterns());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.tables",
            bindability: CommandBindability.Unbindable,
            description: "Echoes every static lookup table the document references: name, value kind, and entry count.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (!authority.TryResolveServerWithoutArguments(
                    args: in args,
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.tables"
                )) {
                    return error;
                }
                return new CommandResult(Output: server.DescribeTables());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.topology",
            bindability: CommandBindability.Unbindable,
            description: "Lists a discrete topology's point-group elements, and with a cell key, that cell's image under each: world.topology <topology> [<cell>].",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count is < 1 or > 2) {
                    return CommandResult.Usage(
                        form: "<topology> [<cell>]",
                        verb: "world.topology"
                    );
                }
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.topology"
                )) {
                    return error;
                }
                return new CommandResult(Output: server.DescribeSymmetry(
                    topologyName: args[0].ToString(),
                    cellKey: ((args.Count == 2)
                    ? args[1].ToString()
                    : null)
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.observe",
            bindability: CommandBindability.Unbindable,
            description: "Composes the literal state observations an EXPLICITLY NAMED principal would see — the read-back side of a hidden-hand table, for inspecting another seat's disclosure without submitting as it: world.observe <principal>. Same token grammar as world.grant (PrincipalTokens.TryParse). Unlike world.state.observe (which reads the CALLER's own stamped identity), this composes for the named principal directly through WorldStateDisclosure.Compose, the same trusted-authority read world.why/world.grants already use — a console/authority tool, not a wire capability check.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count != 1) {
                    return CommandResult.Usage(
                        form: "<principal>",
                        verb: "world.observe"
                    );
                }
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.observe"
                )) {
                    return error;
                }
                if (!PrincipalTokens.TryParse(
                    principal: out var principal,
                    token: args[0].ToString()
                )) {
                    return CommandResult.Error(output: $"[world.observe: unknown principal '{args[0]}' — {PrincipalTokens.Grammar}]");
                }
                // Only the operator composes another principal's view; anyone else observes itself alone.
                var acting = context.Principal;

                if (
                    (acting.Kind != PrincipalKind.Console) &&
                    (acting != principal)
                ) {
                    return CommandResult.Error(output: $"[world.observe: refused — {acting.Describe()} may observe only itself, not {principal.Describe()}]");
                }
                var time = server.Time;
                var rows = (WorldStateDisclosure.Compose(
                    arena: server.Arena,
                    definition: server.Definition,
                    recipient: principal,
                    time: in time
                ) ?? []).ToArray();

                return new CommandResult(Output: System.Text.Json.JsonSerializer.Serialize(
                    rows,
                    WorldJsonContext.Default.WorldObservedRowArray
                ));
            }
        );
        yield return CommandDefinition.WithWireArgs(
            name: "world.match",
            bindability: CommandBindability.Unbindable,
            description: "Walks one word through a pattern and narrates it: world.match <pattern> <row> [<attribute>] for a keyed row or zone, world.match <pattern> <board-row> <origin-cell> <direction|any> for a board.",
            routing: CommandRouting.Immediate,
            handler: (context, args) => {
                if (args.Count is < 2 or > 4) {
                    return CommandResult.Usage(
                        form: "<pattern> <row> [<attribute>] | <pattern> <board-row> <origin-cell> <direction|any>",
                        verb: "world.match"
                    );
                }
                if (!authority.TryResolveReadView(
                    context: context,
                    error: out var viewError,
                    verb: "world.match",
                    view: out var view
                )) {
                    return viewError;
                }
                var pattern = args[0].ToString();
                var row = args[1].ToString();

                // The walk reads the live store, which the view cannot narrow, so it runs only over rows the reader's
                // disclosure carries whole: the word's row and, for a keyed walk, the attribute row it reads per token.
                foreach (var read in ((args.Count == 3)
                    ? new[] { row, args[2].ToString() }
                    : new[] { row })) {
                    if (view.Withheld(row: read) is not null) {
                        return CommandResult.Error(output: view.Refusal(
                            key: null,
                            row: read,
                            verb: "world.match"
                        ));
                    }
                }
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.match"
                )) {
                    return error;
                }

                return new CommandResult(Output: ((args.Count == 4)
                    ? server.DescribeMatch(
                        patternName: pattern,
                        rowName: row,
                        attribute: null,
                        key: args[2].ToString(),
                        direction: args[3].ToString()
                    )
                    : server.DescribeMatch(
                        patternName: pattern,
                        rowName: row,
                        attribute: ((args.Count == 3)
                        ? args[2].ToString()
                        : null),
                        key: null,
                        direction: null
                    )));
            }
        );
    }
    private CommandResult SubmitTransform(CommandContext context, WireArgs args, bool guarded) {
        var verb = (guarded
            ? "world.state.act"
            : "world.state.transform"
        );
        var prefix = (guarded
            ? 3
            : 1
        );

        if (args.Count < prefix) {
            return CommandResult.Usage(
                form: (guarded
                ? "<phase-row> <sequence> <transform-json>"
                : "<transform-json>"),
                verb: verb
            );
        }
        PhaseGuard? guard = null;

        if (guarded) {
            if (!long.TryParse(
                args[1].ToString(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var sequence
            )) {
                return CommandResult.Error(output: "phase sequence must be a nonnegative integer");
            }
            guard = new(
                args[0].ToString(),
                sequence
            );
        }
        try {
            var json = WorldCommandArguments.RawAfter(
                args: in args,
                context: context,
                tokens: prefix
            );
            var operation = JsonSerializer.Deserialize(
                json: json,
                jsonTypeInfo: WorldJsonContext.Default.StateTransform
            );

            if (operation is null) {
                return CommandResult.Error(output: "state transform must be an object");
            }
            return link.Submit(
                new WorldMutation.TransformState(
                    context.Principal,
                    operation,
                    guard
                ),
                echoes,
                verb
            );
        } catch (JsonException exception) {
            return CommandResult.Error(output: $"invalid state transform: {exception.Message}");
        }
    }
    private static string DescribeDiscrete(WorldStateRow row) {
        if (row.Phase is { } phase) {
            return $" phase sequence={phase.Sequence}";
        }
        var domain = row.EffectiveDomain switch {
            StateDomain.Slot => "domain=slot",
            StateDomain.Keys => "domain=keys",
            StateDomain.KeysOf keysOf => $"domain=keysOf row={keysOf.Row} ordered={keysOf.Ordered}",
            StateDomain.CellsOf cellsOf => $"domain=cellsOf topology={cellsOf.Topology} empty={cellsOf.Empty}",
            StateDomain.Ring ring => $"domain=ring capacity={ring.Capacity} empty={ring.Empty} cursor={row.HistoryCursor} held={Math.Min(
            val1: row.HistoryCursor,
            val2: ring.Capacity
        )}",
            var other => throw new InvalidOperationException(message: $"unknown state domain '{other.GetType().Name}'"),
        };

        return ((row.ValuesFrom is { } valuesFrom)
            ? $" {domain} valuesFrom={valuesFrom}"
            : $" {domain}"
        );
    }
}
