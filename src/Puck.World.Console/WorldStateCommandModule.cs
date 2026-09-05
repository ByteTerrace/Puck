using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>state</c> section's fine-grain verb surface — the dev reflection of the genre-neutral game-state document
/// protocol (score, rounds, inventory, flags), molded over stdin through the same <see cref="WorldMutation"/>
/// messages every document write flows through. The whole-row pair (a row's name, kind, envelope, capacity, and cells) lives in
/// <c>world.row.set</c>/<c>world.row.remove state ...</c> — a cell write is a finer grain than a row upsert, not
/// sugar for one, which is why it stays here: <c>world.state.cell.set</c> writes one cell of an already-declared row
/// without re-authoring its shape, dispatching on the row's own declared kind (a numeric/bool token, or a raw-tail
/// string for an already-live text-kind row); <c>world.state.cell.remove</c> removes one, <c>world.generate</c>
/// redraws a draw site, and <c>world.state</c> reads all three grains back (every row, one row with its cells, one
/// cell alone). A slot is a row with one cell keyed <see cref="WorldStateRow.SlotKey"/>, so there is no separate verb
/// family for it. Every write verb routes <see cref="CommandRouting.Simulation"/> (buffers, applies at the tick
/// boundary, the stdin barrier serializes a following read); <c>world.state</c> is an
/// <see cref="CommandRouting.Immediate"/> read of the live section.
/// </summary>
/// <remarks>Every mutation here carries the identity its ingress door stamped (see
/// <see cref="WorldPrincipalMapping"/>) — Console for a typed line. <see cref="WorldServer"/> checks it twice: the
/// standard <see cref="WorldCapability.Mutate"/> hold over <see cref="WorldSection.State"/> every mutation kind
/// requires, plus a second, row-scoped <see cref="WorldCapability.Edit"/> hold over the concrete
/// <c>state:&lt;name&gt;</c> subject the row names (or the wildcard), the same subject whichever grain the write
/// uses. An Edit row may additionally carry a verb mask
/// (<c>world.grant … edit state:&lt;name&gt; verbs:UpsertStateCell,RemoveStateCell</c>), which admits the per-cell
/// writes while denying the whole-row pair — the difference between bumping a row and redefining it. Revoking either
/// grant, or narrowing its mask, refuses that principal's writes here, whichever verb produced them.</remarks>
public sealed partial class WorldStateCommandModule(IWorldConsoleAuthority authority, IServerLink link, WorldDeferredVerbEchoes echoes) : ICommandModule {
    private static string DescribeCell(WorldServer server, WorldStateRow row, string key, long raw, string? text, WorldStateAdvance? advance, WorldStateDynamics? dynamics, WorldStateCycle? cycle) =>
        $"[world.state.cell '{row.Name}'.'{key}' value={DescribeValue(
            raw: raw,
            row: row,
            text: text
        )}{DescribeCellAdvance(advance: advance)}{DescribeDynamics(
            dynamics: dynamics,
            key: key,
            row: row,
            server: server
        )}{DescribeCycle(cycle: cycle)}]";
    // A cycle trait — what it is (generator, power and output), how fast it turns (ticks per step), where its clock
    // sits (epoch) and the period the generator derives; the value on the same line is the live rotation the stored
    // phase has been carried to.
    private static string DescribeCycle(WorldStateCycle? cycle) =>
        ((cycle is { } c)
            ? $" cycle={DescribeWord(word: c.Word)}^{c.Power}:{c.Output}/{c.TicksPerStep}@epoch{c.EpochTick}{((c.SubstepTicks != 0L) ? $"+{c.SubstepTicks}" : string.Empty)} order={c.Order}"
            : string.Empty
        );
    private static string DescribeWord(IReadOnlyList<int>? word) =>
        ((word is null)
            ? "coxeter"
            : $"[{string.Join(separator: ',', values: word)}]"
        );
    // Formats an Advance trait — shared by DescribeRow's row line (against row.Advance) and DescribeCell's cell
    // line (against a keyed cell's own Advance): what the trait IS (rate) and where its clock sits (epoch), the
    // same "what it is, then where it is" precedent DescribeRow already follows for a generator's cursor.
    private static string DescribeCellAdvance(WorldStateAdvance? advance) =>
        ((advance is { } a)
            ? $" advance={a.RateNumerator}/{a.RateDenominator}@epoch{a.EpochTick}"
            : string.Empty
        );
    // A cell's own second-order easing trait — y0/v0 are the follower's continuous state, raw FixedQ4816 bits on
    // every row kind, so they print in the fixed spelling — plus the LIVE eased value in the row's own encoding, read
    // through the same WorldStateReader.TryReadEased the HUD's state.<row>[.<key>] binding resolves.
    private static string DescribeDynamics(WorldServer server, WorldStateRow row, string key, WorldStateDynamics? dynamics) {
        if (dynamics is not { } d) {
            return string.Empty;
        }

        var eased = string.Empty;

        if (
            WorldStateReader.TryReadEased(
            definition: server.Definition,
            key: key,
            rawValue: out var easedRaw,
            row: out _,
            rowName: row.Name,
            text: out var easedText,
            tick: CompletedTick(server: server)
        ) &&
            (easedRaw is { } raw)
        ) {
            eased = $" eased={DescribeValue(
                raw: raw,
                row: row,
                text: easedText
            )}";
        }

        return $" dynamics={d.Row} y0={FixedQ4816.FromRawBits(value: d.Y0)} v0={FixedQ4816.FromRawBits(value: d.V0)}@epoch{d.EpochTick}{eased}";
    }
    // The site's per-context drawn masks, by the source's context declaration ordinal.
    private static string DescribeMasks(WorldStateRow row) {
        if (row.DrawnMasks is not { Count: > 0 } masks) {
            return "none";
        }

        var parts = new List<string>(capacity: masks.Count);

        for (var index = 0; (index < masks.Count); index++) {
            if (!masks[index].IsEmpty) {
                parts.Add(item: $"{index}=0x{masks[index]}");
            }
        }

        return ((parts.Count == 0)
            ? "none"
            : string.Join(
                separator: ",",
                values: parts
            )
        );
    }
    // A DRAW SITE reads back as WHAT IT IS and WHERE IT IS — which source it draws from (named or inline), when it
    // may draw, and its own live position. That position is the whole of a site's draw state (nothing lives outside
    // the document), so this line is what a save/reload proof reads.
    private static string DescribeDraw(WorldStateRow row) {
        if (row.Draw is not { } draw) {
            // A lattice row's draw fill reads back the same way a site does — its source and its pass position —
            // minus a timing, since a whole-field pass is redrawn only by an explicit generate.
            if (WorldLatticeFill.FindDraw(trait: row.Field) is { } fill) {
                var fillSource = ((fill.Source is { } namedFill)
                    ? $"source={namedFill}"
                    : $"source=<inline:{DescribeSourceShape(generator: fill.Generator)}>"
                );

                return $" draw {fillSource} fill=lattice cursor={row.DrawCursor} masks={DescribeMasks(row: row)}";
            }

            return string.Empty;
        }

        var source = ((draw.Source is { } named)
            ? $"source={named}"
            : $"source=<inline:{DescribeSourceShape(generator: draw.Generator)}>"
        );

        return $" draw {source} timing={draw.Timing.ToString().ToLowerInvariant()} cursor={row.DrawCursor} masks={DescribeMasks(row: row)}";
    }
    private static string DescribeEvicts(WorldStateRow row) => (row.Evicts
        ? " evicts=true"
        : string.Empty
    );
    private static string DescribeGatesDrive(WorldStateRow row) => (row.GatesDrive
        ? " gatesDrive=true"
        : string.Empty
    );
    private static string DescribeKind(CellKind kind) => kind.ToString().ToLowerInvariant();
    private static string DescribeNonNegative(WorldStateRow row) => (row.NonNegative
        ? " nonNegative=true"
        : string.Empty
    );
    // The one-cell grain, resolved through WorldStateReader — the SAME (row, key) read the rule gates and the HUD
    // binding run, so this read-back cannot report a cell the engine would not have read.
    private static CommandResult DescribeOneCell(WorldServer server, string rowName, string key) {
        if (!WorldStateReader.TryRead(
            definition: server.Definition,
            rowName: rowName,
            key: key,
            tick: CompletedTick(server: server),
            row: out var row,
            rawValue: out var rawValue,
            text: out var text
        )) {
            return CommandResult.Error(output: $"[world.state {rowName}: no such row]");
        }

        if (rawValue is not { } raw) {
            return CommandResult.Error(output: $"[world.state {rowName} {key}: no such cell]");
        }

        var cell = WorldDefinitionRows.FindCell(
            cells: row.Cells,
            key: WorldCellName.Parse(candidate: key)
        );

        return new CommandResult(Output: DescribeCell(
            server: server,
            row: row,
            key: key,
            raw: raw,
            text: text,
            advance: cell?.Advance,
            dynamics: cell?.Dynamics,
            cycle: cell?.Cycle
        ));
    }
    // One row's own line PLUS every cell it holds — the verb's one-argument form, because there is one substrate and
    // no shape that hides from it.
    private static CommandResult DescribeOneRow(WorldServer server, string name) {
        if (FindRow(
            name: name,
            server: server
        ) is not { } row) {
            return CommandResult.Error(output: $"[world.state {name}: no such row]");
        }

        var cells = (row.Cells ?? []);
        var lines = new List<string>(capacity: (1 + cells.Count)) {
            DescribeRow(
                row: row,
                server: server
            ),
        };

        // Each cell line re-reads through the shared reader by its own key rather than formatting the stored record,
        // so an advancing row's slot cell reads LIVE here exactly as it does on the row line above — one command's
        // output can never show the same cell two ways.
        foreach (var cell in cells) {
            _ = WorldStateReader.TryRead(
                definition: server.Definition,
                rowName: row.Name,
                key: cell.Key.Value,
                tick: CompletedTick(server: server),
                row: out _,
                rawValue: out var raw,
                text: out var text
            );
            lines.Add(item: DescribeCell(
                server: server,
                row: row,
                key: cell.Key.Value,
                raw: (raw ?? 0L),
                text: text,
                advance: cell.Advance,
                dynamics: cell.Dynamics,
                cycle: cell.Cycle
            ));
        }

        return new CommandResult(Output: string.Join(
            separator: Environment.NewLine,
            values: lines
        ));
    }
    private static string DescribeRange(CellKind kind, long? min, long? max) {
        if (
            (min is not { } lo) ||
            (max is not { } hi)
        ) {
            return string.Empty;
        }

        return ((kind == CellKind.Fixed)
            ? $" range={FixedQ4816.FromRawBits(value: lo)}..{FixedQ4816.FromRawBits(value: hi)}"
            : $" range={lo}..{hi}"
        );
    }
    // A one-value row (WorldStateRow.IsSlot) shows its value inline — resolved through WorldStateReader on the row's
    // own slot key, the SAME read the HUD's state.<row> binding runs, so the console line and the panel cannot show
    // different numbers. Every other shape shows its cell count against its effective capacity, and its cells follow
    // on the one-argument form. ONE line format either way — the shape is a field of the line, never a different
    // verb.
    private static string DescribeRow(WorldServer server, WorldStateRow row) {
        var head = $"[world.state.row '{row.Name}' kind={DescribeKind(kind: row.Kind)}{DescribeNonNegative(row: row)}{DescribeGatesDrive(row: row)}{DescribeEvicts(row: row)}";
        var tail = $"{DescribeRange(
            kind: row.Kind,
            min: row.Min,
            max: row.Max
        )}{DescribeCellAdvance(advance: row.Advance)}{DescribeDynamics(
            dynamics: row.Dynamics,
            key: WorldStateRow.SlotKey.Value,
            row: row,
            server: server
        )}{DescribeCycle(cycle: row.Cycle)}{DescribeDraw(row: row)}{DescribeDiscrete(server, row)}]";

        if (!row.IsSlot) {
            var capacity = Math.Clamp(
                value: (row.Capacity ?? row.CellCeiling),
                min: 1,
                max: row.CellCeiling
            );

            return $"{head} cells={(row.Cells?.Count ?? 0)}/{capacity}{tail}";
        }

        // IsSlot already proved this row carries exactly the cell a null key addresses, and the row came out of the
        // very section the reader looks it up in.
        _ = WorldStateReader.TryRead(
            definition: server.Definition,
            rowName: row.Name,
            key: null,
            tick: CompletedTick(server: server),
            row: out _,
            rawValue: out var slot,
            text: out var slotText
        );

        return $"{head} value={DescribeValue(
            raw: (slot ?? 0L),
            row: row,
            text: slotText
        )}{tail}";
    }
    private static string DescribeSourceShape(WorldGenerator? generator) =>
        ((generator is null)
            ? "?"
            : (char.ToLowerInvariant(c: generator.Source.ToString()[0]) + generator.Source.ToString()[1..])
        );
    private static string DescribeState(WorldServer server) {
        var rows = server.Definition.State;
        var lines = new List<string>(capacity: (1 + rows.Count)) {
            $"[world.state: rows {rows.Count}/{WorldStateCapacity.MaxRows}]",
        };

        foreach (var row in rows) {
            lines.Add(item: DescribeRow(
                row: row,
                server: server
            ));
        }

        return string.Join(
            separator: Environment.NewLine,
            values: lines
        );
    }
    private static CommandResult DescribeStateHandler(WorldServer server, WireArgs args) {
        return args.Count switch {
            0 => new CommandResult(Output: DescribeState(server: server)),
            1 => DescribeOneRow(
            server: server,
            name: args[0].ToString()
        ),
            2 => DescribeOneCell(
            server: server,
            rowName: args[0].ToString(),
            key: args[1].ToString()
        ),
            _ => CommandResult.Error(output: "[world.state: expected no arguments, <row>, or <row> <key>]"),
        };
    }
    private static string DescribeValue(WorldStateRow row, long raw, string? text) => row.Kind switch {
        CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
        CellKind.Bool => ((raw != 0)
        ? "true"
        : "false"),
        CellKind.Text => $"'{text}'",
        _ => raw.ToString(provider: CultureInfo.InvariantCulture),
    };
    private static WorldStateRow? FindRow(WorldServer server, string name) => WorldDefinitionRows.FindStateRow(
        rows: server.Definition.State,
        name: name
    );
    // Row existence, row KIND (for the same-batch-declare edge case below), and the bool+add refusal are ALL asked
    // at compose too (WorldServer's UpsertStateCell arm) — a verb-level check can settle the COMMON case (the row
    // is already live) but not the same-batch "world.row.set state declaring <row> a line ago, still draining"
    // race, so compose keeps re-checking regardless of what this dispatch decided.
    //
    // DISPATCH: a row already live as text-kind takes the raw tail as TEXT (the text grammar,
    // folded in here); everything else — int/fixed/bool, OR a row this door cannot yet see live — falls through to
    // the numeric/bool token grammar, exactly as this verb always has. A text row declared and written to in the
    // SAME buffered batch (before this door can see it live) is the one case that still needs two steps.
    private CommandResult HandleCellSet(WorldServer server, CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return CommandResult.Usage(
                form: "<row> <key> <value> [add] | <row> <key> <text...>",
                verb: "world.state.cell.set"
            );
        }

        var rowName = args[0].ToString();

        if (
            (FindRow(
                name: rowName,
                server: server
            ) is { Kind: CellKind.Text }) &&
            (args.Count >= 3)
        ) {
            var text = WorldCommandArguments.RawAfter(
                args: in args,
                context: context,
                tokens: 3
            );

            return link.Submit(mutation: new WorldMutation.UpsertStateCell(
                Principal: context.ActingPrincipal(),
                Row: rowName,
                Key: args[1].ToString(),
                Value: 0L,
                Kind: WorldDocumentWriteKind.Set,
                Text: text
            ));
        }

        if (
            (args.Count != 3) &&
            (args.Count != 4)
        ) {
            return CommandResult.Usage(
                form: "<row> <key> <value> [add] | <row> <key> <text...>",
                verb: "world.state.cell.set"
            );
        }

        var kind = WorldDocumentWriteKind.Set;

        if (args.Count == 4) {
            if (!string.Equals(
                a: args[3].ToString(),
                b: "add",
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                return CommandResult.Error(output: $"[world.state.cell.set: unknown trailing token '{args[3]}' — expected 'add']");
            }

            kind = WorldDocumentWriteKind.Add;
        }

        return link.Submit(mutation: new WorldMutation.UpsertStateCell(
            Principal: context.ActingPrincipal(),
            Row: rowName,
            Key: args[1].ToString(),
            Value: 0L,
            Kind: kind,
            RawToken: args[2].ToString()
        ));
    }

    private const int DefaultRuleTraceEvaluations = 8;

    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        foreach (var command in DiscreteCommands()) {
            yield return command;
        }
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.rule.failures",
            description: "Prints bounded runtime rule-effect refusal counters (Immediate): category, total occurrences, latest tick/rule/effect, and the latest concrete reason. Level-triggered failures log only their category's first occurrence; this read-back keeps the exact count without stderr spam.",
            handler: (context, args) => {
                if (CommandResult.RequireNoArguments(args: args, verb: "world.rule.failures") is { } refusal) {
                    return refusal;
                }
                if (!authority.TryResolveServer(context: context, error: out var error, server: out var server, verb: "world.rule.failures")) {
                    return error;
                }
                return new CommandResult(Output: server.DescribeRuleRuntimeDiagnostics());
            }
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.rule.trace",
            description: $"Captures one rule's or interaction's next evaluations and reads them back (Immediate): world.rule.trace <rule> [evaluations 1..{WorldServer.MaxRuleTraceEvaluations}] arms a capture (default {DefaultRuleTraceEvaluations}; replaces any earlier one); world.rule.trace alone prints what was captured so far — per evaluation its tick, the forEach key, every binding's value, every gate conjunct with the two values it compared and its verdict, whether the gate held (and whether an edge rule was already held), and each effect's spelling, computed value, and outcome: applied, refused with the reason, emitted, or skipped because the write could not move its destination; world.rule.trace off disarms. Arm, world.wait the ticks the rule should run over, then read. An observer only — a traced run hashes identically to an untraced one. A decision rule is refused here; world.decisions echoes it.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(context: context, error: out var error, server: out var server, verb: "world.rule.trace")) {
                    return error;
                }
                if (args.Count == 0) {
                    return new CommandResult(Output: server.DescribeRuleTrace());
                }
                if ((args.Count == 1) && args[0].Equals(other: "off", comparisonType: StringComparison.Ordinal)) {
                    return new CommandResult(Output: (server.DisarmRuleTrace() ? "[world.rule.trace: disarmed]" : "[world.rule.trace: none armed]"));
                }
                var evaluations = DefaultRuleTraceEvaluations;
                if ((args.Count > 2) || ((args.Count == 2) && !args.TryInt(index: 1, value: out evaluations))) {
                    return CommandResult.Usage(form: $"[<rule> [evaluations 1..{WorldServer.MaxRuleTraceEvaluations}] | off]", verb: "world.rule.trace");
                }
                var rule = args[0].ToString();
                return (server.TryArmRuleTrace(rule: rule, evaluations: evaluations, refusal: out var refusal)
                    ? new CommandResult(Output: $"[world.rule.trace {rule}: armed for {evaluations} evaluation(s) — world.wait, then world.rule.trace reads them back]")
                    : new CommandResult(Output: refusal) { IsError = true });
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.budget.rules",
            description: "Lists every rule's and interaction's worst-case work per tick, costliest first (Immediate): world.budget.rules [top]. Each line carries the evaluation multiplier (a forEach row's capacity, an interaction's carrier or pair count), the cost of one evaluation, the line's total, and — when the gate pins a literal cell to a constant — the cell and value it prices exclusively under, so the total world.budget reports can be traced to the lines that make it up.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(context: context, error: out var error, server: out var server, verb: "world.budget.rules")) {
                    return error;
                }
                var top = int.MaxValue;
                if ((args.Count > 1) || ((args.Count == 1) && (!args.TryInt(index: 0, value: out top) || (top < 1)))) {
                    return CommandResult.Usage(form: "[top]", verb: "world.budget.rules");
                }
                var lines = WorldRuleWorkBudget.Contributors(definition: server.Definition);
                var shown = Math.Min(top, lines.Count);
                var output = new List<string>(capacity: shown + 1) { $"[world.budget.rules: {lines.Count} line(s), showing {shown}]" };
                for (var index = 0; index < shown; index++) {
                    var line = lines[index];
                    output.Add(item: $"[world.budget.rules {line.Name}{(line.IsInteraction ? " interaction" : string.Empty)} x{line.Multiplier} unit={line.UnitCost} work={line.WorkUnits}{((line.ExclusiveCell is { } cell) ? $" exclusive {cell}={line.ExclusiveValue}" : string.Empty)}]");
                }
                return new CommandResult(Output: string.Join(separator: Environment.NewLine, values: output));
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state.cell.set",
            description: $"Upserts ONE cell inside an already-declared row, leaving the row's own shape untouched (declare or redeclare the row with world.row.set state <row-json>): world.state.cell.set <row> <key> <value> [add] | <row> <key> <text...>. DISPATCHES ON THE ROW'S OWN DECLARED KIND: when <row> is ALREADY LIVE as a text-kind row, everything after <key> is taken as the RAW TAIL — spaces included, no quoting needed, no 'add' (a string has no addition) — replacing the cell wholesale; otherwise (int/fixed/bool, or a row this SAME batch has not yet declared — the one case this door cannot see live, which falls through to this grammar exactly as it always has) <value> is a single token resolved AT COMPOSE against the row's declared kind (so a same-batch world.row.set state declaring <row> ahead of this line composes first and this line lands against it, deterministically): DECIMAL text for a fixed-kind row (e.g. \"12.5\"), a whole number for int, or true|false for bool — never raw FixedQ4816 bits. The optional trailing 'add' token adds <value> to the key's current value (0 if the key is absent) instead of replacing it — refused on bool, and never admitted on a text write. Reaches any row — pass the reserved key '{WorldStateRow.SlotKey}' to write a one-value row's own cell. Writing '{WorldStateRow.SlotKey}' on a row declaring 'advance' RE-BASES it: the written value becomes the new base and its epoch becomes this tick, exactly like redeclaring the row. Writing a KEYED cell that already carries its OWN advance re-bases that cell the same way, preserving its rate. A row or cell declaring 'dynamics' instead rebases the SAME way, preserving which dynamics row it names: its Y0/V0 become the live eased value/velocity at this tick (never the raw write) plus a velocity kick signed by that row's own response, and its epoch becomes this tick — the write moves TRUTH, never the follower's own position, which keeps chasing from wherever it actually was. A trailing 'add' adds to the row's LIVE truth — the accumulated value for 'advance', the stored value itself for 'dynamics' (never the eased follower position) — rather than to the stored base. Buffers and applies at the tick boundary; rejected loudly (against the CANDIDATE this batch has built so far, never a stale read) if <row> names no state row (declare it first), if a numeric/bool write targets a text-kind row or vice versa, if 'add' targets a bool-kind row, if <value> does not parse under <row>'s kind, if the written text exceeds WorldStateCapacity.MaxTextValueLength, if <key> carries the reserved '$' prefix and is not '{WorldStateRow.SlotKey}' (draw and generator bookkeeping — a cursor, the drawn masks — lives in the row's own fields, never a cell this door can reach), or — at whole-document revalidation — if the resulting value falls outside the row's declared envelope, a non-negative row's value would go negative, the write would grow the row past its capacity, or the acting principal lacks a Mutate/section:state or Edit/state:<row> hold admitting UpsertStateCell.",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.state.cell.set"
                )) {
                    return error;
                }

                return HandleCellSet(
                    args: args,
                    context: context,
                    server: server
                );
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state.cell.remove",
            description: "Removes ONE cell from an already-declared row, leaving the row itself in place: world.state.cell.remove <row> <key>. Rejected if <row> names no state row, no cell inside it carries <key>, or the acting principal's Edit hold does not admit RemoveStateCell.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return CommandResult.Usage(
                        form: "<row> <key>",
                        verb: "world.state.cell.remove"
                    );
                }

                return link.Submit(mutation: new WorldMutation.RemoveStateCell(
                    Principal: context.ActingPrincipal(),
                    Row: args[0].ToString(),
                    Key: args[1].ToString()
                ));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.symmetry",
            description: "Reads the symmetry lattice back for one node (Immediate): world.symmetry <node> [other]. Prints the node's ring, antipode, canonical ray, projected point and its thirty-step ring walk — the values a '$symmetry:' rule operand or a cycle trait's Node/ProjectionX/ProjectionY output would read — and, with a second node, the reflection through it, whether the two rays are orthogonal, and their exact inner product (-2..2; 1 marks a sixty-degree neighbour).",
            handler: (context, args) => {
                if (
                    ((args.Count != 1) && (args.Count != 2)) ||
                    !args.TryInt(
                    index: 0,
                    value: out var node
                ) ||
                    (node < 0) ||
                    (node >= SymmetryLattice.NodeCount)
                ) {
                    return CommandResult.Usage(
                        form: $"<node 0..{SymmetryLattice.NodeCount - 1}> [other 0..{SymmetryLattice.NodeCount - 1}]",
                        verb: "world.symmetry"
                    );
                }

                var projected = SymmetryLattice.Project(node: node);
                var walk = new System.Text.StringBuilder();
                var cursor = node;

                for (var step = 0; (step < SymmetryLattice.RingSize); step++) {
                    if (step > 0) { walk.Append(value: ','); }

                    walk.Append(value: cursor);
                    cursor = SymmetryLattice.Cycle(node: cursor);
                }

                var line = $"[world.symmetry node={node} ring={SymmetryLattice.Ring(node: node)} antipode={SymmetryLattice.Antipode(node: node)} canonicalRay={SymmetryLattice.CanonicalRay(node: node)} projection=({projected.X},{projected.Y}) walk={walk}]";

                if (args.Count == 2) {
                    if (
                        !args.TryInt(
                        index: 1,
                        value: out var other
                    ) ||
                        (other < 0) ||
                        (other >= SymmetryLattice.NodeCount)
                    ) {
                        return CommandResult.Usage(
                            form: $"<node 0..{SymmetryLattice.NodeCount - 1}> [other 0..{SymmetryLattice.NodeCount - 1}]",
                            verb: "world.symmetry"
                        );
                    }

                    line += $"{Environment.NewLine}[world.symmetry node={node} other={other} reflect={SymmetryLattice.Reflect(mirror: other, node: node)} orthogonal={(SymmetryLattice.AreOrthogonal(first: node, second: other) ? 1 : 0)} innerProduct={SymmetryLattice.InnerProduct(first: node, second: other)}]";
                }

                return new CommandResult(Output: line);
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.symmetry.word",
            description: "Reads a word of reflections back (Immediate): world.symmetry.word <mirror>... [node:<n>]. Bakes the word a cycle trait would author as its generator (one to eight mirror nodes, applied first to last) and prints its derived order — the period a cycle carrying it loops in — and, given a node, that node's orbit under the word in step order, so an author can see the dial a word makes before authoring it.",
            handler: (context, args) => {
                var usage = $"<mirror 0..{SymmetryLattice.NodeCount - 1}>... [node:<0..{SymmetryLattice.NodeCount - 1}>]";
                var mirrors = new List<int>(capacity: SymmetryWord.MaximumLength);
                var seed = -1;

                for (var index = 0; (index < args.Count); index++) {
                    var token = args[index].ToString();

                    if (token.StartsWith(value: "node:", comparisonType: StringComparison.Ordinal)) {
                        if ((index != (args.Count - 1)) || !int.TryParse(s: token["node:".Length..], style: System.Globalization.NumberStyles.None, provider: System.Globalization.CultureInfo.InvariantCulture, result: out seed) || (seed >= SymmetryLattice.NodeCount)) {
                            return CommandResult.Usage(form: usage, verb: "world.symmetry.word");
                        }

                        continue;
                    }

                    if (!args.TryInt(index: index, value: out var mirror) || (mirror < 0) || (mirror >= SymmetryLattice.NodeCount) || (mirrors.Count == SymmetryWord.MaximumLength)) {
                        return CommandResult.Usage(form: usage, verb: "world.symmetry.word");
                    }

                    mirrors.Add(item: mirror);
                }

                if (mirrors.Count == 0) {
                    return CommandResult.Usage(form: usage, verb: "world.symmetry.word");
                }

                var word = SymmetryWord.Create(mirrors: System.Runtime.InteropServices.CollectionsMarshal.AsSpan(list: mirrors));
                var line = $"[world.symmetry.word mirrors={DescribeWord(word: mirrors)} order={word.Order} identity={(word.IsIdentity ? 1 : 0)}]";

                if (seed >= 0) {
                    var orbit = new System.Text.StringBuilder();
                    var cursor = seed;

                    for (var step = 0; (step < word.OrbitLength(node: seed)); step++) {
                        if (step > 0) { orbit.Append(value: ','); }

                        orbit.Append(value: cursor);
                        cursor = word.Apply(node: cursor);
                    }

                    line += $"{Environment.NewLine}[world.symmetry.word node={seed} orbitLength={word.OrbitLength(node: seed)} orbit={orbit}]";
                }

                return new CommandResult(Output: line);
            },
            routing: CommandRouting.Immediate
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.generate",
            description: "Redraws a DRAW SITE — a state row declaring a \"draw\" facet: world.generate <row> [key ...]. The site owns its whole draw: the facet either names a declared source from the document's \"generators\" section (\"source\": <name>) or inlines one (\"generator\": {...}), and the drawn value lands in the site's own slot cell — or, on a KEYED site (a dice tray), one numeric sample per cell in cell order; naming keys redraws those cells alone and holds the rest. A markov source walks weighted transitions and writes TEXT — ending at a TERMINAL context (one declaring no alternatives) and REFUSING BY NAME rather than truncating if it reaches the source's declared bound first; under an exhausting mode (withoutReplacement / restartOnExhaustion) each alternative is drawn at most once per context, and the drawn mask persists across draws in THIS SITE's own bookkeeping (two sites sharing one source draw independently). The uniformRange, weightedNumeric, and streamDraw sources each write ONE numeric value. Refused by name if the site declares timing=boot (drawn once at first fill, never again). The drawn value and the site's advanced cursor land in the SAME candidate, so world.undo rewinds a draw exactly. Buffers and applies at the tick boundary; rejected loudly if <row> names no state row, names one declaring no draw, the site's source resolves to nothing or writes a kind the site cannot hold, the emission exceeds the text bound, or the acting principal lacks a Mutate/section:state or Edit/state:<row> hold admitting Generate.",
            handler: (context, args) => {
                if (args.Count < 1) {
                    return CommandResult.Usage(
                        form: "<row> [key ...]",
                        verb: "world.generate"
                    );
                }

                string[]? keys = null;

                if (args.Count > 1) {
                    keys = new string[args.Count - 1];

                    for (var index = 1; index < args.Count; index++) {
                        keys[index - 1] = args[index].ToString();
                    }
                }

                return link.Submit(mutation: new WorldMutation.Generate(
                    Principal: context.ActingPrincipal(),
                    Row: args[0].ToString(),
                    Keys: keys
                ));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state",
            description: "Reads back the live state section at all three grains (Immediate): with no argument, every row's name/kind/shape (a one-value row shows its value, a keyed row its cell count against its capacity), plus each row's declared range and non-negative floor when it carries them; with a row name, that row's line followed by every cell it holds; with a row name and a cell key, that one cell alone — or a refusal naming why there is none: world.state [row] [key].",
            handler: (context, args) => {
                if (!authority.TryResolveServer(
                    context: context,
                    error: out var error,
                    server: out var server,
                    verb: "world.state"
                )) {
                    return error;
                }

                return DescribeStateHandler(
                    args: args,
                    server: server
                );
            },
            routing: CommandRouting.Immediate
        );
    }

    // The tick this module's reads answer AS OF: the server's most recently COMPLETED tick, derived the same way
    // WorldInstance.CompletedTicks derives it (NextInputTick is m_lastCompletedTick + 1, and its one writer is Step).
    // This is the tick an advancing row's value is computed at, and it is the completed one rather than the next one
    // because a console read-back must answer for the same instant the simulation last settled on.
    private static ulong CompletedTick(WorldServer server) => (server.NextInputTick - 1UL);
}
