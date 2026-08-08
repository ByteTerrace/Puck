using System.Globalization;
using Puck.Commands;
using Puck.Maths;
using Puck.World.Protocol;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The <c>state</c> section's FINE-GRAIN verb surface — the dev reflection of the genre-neutral game-state document
/// protocol (score, rounds, inventory, flags), molded over stdin through the SAME <see cref="WorldMutation"/>
/// messages the editor drives. The whole-row pair (a row's name, kind, envelope, capacity, and cells) lives in the
/// general <see cref="WorldRowCommandModule"/> as <c>world.row.set</c>/<c>world.row.remove state ...</c> — a cell
/// write is a FINER grain than a row upsert, not sugar for one, which is why it stays here:
/// <c>world.state.cell.set</c> writes one cell of an already-declared row without re-authoring its shape,
/// DISPATCHING on the row's own declared kind (a numeric/bool token, or a raw-tail string for an already-live
/// text-kind row); <c>world.state.cell.remove</c> removes one, <c>world.generate</c> redraws a draw site, and
/// <c>world.state</c> reads all three grains back (every row, one row with its cells, one cell alone). The former
/// world.table.* module is retired with the
/// separate table concept it named — a slot IS a row with one cell keyed <see cref="WorldStateRow.SlotKey"/>, so a
/// second verb family over the same rows was a second name for one thing. Every write verb routes
/// <see cref="CommandRouting.Simulation"/> (buffers, applies at the tick boundary, the stdin barrier serializes a
/// following read); <c>world.state</c> is an <see cref="CommandRouting.Immediate"/> read of the live section.
/// </summary>
/// <remarks>Every mutation here carries the identity its ingress door stamped (see
/// <see cref="WorldPrincipalMapping"/>) — Console for a typed line. <see cref="WorldServer"/> checks it TWICE: the
/// standard <see cref="WorldCapability.Mutate"/> hold over <see cref="WorldSection.State"/> every mutation kind
/// requires, PLUS a second, row-scoped <see cref="WorldCapability.Edit"/> hold over the CONCRETE
/// <c>state:&lt;name&gt;</c> subject the row names (or the wildcard) — the "concrete rows" ruling, and the SAME
/// subject whichever grain the write uses. An Edit row may additionally carry a verb mask
/// (<c>world.grant … edit state:&lt;name&gt; verbs:UpsertStateCell,RemoveStateCell</c>), which admits the per-cell
/// writes while denying the whole-row pair — the difference between bumping a row and redefining it. Revoking either
/// grant, or narrowing its mask, refuses that principal's writes here, whichever verb produced them.</remarks>
internal sealed class WorldStateCommandModule(WorldServer server, IServerLink link) : ICommandModule {
    /// <inheritdoc/>
    public IEnumerable<CommandDefinition> GetCommands() {
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state.cell.set",
            description: $"Upserts ONE cell inside an already-declared row, leaving the row's own shape untouched (declare or redeclare the row with world.row.set state <row-json>): world.state.cell.set <row> <key> <value> [add] | <row> <key> <text...>. DISPATCHES ON THE ROW'S OWN DECLARED KIND: when <row> is ALREADY LIVE as a text-kind row, everything after <key> is taken as the RAW TAIL — spaces included, no quoting needed, no 'add' (a string has no addition) — replacing the cell wholesale; otherwise (int/fixed/bool, or a row this SAME batch has not yet declared — the one case this door cannot see live, which falls through to this grammar exactly as it always has) <value> is a single token resolved AT COMPOSE against the row's declared kind (so a same-batch world.row.set state declaring <row> ahead of this line composes first and this line lands against it, deterministically): DECIMAL text for a fixed-kind row (e.g. \"12.5\"), a whole number for int, or true|false for bool — never raw FixedQ4816 bits. The optional trailing 'add' token adds <value> to the key's current value (0 if the key is absent) instead of replacing it — refused on bool, and never admitted on a text write. Reaches any row — pass the reserved key '{WorldStateRow.SlotKey}' to write a one-value row's own cell. Writing '{WorldStateRow.SlotKey}' on a row declaring 'advance' RE-BASES it: the written value becomes the new base and its epoch becomes this tick, exactly like redeclaring the row. Writing a KEYED cell that already carries its OWN advance re-bases that cell the same way, preserving its rate. A trailing 'add' adds to the LIVE accumulated value, never to the stored base. Buffers and applies at the tick boundary; rejected loudly (against the CANDIDATE this batch has built so far, never a stale read) if <row> names no state row (declare it first), if a numeric/bool write targets a text-kind row or vice versa, if 'add' targets a bool-kind row, if <value> does not parse under <row>'s kind, if the written text exceeds WorldStateCapacity.MaxTextValueLength, if <key> carries the reserved '$' prefix and is not a cell this row's shape mints with a value it could have minted (a GENERATOR row's '$cursor' is a non-negative sample count; a '$deck<n>' names a declared context, exists only under a non-withReplacement mode, and deals no bit past its context's alternative count), or — at whole-document revalidation — if the resulting value falls outside the row's declared envelope, a non-negative row's value would go negative, the write would grow the row past its capacity, or the acting principal lacks a Mutate/section:state or Edit/state:<row> hold admitting UpsertStateCell.",
            handler: (context, args) => HandleCellSet(context: context, args: args),
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state.cell.remove",
            description: "Removes ONE cell from an already-declared row, leaving the row itself in place: world.state.cell.remove <row> <key>. Rejected if <row> names no state row, no cell inside it carries <key>, or the acting principal's Edit hold does not admit RemoveStateCell.",
            handler: (context, args) => {
                if (args.Count != 2) {
                    return Usage(verb: "world.state.cell.remove", form: "<row> <key>");
                }

                return Submit(mutation: new WorldMutation.RemoveStateCell(Principal: context.ActingPrincipal(), Row: args[0].ToString(), Key: args[1].ToString()));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.generate",
            description: "Redraws a DRAW SITE — a state row declaring a \"draw\" facet: world.generate <row>. One argument, because a site owns its whole draw: the facet either names a declared source from the document's \"generators\" section (\"source\": <name>) or inlines one (\"generator\": {...}), and the drawn value lands in the site's own slot cell. A markov source walks weighted transitions and writes TEXT — ending at a TERMINAL context (one declaring no alternatives) and REFUSING BY NAME rather than truncating if it reaches the source's declared bound first; under a deck mode (withoutReplacement / reshuffleOnExhaustion) each alternative is dealt at most once per context, and the deck persists across draws in THIS SITE's own bookkeeping (two sites sharing one source deal independently). The uniformRange, weightedNumeric, and streamDraw sources each write ONE numeric value. Refused by name if the site declares timing=boot (drawn once at first fill, never again). The drawn value and the site's advanced cursor land in the SAME candidate, so world.undo rewinds a draw exactly. Buffers and applies at the tick boundary; rejected loudly if <row> names no state row, names one declaring no draw, the site's source resolves to nothing or writes a kind the site cannot hold, the emission exceeds the text bound, or the acting principal lacks a Mutate/section:state or Edit/state:<row> hold admitting Generate.",
            handler: (context, args) => {
                if (args.Count != 1) {
                    return Usage(verb: "world.generate", form: "<row>");
                }

                return Submit(mutation: new WorldMutation.Generate(
                    Principal: context.ActingPrincipal(),
                    Row: args[0].ToString()
                ));
            },
            routing: CommandRouting.Simulation
        );
        yield return CommandDefinition.WithWireArgs(
            bindability: CommandBindability.Unbindable,
            name: "world.state",
            description: "Reads back the live state section at all three grains (Immediate): with no argument, every row's name/kind/shape (a one-value row shows its value, a keyed row its cell count against its capacity), plus each row's declared range and non-negative floor when it carries them; with a row name, that row's line followed by every cell it holds; with a row name and a cell key, that one cell alone — or a refusal naming why there is none: world.state [row] [key].",
            handler: (_, args) => DescribeStateHandler(args: args),
            routing: CommandRouting.Immediate
        );
    }

    // Row existence, row KIND (for the same-batch-declare edge case below), and the bool+add refusal are ALL asked
    // at compose too (WorldServer's UpsertStateCell arm) — a verb-level check can settle the COMMON case (the row
    // is already live) but not the same-batch "world.row.set state declaring <row> a line ago, still draining"
    // race, so compose keeps re-checking regardless of what this dispatch decided.
    //
    // DISPATCH: a row already live as text-kind takes the raw tail as TEXT (the text grammar,
    // folded in here); everything else — int/fixed/bool, OR a row this door cannot yet see live — falls through to
    // the numeric/bool token grammar, exactly as this verb always has. A text row declared and written to in the
    // SAME buffered batch (before this door can see it live) is the one case that still needs two steps.
    private CommandResult HandleCellSet(CommandContext context, WireArgs args) {
        if (args.Count < 2) {
            return Usage(verb: "world.state.cell.set", form: "<row> <key> <value> [add] | <row> <key> <text...>");
        }

        var rowName = args[0].ToString();

        if ((FindRow(name: rowName) is { Kind: CellKind.Text }) && (args.Count >= 3)) {
            var text = WorldCommandArguments.RawAfter(context: context, args: in args, tokens: 3);

            return Submit(mutation: new WorldMutation.UpsertStateCell(Principal: context.ActingPrincipal(), Row: rowName, Key: args[1].ToString(), Value: 0L, Kind: WorldDocumentWriteKind.Set, Text: text));
        }

        if ((args.Count != 3) && (args.Count != 4)) {
            return Usage(verb: "world.state.cell.set", form: "<row> <key> <value> [add] | <row> <key> <text...>");
        }

        var kind = WorldDocumentWriteKind.Set;

        if (args.Count == 4) {
            if (!string.Equals(a: args[3].ToString(), b: "add", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                return CommandResult.Error(output: $"[world.state.cell.set: unknown trailing token '{args[3]}' — expected 'add']");
            }

            kind = WorldDocumentWriteKind.Add;
        }

        return Submit(mutation: new WorldMutation.UpsertStateCell(Principal: context.ActingPrincipal(), Row: rowName, Key: args[1].ToString(), Value: 0L, Kind: kind, RawToken: args[2].ToString()));
    }
    private CommandResult DescribeStateHandler(WireArgs args) {
        return args.Count switch {
            0 => new CommandResult(Output: DescribeState()),
            1 => DescribeOneRow(name: args[0].ToString()),
            2 => DescribeOneCell(rowName: args[0].ToString(), key: args[1].ToString()),
            _ => CommandResult.Error(output: "[world.state: expected no arguments, <row>, or <row> <key>]"),
        };
    }
    private string DescribeState() {
        var rows = server.Definition.State;
        var lines = new List<string>(capacity: (1 + rows.Count)) {
            $"[world.state: rows {rows.Count}/{WorldStateCapacity.MaxRows}]",
        };

        foreach (var row in rows) {
            lines.Add(item: DescribeRow(row: row));
        }

        return string.Join(separator: Environment.NewLine, values: lines);
    }

    // One row's own line PLUS every cell it holds — the grain world.table used to own, now the same verb's
    // one-argument form, because there is one substrate and no shape that hides from it.
    private CommandResult DescribeOneRow(string name) {
        if (FindRow(name: name) is not { } row) {
            return CommandResult.Error(output: $"[world.state {name}: no such row]");
        }

        var cells = (row.Cells ?? []);
        var lines = new List<string>(capacity: (1 + cells.Count)) {
            DescribeRow(row: row),
        };

        // Each cell line re-reads through the shared reader by its own key rather than formatting the stored record,
        // so an advancing row's slot cell reads LIVE here exactly as it does on the row line above — one command's
        // output can never show the same cell two ways.
        foreach (var cell in cells) {
            _ = WorldStateReader.TryRead(definition: server.Definition, rowName: row.Name, key: cell.Key.Value, tick: CompletedTick, row: out _, rawValue: out var raw, text: out var text);
            lines.Add(item: DescribeCell(row: row, key: cell.Key.Value, raw: (raw ?? 0L), text: text, advance: cell.Advance));
        }

        return new CommandResult(Output: string.Join(separator: Environment.NewLine, values: lines));
    }

    // The one-cell grain, resolved through WorldStateReader — the SAME (row, key) read the rule gates and the HUD
    // binding run, so this read-back cannot report a cell the engine would not have read.
    private CommandResult DescribeOneCell(string rowName, string key) {
        if (!WorldStateReader.TryRead(definition: server.Definition, rowName: rowName, key: key, tick: CompletedTick, row: out var row, rawValue: out var rawValue, text: out var text)) {
            return CommandResult.Error(output: $"[world.state {rowName}: no such row]");
        }

        return ((rawValue is { } raw)
            ? new CommandResult(Output: DescribeCell(row: row, key: key, raw: raw, text: text, advance: FindCellAdvance(row: row, key: key)))
            : CommandResult.Error(output: $"[world.state {rowName} {key}: no such cell]"));
    }

    // Looks up an addressed cell's OWN advance trait for the read-back — mirroring Server.WorldServer's identical
    // helper, kept separate since it is a different assembly, over the SAME WorldStateRow shape.
    private static WorldStateAdvance? FindCellAdvance(WorldStateRow row, string key) {
        foreach (var cell in (row.Cells ?? [])) {
            if (string.Equals(a: cell.Key.Value, b: key, comparisonType: StringComparison.Ordinal)) {
                return cell.Advance;
            }
        }

        return null;
    }
    private WorldStateRow? FindRow(string name) => WorldDefinitionRows.FindStateRow(rows: server.Definition.State, name: name);

    // The tick this module's reads answer AS OF: the server's most recently COMPLETED tick, derived the same way
    // WorldInstance.CompletedTicks derives it (NextInputTick is m_lastCompletedTick + 1, and its one writer is Step).
    // This is the tick an advancing row's value is computed at, and it is the completed one rather than the next one
    // because a console read-back must answer for the same instant the simulation last settled on.
    private ulong CompletedTick => (server.NextInputTick - 1UL);

    // A one-value row (WorldStateRow.IsSlot) shows its value inline — resolved through WorldStateReader on the row's
    // own slot key, the SAME read the HUD's state.<row> binding runs, so the console line and the panel cannot show
    // different numbers. Every other shape shows its cell count against its effective capacity, and its cells follow
    // on the one-argument form. ONE line format either way — the shape is a field of the line, never a different
    // verb.
    private string DescribeRow(WorldStateRow row) {
        var head = $"[world.state.row '{row.Name}' kind={DescribeKind(kind: row.Kind)}{DescribeNonNegative(row: row)}{DescribeGatesDrive(row: row)}{DescribeEvicts(row: row)}";
        var tail = $"{DescribeRange(kind: row.Kind, min: row.Min, max: row.Max)}{DescribeAdvance(row: row)}{DescribeDraw(row: row)}]";

        if (!row.IsSlot) {
            var capacity = Math.Clamp(value: (row.Capacity ?? WorldStateCapacity.MaxCellsPerRow), min: 1, max: WorldStateCapacity.MaxCellsPerRow);

            return $"{head} cells={(row.Cells?.Count ?? 0)}/{capacity}{tail}";
        }

        // IsSlot already proved this row carries exactly the cell a null key addresses, and the row came out of the
        // very section the reader looks it up in.
        _ = WorldStateReader.TryRead(definition: server.Definition, rowName: row.Name, key: null, tick: CompletedTick, row: out _, rawValue: out var slot, text: out var slotText);

        return $"{head} value={DescribeValue(row: row, raw: (slot ?? 0L), text: slotText)}{tail}";
    }
    private static string DescribeCell(WorldStateRow row, string key, long raw, string? text, WorldStateAdvance? advance) =>
        $"[world.state.cell '{row.Name}'.'{key}' value={DescribeValue(row: row, raw: raw, text: text)}{DescribeCellAdvance(advance: advance)}]";
    private static string DescribeValue(WorldStateRow row, long raw, string? text) => row.Kind switch {
        CellKind.Fixed => FixedQ4816.FromRawBits(value: raw).ToString(),
        CellKind.Bool => ((raw != 0) ? "true" : "false"),
        CellKind.Text => $"'{text}'",
        _ => raw.ToString(provider: CultureInfo.InvariantCulture),
    };

    // Transparency alongside the live value above: what the trait IS (rate) and where its clock sits (epoch), the
    // same "what it is, then where it is" precedent DescribeRow already follows for a generator's cursor.
    private static string DescribeAdvance(WorldStateRow row) =>
        ((row.Advance is { } advance) ? $" advance={advance.RateNumerator}/{advance.RateDenominator}@epoch{advance.EpochTick}" : string.Empty);

    // The KEYED counterpart of DescribeAdvance above — a cell's OWN advance trait, echoed on the cell line rather
    // than the row line, since it is the cell's own base/rate/epoch that governs it, never the row's.
    private static string DescribeCellAdvance(WorldStateAdvance? advance) =>
        ((advance is { } a) ? $" advance={a.RateNumerator}/{a.RateDenominator}@epoch{a.EpochTick}" : string.Empty);
    private static string DescribeNonNegative(WorldStateRow row) => (row.NonNegative ? " nonNegative=true" : string.Empty);
    private static string DescribeGatesDrive(WorldStateRow row) => (row.GatesDrive ? " gatesDrive=true" : string.Empty);
    private static string DescribeEvicts(WorldStateRow row) => (row.Evicts ? " evicts=true" : string.Empty);

    // A DRAW SITE reads back as WHAT IT IS and WHERE IT IS — which source it draws from (named or inline), when it
    // may draw, and its own live position. That position is the whole of a site's draw state (nothing lives outside
    // the document), so this line is what a save/reload proof reads.
    private static string DescribeDraw(WorldStateRow row) {
        if (row.Draw is not { } draw) {
            return string.Empty;
        }

        var source = ((draw.Source is { } named) ? $"source={named}" : $"source=<inline:{DescribeSourceShape(generator: draw.Generator)}>");

        return $" draw {source} timing={draw.Timing.ToString().ToLowerInvariant()} cursor={row.DrawCursor} decks={DescribeDecks(row: row)}";
    }
    private static string DescribeSourceShape(WorldGenerator? generator) =>
        ((generator is null) ? "?" : (char.ToLowerInvariant(c: generator.Source.ToString()[0]) + generator.Source.ToString()[1..]));

    // The site's per-context dealt masks, by the source's context declaration ordinal.
    private static string DescribeDecks(WorldStateRow row) {
        if (row.DrawDecks is not { Count: > 0 } decks) {
            return "none";
        }

        var parts = new List<string>(capacity: decks.Count);

        for (var index = 0; (index < decks.Count); index++) {
            if (decks[index] != 0L) {
                parts.Add(item: $"{index}=0x{decks[index]:X}");
            }
        }

        return ((parts.Count == 0) ? "none" : string.Join(separator: ",", values: parts));
    }
    private static string DescribeKind(CellKind kind) => kind.ToString().ToLowerInvariant();
    private static string DescribeRange(CellKind kind, long? min, long? max) {
        if ((min is not { } lo) || (max is not { } hi)) {
            return string.Empty;
        }

        return ((kind == CellKind.Fixed)
            ? $" range={FixedQ4816.FromRawBits(value: lo)}..{FixedQ4816.FromRawBits(value: hi)}"
            : $" range={lo}..{hi}");
    }
    private CommandResult Submit(WorldMutation mutation) {
        link.SubmitWorldMutation(mutation: mutation);

        return CommandResult.None;
    }
    private static CommandResult Usage(string verb, string form) {
        return CommandResult.Error(output: $"[{verb}: expected {form}]");
    }
}
