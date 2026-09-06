using System.Globalization;
using Puck.Maths;

namespace Puck.State;

public static partial class RuleCompiler {
    private static readonly string[] s_coreSpellings = [RuleFacts.Tick, $"{RuleFacts.ReducePrefix}<op>:<row>"];

    /// <summary>Resolves any read operand — a compareState's primary (State, Key) pair, its comparand, a
    /// setState/addState's live copy source, an expression's state token — through the same reserved-channel/state-row
    /// walk, so no two of them can drift into different readings of the same name. The registered families are
    /// consulted first, then the library's own channels, then the declared rows.</summary>
    /// <param name="name">The authored operand name.</param>
    /// <param name="key">The authored cell key, or <see langword="null"/>.</param>
    /// <param name="site">Where the operand is spelled.</param>
    /// <param name="context">The compile context.</param>
    public static ResolvedOperand ResolveOperand(string name, string? key, in OperandSite site, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: name);
        ArgumentNullException.ThrowIfNull(argument: context);

        foreach (var family in context.Vocabulary.Operands) {
            if (family.TryCompile(name: name, key: key, site: in site, context: context, fact: out var fact)) {
                return new ResolvedOperand(operand: fact!, describe: Describe(name: name, key: key));
            }
        }

        var ruleName = site.RuleName;

        if (name.StartsWith("$phase:", StringComparison.Ordinal)) {
            return ResolvePhaseOperand(name, key, ruleName, context);
        }
        if (name.StartsWith("$board:", StringComparison.Ordinal)) {
            return ResolveBoardOperand(name, key, ruleName, context);
        }
        if (name.StartsWith(RuleFacts.MatchPrefix, StringComparison.Ordinal)) {
            return ResolvePatternOperand(name, key, ruleName, context);
        }
        if (name.StartsWith(RuleFacts.HistoryPrefix, StringComparison.Ordinal)) {
            return ResolveHistoryOperand(name, key, ruleName, context);
        }
        if (name.StartsWith(RuleFacts.BindPrefix, StringComparison.Ordinal)) {
            return ResolveBindingOperand(name, key, ruleName, site.KeyFieldLabel, context);
        }
        if (name.StartsWith(RuleFacts.TablePrefix, StringComparison.Ordinal)) {
            return ResolveTableOperand(name, key, ruleName, context, site.KeyFieldLabel);
        }

        var describe = Describe(name: name, key: key);

        if (string.Equals(a: name, b: RuleFacts.Tick, comparisonType: StringComparison.Ordinal)) {
            RefuseKeyOnReservedChannel(key: key, keyFieldLabel: site.KeyFieldLabel, name: name, ruleName: ruleName);

            return new ResolvedOperand(operand: TickOperand.Instance, describe: describe);
        }

        if (name.StartsWith(value: RuleFacts.ReducePrefix, comparisonType: StringComparison.Ordinal)) {
            return ResolveReduceOperand(name: name, key: key, ruleName: ruleName, keyFieldLabel: site.KeyFieldLabel, context: context, describe: describe);
        }

        if (name.StartsWith(value: RuleFacts.SymmetryPrefix, comparisonType: StringComparison.Ordinal)) {
            return ResolveSymmetryOperand(name: name, key: key, site: in site, context: context, describe: describe);
        }

        if (name.StartsWith(value: StateRow.ReservedNamePrefix, comparisonType: StringComparison.Ordinal)) {
            throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"'{name}' carries the reserved '{StateRow.ReservedNamePrefix}' prefix but names none of the reserved channels ({ReservedChannels(context: context)})"
            );
        }

        // A declared row name is dot-free by construction (CellName refuses a dot) — this only ever fires for an
        // author reaching for a "row.key" spelling in one string. Named explicitly rather than falling through to a
        // generic "unknown row", which would leave the actual mistake (use the separate key field) unsaid.
        if (name.Contains(value: '.')) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"'{site.FieldLabel}' value '{name}' carries a '.' — a state row name is never dotted; address the cell with '{site.KeyFieldLabel}' instead of dotting it into '{site.FieldLabel}'"
            );
        }

        var row = (context.FindRow(name: name)
            ?? throw new RuleException(
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName,
                detail: $"'{name}' names no state row, and is not a reserved channel ({ReservedChannels(context: context)})"
            ));

        if ((row.Kind == CellKind.Text) && !site.AllowText) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName,
                detail: $"state row '{name}' is kind=text — a rule compares numbers, never text"
            );
        }

        if (TryResolveDynamicKey(cell: out var dynamicKey, context: context, key: key, keyFieldLabel: site.KeyFieldLabel, ruleName: ruleName, verb: site.Verb)) {
            if (!row.IsKeyed) {
                throw new RuleException(
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName,
                    detail: $"'{site.Verb}' {site.KeyFieldLabel} '{key}' addresses a cell by indirection, but row '{name}' is not keyed"
                );
            }

            return new ResolvedOperand(
                operand: new StateCellOperand(row: name, key: null, keyFrom: dynamicKey, stateHandle: ResolveHandle(context: context, name: name), valueKind: row.Kind),
                describe: describe
            );
        }

        var resolvedKey = ResolveKey(key: key, keyFieldLabel: site.KeyFieldLabel, row: row, ruleName: ruleName, verb: site.Verb);

        // A read operand must address a cell the row declares today: an undeclared cell reads 0 forever with no
        // refusal anywhere, so it refuses at compile instead. Write destinations mint their cells and are not
        // funneled through here. A draw site's slot cell is declared by its facet, even before it holds one: the
        // boot resolver fills every first-fill site at load, and validation runs on the document before that.
        if (!row.HasCell(key: resolvedKey) && !(row.IsDraw && (resolvedKey == StateRow.SlotKey.Value))) {
            throw new RuleException(
                refusal: RuleRefusal.StateCellUndeclared,
                ruleName: ruleName,
                detail: $"'{site.Verb}' {site.FieldLabel} '{name}' reads cell '{resolvedKey}', which the row does not declare — an undeclared cell reads 0 forever; declare the cell first (an authored 0 is fine)"
            );
        }

        return new ResolvedOperand(
            operand: new StateCellOperand(row: name, key: resolvedKey, keyFrom: null, stateHandle: ResolveHandle(context: context, name: name), valueKind: row.Kind),
            describe: describe
        );
    }

    private static string Describe(string name, string? key) => $"{name}{((key is { } spelledKey) ? $".{spelledKey}" : string.Empty)}";

    private static string ReservedChannels(RuleCompileContext context) {
        var spellings = new List<string>(s_coreSpellings);

        foreach (var family in context.Vocabulary.Operands) {
            spellings.AddRange(collection: family.Spellings);
        }

        return string.Join(separator: ", ", values: spellings.Select(selector: static spelling => $"'{spelling}'"));
    }

    private static ResolvedOperand ResolveReduceOperand(string name, string? key, string ruleName, string keyFieldLabel, RuleCompileContext context, string describe) {
        RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);

        var suffix = name[RuleFacts.ReducePrefix.Length..];
        var separator = suffix.IndexOf(value: ':', comparisonType: StringComparison.Ordinal);

        if ((separator < 0) || !TryParseReduceOp(text: suffix[..separator], op: out var op) || string.IsNullOrEmpty(value: suffix[(separator + 1)..])) {
            throw new RuleException(
                refusal: RuleRefusal.ReduceChannelMalformed,
                ruleName: ruleName,
                detail: $"'{name}' does not spell '{RuleFacts.ReducePrefix}<max|min|sum|count|arrangementRank>:<row>'"
            );
        }

        var parts = suffix[(separator + 1)..].Split(':');
        var rowName = parts[0];
        string? filterRowName = null;
        (decimal Lower, decimal Upper)? bounds = null;
        for (var index = 1; index < parts.Length;) {
            if (parts[index] == "where" && filterRowName is null && index + 1 < parts.Length && parts[index + 1].Length > 0) {
                filterRowName = parts[index + 1];
                index += 2;
            } else if (parts[index] == "between" && bounds is null && index + 2 < parts.Length &&
                decimal.TryParse(parts[index + 1], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var lower) &&
                decimal.TryParse(parts[index + 2], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var upper) && lower <= upper) {
                bounds = (lower, upper);
                index += 3;
            } else {
                throw new RuleException(refusal: RuleRefusal.ReduceChannelMalformed, ruleName: ruleName, detail: $"'{name}' takes optional ':where:<filterRow>' and ':between:<lower>:<upper>' filters once each, with lower <= upper");
            }
        }
        var reduceRow = ResolveNumericRow(channel: name, context: context, malformed: RuleRefusal.ReduceChannelMalformed, name: rowName, requireKeyed: false, ruleName: ruleName);
        if (op == StateReduceOp.ArrangementRank && (reduceRow.EffectiveDomain is not StateDomain.KeysOf { Ordered: true } || filterRowName is not null || bounds is not null)) {
            throw new RuleException(refusal: RuleRefusal.ReduceChannelMalformed, ruleName: ruleName, detail: $"'{name}' ranks an ordered zone's arrangement and takes no filters");
        }
        StateHandle filterHandle = default;
        if (filterRowName is not null) {
            _ = ResolveNumericRow(channel: name, context: context, malformed: RuleRefusal.ReduceChannelMalformed, name: filterRowName, requireKeyed: true, ruleName: ruleName);
            if (!reduceRow.IsKeyed) {
                throw new RuleException(refusal: RuleRefusal.ReduceChannelMalformed, ruleName: ruleName, detail: $"'{name}' applies a keyed filter to non-keyed row '{rowName}'");
            }
            filterHandle = ResolveHandle(context: context, name: filterRowName);
        }
        var reduceValueKind = ((op is StateReduceOp.Count or StateReduceOp.ArrangementRank) ? CellKind.Int : reduceRow.Kind);

        return new ResolvedOperand(
            operand: new ReductionOperand(row: rowName, stateHandle: ResolveHandle(context: context, name: rowName), reduce: op, filterRow: filterRowName, filterHandle: filterHandle, valueKind: reduceValueKind,
                range: bounds is { } range ? (LiteralToRaw(reduceRow.Kind, range.Lower, ruleName, "reduce"), LiteralToRaw(reduceRow.Kind, range.Upper, ruleName, "reduce")) : null),
            describe: describe
        );
    }

    /// <summary>Parses a <c>$reduce:</c> op token.</summary>
    /// <param name="text">The token.</param>
    /// <param name="op">The op, on success.</param>
    public static bool TryParseReduceOp(string text, out StateReduceOp op) {
        op = text switch {
            "max" => StateReduceOp.Max,
            "min" => StateReduceOp.Min,
            "sum" => StateReduceOp.Sum,
            "count" => StateReduceOp.Count,
            "arrangementRank" => StateReduceOp.ArrangementRank,
            _ => StateReduceOp.None,
        };

        return (op != StateReduceOp.None);
    }

    // $symmetry:<function>[:<argument>]:<row> — the row is the last token (a row name is colon-free by construction),
    // the function the first, and whatever sits between is the argument the function takes. The source cell resolves
    // through the ordinary row/key walk, so every key rule holds for it unchanged; a cell argument resolves the same way.
    private static ResolvedOperand ResolveSymmetryOperand(string name, string? key, in OperandSite site, RuleCompileContext context, string describe) {
        var ruleName = site.RuleName;
        var tokens = name[RuleFacts.SymmetryPrefix.Length..].Split(separator: ':');

        static RuleException Malformed(string ruleName, string name, string detail) => new(
            refusal: RuleRefusal.SymmetryChannelMalformed,
            ruleName: ruleName,
            detail: $"'{name}' {detail} — a symmetry channel spells '{RuleFacts.SymmetryPrefix}<ring|antipode|canonicalRay|cycle:<steps>|reflect:<node|cell:<row>[.<key>]>|orthogonal:<node|cell:<row>[.<key>]>|innerProduct:<node|cell:<row>[.<key>]>|projectionX|projectionY>:<row>'"
        );

        if (tokens.Length < 2) {
            throw Malformed(ruleName: ruleName, name: name, detail: "names no source row");
        }

        var rowName = tokens[^1];
        var function = tokens[0] switch {
            "ring" => SymmetryFunction.Ring,
            "antipode" => SymmetryFunction.Antipode,
            "canonicalRay" => SymmetryFunction.CanonicalRay,
            "cycle" => SymmetryFunction.Cycle,
            "reflect" => SymmetryFunction.Reflect,
            "orthogonal" => SymmetryFunction.Orthogonal,
            "innerProduct" => SymmetryFunction.InnerProduct,
            "projectionX" => SymmetryFunction.ProjectionX,
            "projectionY" => SymmetryFunction.ProjectionY,
            _ => throw Malformed(ruleName: ruleName, name: name, detail: $"names no symmetry function '{tokens[0]}'"),
        };
        var argument = string.Join(separator: ':', values: tokens[1..^1]);
        var takesArgument = (function is SymmetryFunction.Cycle or SymmetryFunction.Reflect or SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct);

        if (takesArgument == (argument.Length == 0)) {
            throw Malformed(ruleName: ruleName, name: name, detail: (takesArgument ? $"'{tokens[0]}' needs an argument" : $"'{tokens[0]}' takes no argument"));
        }

        var source = ResolveOperand(name: rowName, key: key, site: site with { AllowText = false }, context: context);

        if (source.Operand is not StateCellOperand sourceCell) {
            throw Malformed(ruleName: ruleName, name: name, detail: $"names '{rowName}', which is not a state row — the source of a symmetry read is a declared row's cell");
        }

        var literal = 0L;
        CompiledCellRef? other = null;

        if (takesArgument) {
            if (function == SymmetryFunction.Cycle) {
                if (!long.TryParse(s: argument, style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture, result: out literal)) {
                    throw Malformed(ruleName: ruleName, name: name, detail: $"'cycle' needs a whole number of ring steps, not '{argument}'");
                }
            } else if (argument.StartsWith(value: "cell:", comparisonType: StringComparison.Ordinal)) {
                var reference = argument["cell:".Length..];
                var dot = reference.IndexOf(value: '.');
                var otherRow = ((dot < 0) ? reference : reference[..dot]);
                var otherKey = ((dot < 0) ? null : reference[(dot + 1)..]);
                var resolved = ResolveOperand(name: otherRow, key: otherKey, site: site with { AllowText = false }, context: context);

                if ((resolved.Operand is not StateCellOperand otherCell) || (otherCell.KeyFrom is not null)) {
                    throw Malformed(ruleName: ruleName, name: name, detail: $"argument '{argument}' does not name a declared row's cell by a literal key");
                }

                other = new CompiledCellRef(Row: otherCell.Row, Key: (otherCell.Key ?? string.Empty), Handle: otherCell.StateHandle);
            } else if (!long.TryParse(s: argument, style: NumberStyles.None, provider: CultureInfo.InvariantCulture, result: out literal) || (literal >= SymmetryLattice.NodeCount)) {
                throw Malformed(ruleName: ruleName, name: name, detail: $"argument '{argument}' is neither a node 0..{SymmetryLattice.NodeCount - 1} nor 'cell:<row>[.<key>]'");
            }
        }

        var symmetryValueKind = ((function is SymmetryFunction.ProjectionX or SymmetryFunction.ProjectionY) ? CellKind.Fixed : CellKind.Int);

        return new ResolvedOperand(
            operand: SymmetryOperand.FromStateCell(source: sourceCell, symmetry: function, symmetryArgument: literal, symmetryOtherCell: other, valueKind: symmetryValueKind),
            describe: describe
        );
    }

    // $table:<name>:<key> for a single-value table, $table:<name>:<column>:<key> for a column table — the table is
    // resolved and compiled here so a literal key is proven present and the value kind is the table's own; a
    // dynamic key ($cell:<row>:<key>, $each, or $bind:<name>) is read at evaluation.
    private static ResolvedOperand ResolveTableOperand(string name, string? key, string ruleName, RuleCompileContext context, string keyFieldLabel) {
        RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);
        var rest = name[RuleFacts.TablePrefix.Length..];
        var firstColon = rest.IndexOf(value: ':');
        if (firstColon <= 0 || firstColon == rest.Length - 1) {
            throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' does not spell '{RuleFacts.TablePrefix}<table>:<key>' or '{RuleFacts.TablePrefix}<table>:<column>:<key>'");
        }
        string[] tokens = [rest[..firstColon], rest[(firstColon + 1)..]];
        if (!context.TryTable(name: tokens[0], ordinal: out var ordinal, table: out var table, error: out var error)) {
            if (ordinal < 0) {
                throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'{name}' names table '{tokens[0]}', which the document's tables do not declare");
            }
            throw new RuleException(refusal: RuleRefusal.StateRowUnknown, ruleName: ruleName, detail: $"'{name}': table '{tokens[0]}' cannot load — {error}");
        }
        var spelledKey = tokens[1];
        var column = 0;
        if (table!.ColumnNames.Count > 0) {
            var columnColon = spelledKey.IndexOf(value: ':');
            var columnName = (columnColon < 0) ? spelledKey : spelledKey[..columnColon];
            column = table.Column(name: columnName);
            if (column < 0 || columnColon < 0 || columnColon == spelledKey.Length - 1) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}': table '{tokens[0]}' has columns [{string.Join(separator: ", ", values: table.ColumnNames)}] and is read as '{RuleFacts.TablePrefix}{tokens[0]}:<column>:<key>'");
            }
            spelledKey = spelledKey[(columnColon + 1)..];
        }
        CompiledCellRef? keyFrom = null;
        var keyBinding = -1;
        var literal = 0L;
        if (spelledKey.StartsWith(RuleFacts.BindPrefix, StringComparison.Ordinal)) {
            var bound = ResolveBindingOperand(name: spelledKey, key: null, ruleName: ruleName, keyFieldLabel: keyFieldLabel, context: context);
            if (bound.ValueKind != CellKind.Int) {
                throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' key '{spelledKey}' is a fixed binding — a table key is an int binding");
            }
            keyBinding = ((BindingOperand)bound.Operand).Ordinal;
        } else if (TryResolveDynamicKey(key: spelledKey, ruleName: ruleName, context: context, verb: name, keyFieldLabel: "key", cell: out var dynamic)) {
            keyFrom = dynamic;
        } else if (!long.TryParse(s: spelledKey, style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture, result: out literal)) {
            throw new RuleException(refusal: RuleRefusal.StateCellUnaddressable, ruleName: ruleName, detail: $"'{name}' key '{spelledKey}' is not an integer, a '{RuleFacts.CellKeyPrefix}<row>:<key>' indirection, a '{RuleFacts.BindPrefix}<name>' binding, or a bound key token");
        } else if (!table.TryLookup(key: literal, column: column, raw: out _)) {
            throw new RuleException(refusal: RuleRefusal.StateCellUndeclared, ruleName: ruleName, detail: $"'{name}' names key {literal}, which table '{tokens[0]}' does not carry");
        }
        return new ResolvedOperand(
            operand: new TableOperand(tableOrdinal: ordinal, table: tokens[0], key: literal, keyFrom: keyFrom, keyBinding: keyBinding, column: column, entryCount: table.Count, valueKind: table.Kind),
            describe: name
        );
    }

    private static ResolvedOperand ResolveBindingOperand(string name, string? key, string ruleName, string keyFieldLabel, RuleCompileContext context) {
        RefuseKeyOnReservedChannel(key: key, keyFieldLabel: keyFieldLabel, name: name, ruleName: ruleName);
        var bound = name[RuleFacts.BindPrefix.Length..];
        var scope = (context.RuleBindings ?? []);
        for (var ordinal = 0; ordinal < scope.Count; ordinal++) {
            if (string.Equals(a: scope[ordinal].Name, b: bound, comparisonType: StringComparison.Ordinal)) {
                return new ResolvedOperand(operand: new BindingOperand(ordinal: ordinal, name: bound, valueKind: scope[ordinal].Kind), describe: name);
            }
        }
        throw new RuleException(
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName,
            detail: $"'{name}' names no binding declared before it — a rule's 'bindings' list is read in declared order by later bindings, the gate, and the effects"
        );
    }

    // $phase:<row> — the row's own generation, the same value a PhaseGuard checks against it.
    private static ResolvedOperand ResolvePhaseOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        var tokens = name.Split(':');
        if (key is not null || tokens.Length != 2 || context.FindRow(name: tokens[1])?.Phase is null) {
            throw new RuleException(RuleRefusal.StateCellUnaddressable, ruleName, "phase query requires $phase:<row>, without key");
        }
        return new ResolvedOperand(operand: new PhaseOperand(tokens[1], ResolveHandle(context: context, name: tokens[1])), describe: name);
    }

    // $history:<row>:<age> — the value pushed age pushes ago; the age is bounded by the ring at compile time.
    private static ResolvedOperand ResolveHistoryOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(RuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length != 3 || key is not null) {
            throw Invalid("history read requires $history:<row>:<age> and no key");
        }
        var row = context.FindRow(name: tokens[1]) ?? throw Invalid($"'{tokens[1]}' names no state row");
        if (row.EffectiveDomain is not StateDomain.Ring history) {
            throw Invalid($"'{tokens[1]}' is not a history row");
        }
        if (!int.TryParse(tokens[2], NumberStyles.None, CultureInfo.InvariantCulture, out var age) || age >= history.Capacity) {
            throw Invalid($"age must be 0..{history.Capacity - 1} on '{tokens[1]}'");
        }
        return new ResolvedOperand(
            operand: new HistoryOperand(row: tokens[1], stateHandle: ResolveHandle(context: context, name: tokens[1]), age: age, valueKind: row.Kind),
            describe: name
        );
    }

    private static PatternRow? FindPattern(RuleCompileContext context, string name) {
        foreach (var candidate in context.Patterns) {
            if ((candidate is not null) && (candidate.Name.Value == name)) {
                return candidate;
            }
        }

        return null;
    }

    // $match:<pattern>:<row>[:<direction>|:any][:<facet>] — a board source walks the ray from the operand key's
    // origin cell (exclusive) in the named direction, or every direction under `any`; an ordered zone reads the
    // pattern's attribute row in pile order; a keyed row reads its own cells in cell order. The word's kind must be
    // the pattern's kind. On one direction the facet is `prefix`, `cell`, or `distance`; over `any` it is
    // `mask`/`count`. Absent, the operand answers acceptance.
    private static ResolvedOperand ResolvePatternOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(RuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length is < 3 or > 5) {
            throw Invalid("pattern match requires $match:<pattern>:<row>[:<direction>|:any][:prefix|:cell|:distance|:mask|:count]");
        }
        var facet = MatchFacet.Accept;
        var pattern = FindPattern(context: context, name: tokens[1]) ?? throw Invalid($"'{tokens[1]}' names no pattern");
        var row = context.FindRow(name: tokens[2]) ?? throw Invalid($"'{tokens[2]}' names no state row");
        BoardNeighbourQuery? board = null;
        CompiledCellRef? keyFrom = null;
        string? attribute = null;
        CellKind kind;
        if (row.EffectiveDomain is StateDomain.CellsOf declaredBoard) {
            if (tokens.Length < 4) {
                throw Invalid("a board source requires a direction or any");
            }
            var topology = context.FindTopology(name: declaredBoard.Topology) ?? throw Invalid($"'{tokens[2]}' names no compiled topology");
            var every = tokens[3] == "any";
            var direction = every ? -1 : topology.Direction(tokens[3]);
            if (direction < 0 && !every) {
                throw Invalid($"'{tokens[3]}' is not a direction of '{declaredBoard.Topology}'");
            }
            if (tokens.Length == 5) {
                facet = tokens[4] switch {
                    "prefix" when !every => MatchFacet.Prefix,
                    "cell" when !every => MatchFacet.Cell,
                    "distance" when !every => MatchFacet.Distance,
                    "mask" when every => MatchFacet.DirectionMask,
                    "count" when every => MatchFacet.DirectionCount,
                    _ => throw Invalid($"'{tokens[4]}' is not a facet for this source: prefix, cell or distance on one direction, mask or count over any"),
                };
            }
            if (TryResolveDynamicKey(context: context, key: key, ruleName: ruleName, verb: "match", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("a board source's key must name the origin cell or use a validated dynamic key");
            }
            board = new BoardNeighbourQuery(topology, direction);
            kind = CellKind.Int;
        } else {
            if (tokens.Length > 4) {
                throw Invalid("a zone or keyed source takes no direction");
            }
            if (tokens.Length == 4) {
                facet = (tokens[3] == "prefix") ? MatchFacet.Prefix : throw Invalid($"'{tokens[3]}' is not a facet for a word source; prefix is");
            }
            // A zone or keyed word may start at a token: the key names it (a literal token key, or a live indirection),
            // and the word is that token and every token after it in row order. A history ring reads whole.
            if (key is not null && row.EffectiveDomain is StateDomain.Ring) {
                throw Invalid("a history source takes no key");
            }
            if (TryResolveDynamicKey(context: context, key: key, ruleName: ruleName, verb: "match", keyFieldLabel: "key", cell: out var startKey)) {
                keyFrom = startKey;
            } else if (key is not null && !CellName.TryParse(candidate: key, name: out _, reason: out _)) {
                throw Invalid("a word source's key must name the token the word starts at, or use a validated dynamic key");
            }
            if (row.EffectiveDomain is StateDomain.KeysOf { Ordered: true } zone) {
                if (pattern.Value is not null) {
                    if (!TryCompilePatternValue(context: context, pattern: pattern, tokenDomain: zone.Row.Value, ruleName: ruleName, tokens: out var tokenExpression, reason: out var valueReason)) {
                        throw Invalid(valueReason);
                    }
                    return new ResolvedOperand(
                        operand: new PatternOperand(row: tokens[2], key: key, keyFrom: keyFrom, stateHandle: ResolveHandle(context: context, name: tokens[2]), pattern: tokens[1], board: null, filterRow: null, filterHandle: default, matchFacet: facet, tokenExpression: tokenExpression),
                        describe: name
                    );
                }
                attribute = pattern.Attribute ?? throw Invalid($"pattern '{pattern.Name}' reads a zone and so needs an attribute row or a value expression");
                var attributeRow = context.FindRow(name: attribute) ?? throw Invalid($"attribute '{attribute}' names no state row");
                if (attributeRow.Kind is not (CellKind.Int or CellKind.Fixed) || attributeRow.EffectiveDomain is not StateDomain.KeysOf attributeKeysOf || attributeKeysOf.Row.Value != zone.Row.Value) {
                    throw Invalid($"attribute '{attribute}' must be a numeric row keyed over token domain '{zone.Row}'");
                }
                kind = attributeRow.Kind;
            } else {
                if ((!row.IsKeyed && row.EffectiveDomain is not StateDomain.Ring) || pattern.Attribute is not null) {
                    throw Invalid($"'{row.Name}' must be a keyed or history row read without an attribute");
                }
                kind = row.Kind == CellKind.Bool ? CellKind.Int : row.Kind;
            }
        }
        if (kind != pattern.Kind) {
            throw Invalid($"pattern '{pattern.Name}' reads kind={pattern.Kind} but the source word is kind={kind}");
        }
        return new ResolvedOperand(
            operand: new PatternOperand(
                row: tokens[2],
                key: key,
                keyFrom: keyFrom,
                stateHandle: ResolveHandle(context: context, name: tokens[2]),
                pattern: tokens[1],
                board: board,
                filterRow: attribute,
                filterHandle: (attribute is null) ? default : ResolveHandle(context: context, name: attribute),
                matchFacet: facet,
                tokenExpression: null
            ),
            describe: name
        );
    }

    /// <summary>Compiles a pattern row's value expression for one zone: inside it, a state token keyed
    /// <c>$token</c> reads the current token's cell of a row keyed over <paramref name="tokenDomain"/>.</summary>
    /// <param name="context">The compile context.</param>
    /// <param name="pattern">The pattern row carrying the expression.</param>
    /// <param name="tokenDomain">The zone's token domain.</param>
    /// <param name="ruleName">The rule or console verb the compile answers for.</param>
    /// <param name="tokens">The compiled postfix program, on success.</param>
    /// <param name="reason">The refusal, on failure.</param>
    /// <returns><see langword="true"/> when the expression compiles in the pattern's kind.</returns>
    public static bool TryCompilePatternValue(RuleCompileContext context, PatternRow pattern, string tokenDomain, string ruleName, out CompiledExpressionToken[]? tokens, out string reason) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: pattern);
        var scope = context.BindingScope;
        context.BindingScope = [BoundKey.Token, BoundKey.Previous];
        try {
            tokens = CompileExpression(expression: pattern.Value, kind: pattern.Kind, ruleName: ruleName, verb: $"pattern '{pattern.Name}' value", context: context);
            foreach (var token in tokens) {
                if (token.Operand is IStateAddressedOperand { KeyFrom: { Binding: BoundKey.Token or BoundKey.Previous } } operand &&
                    (context.FindRow(name: operand.Row) is not { } row || row.EffectiveDomain is not StateDomain.KeysOf tokenKeysOf || tokenKeysOf.Row.Value != tokenDomain)) {
                    tokens = null;
                    reason = $"pattern '{pattern.Name}' value reads '{operand.Row}' by $token or $previous, which must be a row keyed over token domain '{tokenDomain}'";
                    return false;
                }
            }
            reason = string.Empty;
            return true;
        } catch (RuleException exception) {
            tokens = null;
            reason = exception.Message;
            return false;
        } finally {
            context.BindingScope = scope;
        }
    }

    private static ResolvedOperand ResolveBoardOperand(string name, string? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(RuleRefusal.StateCellUnaddressable, ruleName, detail);
        var tokens = name.Split(':');
        if (tokens.Length < 3 || (tokens.Length < 4 && tokens[1] != "canonical")) {
            throw Invalid("board query requires $board:<operation>:<row>:<arguments>");
        }
        var row = context.FindRow(name: tokens[2]);
        if (row?.EffectiveDomain is not StateDomain.CellsOf board || context.FindTopology(name: board.Topology) is not { } topology) {
            throw Invalid($"'{tokens[2]}' names no discrete board row");
        }
        var kind = tokens[1] switch {
            "neighbour" => BoardQueryKind.Neighbour,
            "pathCost" => BoardQueryKind.PathCost,
            "mask" => BoardQueryKind.Mask,
            "canonical" => BoardQueryKind.Canonical,
            "offset" => BoardQueryKind.Offset,
            "attacks" => BoardQueryKind.Attacks,
            "component" => BoardQueryKind.Component,
            "boundary" => BoardQueryKind.Boundary,
            "boundaryAt" => BoardQueryKind.BoundaryAt,
            "enclosedAt" => BoardQueryKind.EnclosedAt,
            _ => throw Invalid($"unknown board operation '{tokens[1]}'"),
        };
        if (kind is BoardQueryKind.Mask && topology.CellCount > BoardMask.MaxCells) {
            throw Invalid($"{tokens[1]} reads at most {BoardMask.MaxCells} cells as bits; '{board.Topology}' has {topology.CellCount} — a wider board's set algebra is the boardCombine transform");
        }
        if (kind is BoardQueryKind.Offset && topology.Kind is not (TopologyKind.Grid or TopologyKind.Hex)) {
            throw Invalid($"'{tokens[1]}' requires a Grid or Hex topology, not {topology.Kind}");
        }
        CompiledCellRef? keyFrom = null;
        if (kind is not (BoardQueryKind.Mask or BoardQueryKind.Canonical)) {
            if (TryResolveDynamicKey(context: context, key: key, ruleName: ruleName, verb: "board", keyFieldLabel: "key", cell: out var dynamicKey)) {
                keyFrom = dynamicKey;
            } else if (key is null || !topology.TryCell(key, out _)) {
                throw Invalid("board query key must name a source cell or use a validated dynamic key");
            }
        } else if (key is not null) {
            throw Invalid($"{tokens[1]} does not accept key");
        }
        BoardQuery query;
        if (kind == BoardQueryKind.Canonical) {
            if (tokens.Length != 3) {
                throw Invalid("canonical takes no arguments beyond the row");
            }
            query = new BoardCanonicalQuery(topology);
        } else if (kind == BoardQueryKind.Mask) {
            if (tokens.Length != 5 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var lower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var upper) || lower > upper) {
                throw Invalid("mask requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            query = new BoardMaskQuery(topology, lower, upper);
        } else if (kind == BoardQueryKind.PathCost) {
            if (row.Kind != CellKind.Int) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            // A dynamic target — 'cell:<row>:<key>' — reads its live integer value as the destination ordinal every
            // evaluation, instead of the literal ordinal a plain target takes at compile time alone.
            var dynamicTarget = ((tokens.Length == 8) && string.Equals(tokens[3], "cell", StringComparison.Ordinal));
            var target = 0;
            CompiledCellRef? targetFrom = null;
            var argumentsStart = 4;

            if (dynamicTarget) {
                targetFrom = ResolveCellRef(channel: name, context: context, key: tokens[5], row: tokens[4], ruleName: ruleName);
                argumentsStart = 6;
            } else if (tokens.Length != 6 || !topology.TryCell(tokens[3], out target)) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }

            if (!long.TryParse(tokens[argumentsStart], NumberStyles.None, CultureInfo.InvariantCulture, out var cost) || cost < 0 ||
                !int.TryParse(tokens[argumentsStart + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var visits) || visits < 1 || visits > topology.CellCount) {
                throw Invalid("pathCost requires <targetCell>:<maxCost>:<maxVisits> or cell:<row>:<key>:<maxCost>:<maxVisits> on an integer terrain row");
            }
            query = new BoardPathCostQuery(topology, target, cost, visits, targetFrom);
        } else if (kind is BoardQueryKind.Component or BoardQueryKind.Boundary or BoardQueryKind.BoundaryAt or BoardQueryKind.EnclosedAt) {
            var boundary = (kind != BoardQueryKind.Component);
            var arity = (boundary ? 8 : 6);
            var boundaryLower = 0L;
            var boundaryUpper = 0L;
            if (tokens.Length != arity || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var memberLower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var memberUpper) || memberLower > memberUpper ||
                (boundary && (!long.TryParse(tokens[5], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out boundaryLower) ||
                    !long.TryParse(tokens[6], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out boundaryUpper) || boundaryLower > boundaryUpper)) ||
                !int.TryParse(tokens[arity - 1], NumberStyles.None, CultureInfo.InvariantCulture, out var componentVisits) || componentVisits < 1 || componentVisits > topology.CellCount) {
                throw Invalid(boundary
                    ? $"{tokens[1]} requires <min>:<max>:<boundaryMin>:<boundaryMax>:<maxVisits> on an integer or boolean board row"
                    : "component requires <min>:<max>:<maxVisits> on an integer or boolean board row");
            }
            query = new BoardComponentQuery(topology, memberLower, memberUpper, componentVisits, boundaryLower, boundaryUpper, kind);
        } else if (kind == BoardQueryKind.Attacks) {
            if (tokens.Length != 6 || row.Kind is not (CellKind.Int or CellKind.Bool) ||
                !long.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var attackLower) ||
                !long.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var attackUpper) || attackLower > attackUpper) {
                throw Invalid("attacks requires <min>:<max> integer bounds, min <= max, on an integer or boolean board row");
            }
            var directionNames = tokens[5].Split(',');
            if (directionNames.Length is < 1 or > 4) {
                throw Invalid("attacks requires 1..4 comma-separated directions");
            }
            var directions = new int[directionNames.Length];
            for (var index = 0; index < directionNames.Length; index++) {
                var directionOrdinal = topology.Direction(directionNames[index]);
                if (directionOrdinal < 0) {
                    throw Invalid($"'{directionNames[index]}' is not a direction valid for its topology");
                }
                directions[index] = directionOrdinal;
            }
            query = new BoardAttacksQuery(topology, attackLower, attackUpper, directions);
        } else if (kind == BoardQueryKind.Offset) {
            if (tokens.Length != 5 || !int.TryParse(tokens[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dx) ||
                !int.TryParse(tokens[4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var dz)) {
                throw Invalid("offset requires <dx>:<dz>, both signed integers");
            }
            query = new BoardOffsetQuery(topology, dx, dz);
        } else {
            if (tokens.Length != 4) {
                throw Invalid("neighbour requires exactly one direction token");
            }
            var direction = topology.Direction(tokens[3]);
            if (direction < 0) {
                throw Invalid($"'{tokens[3]}' is not a direction of '{board.Topology}'");
            }
            query = new BoardNeighbourQuery(topology, direction);
        }
        return new ResolvedOperand(
            operand: new BoardOperand(row: row.Name, key: key, keyFrom: keyFrom, stateHandle: ResolveHandle(context: context, name: row.Name), board: query),
            describe: name
        );
    }
}
