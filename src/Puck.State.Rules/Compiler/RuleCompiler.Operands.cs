using System.Globalization;
using Puck.Maths;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    /// <summary>Resolves a qualified pool field (<c>binding.field</c>) in the active lexical instance scope.</summary>
    public static bool TryResolveInstanceField(StateChannelRef reference, RuleCompileContext context, out RuleInstanceBinding binding, out StatePoolFieldDescriptor field) {
        ArgumentNullException.ThrowIfNull(argument: context);
        if (reference.PoolField is not { Binding: { } bindingName, Pool: null, Slot: null } typed) {
            binding = default;
            field = default;
            return false;
        }
        if (!context.TryInstanceBinding(binding: out binding, name: bindingName)) {
            field = default;
            return false;
        }

        foreach (var candidate in binding.Pool.Fields) {
            if (string.Equals(a: candidate.Name.Value, b: typed.Field, comparisonType: StringComparison.Ordinal)) {
                field = candidate;
                return true;
            }
        }
        field = default;
        return false;
    }
    /// <summary>Determines whether a row declares the cell a literal key names: an authored cell, or, on a board
    /// row, any cell of its topology, which every board declares whether or not it holds a value.</summary>
    /// <param name="row">The row read.</param>
    /// <param name="key">The literal cell key.</param>
    /// <param name="context">The compile context the board's topology resolves in.</param>
    /// <returns><see langword="true"/> when a read of the cell can see a value.</returns>
    public static bool DeclaresCell(StateRow row, string key, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: row);
        ArgumentNullException.ThrowIfNull(argument: context);

        return (row.HasCell(key: key) || (
            (row.EffectiveDomain is StateDomain.CellsOf board) &&
            (context.FindTopology(name: board.Topology) is { } topology) &&
            long.TryParse(provider: CultureInfo.InvariantCulture, result: out var ordinal, s: key, style: NumberStyles.None) &&
            (ordinal < topology.CellCount)
        ));
    }

    private static bool TryResolveStaticInstanceField(StateChannelRef reference, StateChannelRef? cell, RuleCompileContext context, out StatePoolDescriptor? pool, out StatePoolFieldDescriptor field, out StateInstanceHandle handle) {
        pool = null;
        field = default;
        handle = default;
        if ((reference.PoolField is not { Pool: { } poolName, Slot: { } slot, Binding: null } typed) || (cell is not null) || !context.TryStaticPoolHandle(handle: out handle, pool: out pool, poolName: poolName, slot: slot) || (pool is null)) {
            return false;
        }
        foreach (var candidate in pool.Fields) {
            if (string.Equals(a: candidate.Name.Value, b: typed.Field, comparisonType: StringComparison.Ordinal)) {
                field = candidate;
                return true;
            }
        }
        pool = null;
        handle = default;
        return false;
    }

    /// <summary>Resolves any read operand — a compareState's primary (state, key) pair, its comparand, a
    /// setState/addState's live copy source, an expression's state token — through the same reserved-channel/state-row
    /// walk, so no two of them can drift into different readings of the same name. The registered families are
    /// consulted first, then the library's own channels, then the declared rows.</summary>
    /// <param name="operand">The authored operand: a row name, or a reserved channel as a call.</param>
    /// <param name="cell">The authored cell key, or <see langword="null"/>.</param>
    /// <param name="site">Where the operand is spelled.</param>
    /// <param name="context">The compile context.</param>
    /// <returns>The resolved operand.</returns>
    public static ResolvedOperand ResolveOperand(StateChannelRef operand, StateChannelRef? cell, in OperandSite site, RuleCompileContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);
        ArgumentNullException.ThrowIfNull(argument: operand);

        // The spellings are what a refusal and a read-back quote; the call is what the walk dispatches on.
        var call = operand.Call;
        var key = cell?.Spelling;
        var name = operand.Spelling;

        foreach (var family in context.Vocabulary.Operands) {
            if (family.TryCompile(
                cell: cell,
                context: context,
                fact: out var fact,
                reference: operand,
                site: in site
            )) {
                context.Needs.AddFact(fact: fact);

                return new ResolvedOperand(
                    describe: Describe(
                        key: key,
                        name: name
                    ),
                    operand: fact!
                );
            }
        }

        if (TryResolveInstanceField(binding: out var instance, context: context, field: out var field, reference: operand)) {
            if (cell is not null) {
                throw new RuleException(
                    detail: $"'{site.Verb}' addresses pool field '{name}' with a cell key — a qualified field already selects one instance cell",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: site.RuleName
                );
            }
            if ((field.Kind == CellKind.Text) && !site.AllowText) {
                throw new RuleException(
                    detail: $"pool field '{name}' is kind=Text — a rule compares numbers, never text",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: site.RuleName
                );
            }
            if (field.Kind == CellKind.Vector) {
                throw new RuleException(
                    detail: $"pool field '{name}' is kind=Vector — a scalar operand site cannot read it",
                    refusal: RuleRefusal.VectorEffectNotAdmitted,
                    ruleName: site.RuleName
                );
            }
            return new ResolvedOperand(
                describe: name,
                operand: new InstanceFieldOperand(pool: instance.Pool, field: field, bindingSlot: instance.Slot)
            );
        }
        if (TryResolveStaticInstanceField(cell: cell, context: context, field: out var staticField, handle: out var staticHandle, pool: out var staticPool, reference: operand)) {
            if ((staticField.Kind == CellKind.Text) && !site.AllowText) {
                throw new RuleException(
                    detail: $"pool field '{name}' is kind=Text — a rule compares numbers, never text",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: site.RuleName
                );
            }
            if (staticField.Kind == CellKind.Vector) {
                throw new RuleException(
                    detail: $"pool field '{name}' is kind=Vector — a scalar operand site cannot read it",
                    refusal: RuleRefusal.VectorEffectNotAdmitted,
                    ruleName: site.RuleName
                );
            }
            return new ResolvedOperand(
                describe: name,
                operand: new StaticInstanceFieldOperand(field: staticField, handle: staticHandle, pool: staticPool!)
            );
        }
        if (operand.PoolField is { Binding: { } bindingName } bindingField) {
            throw new RuleException(
                detail: (context.TryInstanceBinding(binding: out _, name: bindingName)
                    ? $"pool binding '{bindingName}' has no field '{bindingField.Field}'"
                    : $"pool binding '{bindingName}' is not live at this use"),
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: site.RuleName
            );
        }
        if (operand.PoolField is { Pool: { } poolName, Slot: { } slot } poolField) {
            if (!context.Catalog.TryGetPool(name: CellName.Parse(candidate: poolName), pool: out var declaredPool) || (declaredPool is null)) {
                throw new RuleException(detail: $"'{poolName}' names no declared pool", refusal: RuleRefusal.StateRowUnknown, ruleName: site.RuleName);
            }
            throw new RuleException(
                detail: ((((uint)slot) >= ((uint)declaredPool.Capacity))
                    ? $"pool '{poolName}' slot {slot} lies outside capacity {declaredPool.Capacity}"
                    : $"pool '{poolName}' has no field '{poolField.Field}'"),
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: site.RuleName
            );
        }

        var ruleName = site.RuleName;

        // A reserved channel is dispatched on its name; what each argument means is the channel's own compiler's
        // business.
        switch (call?.Channel) {
            case "phase":
                return ResolvePhaseOperand(
                    call: call,
                    context: context,
                    key: cell,
                    name: name,
                    ruleName: ruleName
                );
            case "board":
                return ResolveBoardOperand(
                    call: call,
                    context: context,
                    key: cell,
                    name: name,
                    ruleName: ruleName
                );
            case "match":
                return ResolvePatternOperand(
                    call: call,
                    context: context,
                    key: cell,
                    name: name,
                    ruleName: ruleName
                );
            case "history":
                return ResolveHistoryOperand(
                    call: call,
                    context: context,
                    key: cell,
                    name: name,
                    ruleName: ruleName
                );
            case "local":
                return ResolveLocalOperand(
                    context: context,
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
            case "table":
                return ResolveTableOperand(
                    context: context,
                    key: cell,
                    keyFieldLabel: site.KeyFieldLabel,
                    name: name,
                    ruleName: ruleName
                );
            case "search" when (name == RuleFacts.SearchPly):
                RefuseKeyOnReservedChannel(key: cell, keyFieldLabel: site.KeyFieldLabel, name: name, ruleName: ruleName);
                context.Needs.MarkVolatile();

                return new ResolvedOperand(describe: name, operand: SearchPlyOperand.Instance);
        }

        var describe = Describe(
            key: key,
            name: name
        );

        if (string.Equals(
            a: name,
            b: RuleFacts.Tick,
            comparisonType: StringComparison.Ordinal
        )) {
            RefuseKeyOnReservedChannel(
                key: cell,
                keyFieldLabel: site.KeyFieldLabel,
                name: name,
                ruleName: ruleName
            );
            context.Needs.MarkReadsTick();

            return new ResolvedOperand(
                describe: describe,
                operand: TickOperand.Instance
            );
        }
        if (call?.Channel == "reduce") {
            return ResolveReduceOperand(
                call: call,
                context: context,
                describe: describe,
                key: cell,
                keyFieldLabel: site.KeyFieldLabel,
                name: name,
                ruleName: ruleName
            );
        }
        if (call?.Channel == "symmetry") {
            return ResolveSymmetryOperand(
                call: call,
                context: context,
                describe: describe,
                key: cell,
                name: name,
                site: in site
            );
        }
        if (TryResolveLiveRow(
            context: context,
            name: name,
            row: out var live,
            ruleName: ruleName,
            where: $"'{site.Verb}' {site.FieldLabel}"
        )) {
            return ResolveLiveRowCell(
                context: context,
                describe: describe,
                key: cell,
                live: live!,
                site: in site
            );
        }
        if (name.StartsWith(
            comparisonType: StringComparison.Ordinal,
            value: StateRow.ReservedNamePrefix
        )) {
            throw new RuleException(
                detail: $"'{name}' carries the reserved '{StateRow.ReservedNamePrefix}' prefix but names none of the reserved channels ({ReservedChannels(context: context)})",
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName
            );
        }
        // A declared row name is dot-free by construction — this only ever fires for an author reaching for a
        // "row.key" spelling in one string.
        if (name.Contains(value: '.')) {
            throw new RuleException(
                detail: $"'{site.FieldLabel}' value '{name}' carries a '.' — a state row name is never dotted; address the cell with '{site.KeyFieldLabel}' instead of dotting it into '{site.FieldLabel}'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var row = (context.FindRow(name: name)
            ?? throw new RuleException(
            detail: $"'{name}' names no state row, and is not a reserved channel ({ReservedChannels(context: context)})",
            refusal: RuleRefusal.StateRowUnknown,
            ruleName: ruleName
        ));

        if (
            (row.Kind == CellKind.Text) &&
            !site.AllowText
        ) {
            throw new RuleException(
                detail: $"state row '{name}' is kind=Text — a rule compares numbers, never text",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var rowOrdinal = ResolveRowOrdinal(
            context: context,
            name: name
        );

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            reference: cell,
            keyFieldLabel: site.KeyFieldLabel,
            ruleName: ruleName,
            verb: site.Verb
        )) {
            if (!row.IsKeyed) {
                throw new RuleException(
                    detail: $"'{site.Verb}' {site.KeyFieldLabel} '{key}' addresses a cell by indirection, but row '{name}' is not keyed",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            return new ResolvedOperand(
                describe: describe,
                operand: new StateCellOperand(
                    key: default,
                    keyFrom: dynamicKey,
                    rowOrdinal: rowOrdinal,
                    valueKind: row.Kind
                )
            );
        }

        var resolvedKey = ResolveKey(
            key: key,
            keyFieldLabel: site.KeyFieldLabel,
            row: row,
            ruleName: ruleName,
            verb: site.Verb
        );

        // A read operand must address a cell the row declares today: an undeclared cell reads 0 forever with no
        // refusal anywhere, so it refuses at compile instead. A draw site's slot cell is declared by its facet.
        if (
            !DeclaresCell(context: context, key: resolvedKey, row: row) &&
            !(row.IsDraw && (resolvedKey == StateRow.SlotKey.Value))
        ) {
            throw new RuleException(
                detail: $"'{site.Verb}' {site.FieldLabel} '{name}' reads cell '{resolvedKey}', which the row does not declare — an undeclared cell reads 0 forever; declare the cell first (an authored 0 is fine)",
                refusal: RuleRefusal.StateCellUndeclared,
                ruleName: ruleName
            );
        }

        return new ResolvedOperand(
            describe: describe,
            operand: new StateCellOperand(
                key: InternKey(
                    context: context,
                    name: resolvedKey
                ),
                keyFrom: null,
                rowOrdinal: rowOrdinal,
                valueKind: row.Kind
            )
        );
    }
    /// <summary>Parses a <c>$reduce:</c> op token.</summary>
    /// <param name="text">The token.</param>
    /// <param name="op">The op, on success.</param>
    /// <returns><see langword="true"/> when the token names an aggregate.</returns>
    public static bool TryParseReduceOp(string text, out StateReduceOp op) {
        op = (text switch {
            "max" => StateReduceOp.Max,
            "min" => StateReduceOp.Min,
            "sum" => StateReduceOp.Sum,
            "count" => StateReduceOp.Count,
            "arrangementRank" => StateReduceOp.ArrangementRank,
            _ => StateReduceOp.None,
        });

        return (op != StateReduceOp.None);
    }

    private static string Describe(string name, string? key) => $"{name}{((key is { } spelled)
        ? $".{spelled}"
        : string.Empty)}";
    private static ResolvedOperand ResolveLocalOperand(string name, StateChannelRef? key, string ruleName, string keyFieldLabel, RuleCompileContext context) {
        RefuseKeyOnReservedChannel(
            key: key,
            keyFieldLabel: keyFieldLabel,
            name: name,
            ruleName: ruleName
        );

        if (name.Length <= RuleFacts.LocalPrefix.Length) {
            throw new RuleException(
                detail: $"'{name}' does not spell '{RuleFacts.LocalPrefix}<name>'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var bound = name[RuleFacts.LocalPrefix.Length..];
        var scope = (context.RuleLocals ?? []);

        for (var ordinal = 0; (ordinal < scope.Count); ordinal++) {
            if (string.Equals(
                a: scope[ordinal].Name,
                b: bound,
                comparisonType: StringComparison.Ordinal
            )) {
                return new ResolvedOperand(
                    describe: name,
                    operand: new LocalOperand(
                        name: bound,
                        ordinal: ordinal,
                        source: scope[ordinal],
                        valueKind: scope[ordinal].Kind
                    )
                );
            }
        }

        throw new RuleException(
            detail: $"'{name}' names no binding declared before it — a rule's 'bindings' list is read in declared order by later bindings, the gate, and the effects",
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName
        );
    }
    // $history:<row>:<age> — the value pushed age pushes ago. A whole-number age is bounded by the ring here; any
    // other spelling is an int expression evaluated per read, whose result outside 0..capacity-1 reads the ring's
    // empty value exactly as an out-of-range constant would. The age is everything after the second colon, so a
    // ternary's own colon stays inside it.
    private static ResolvedOperand ResolveHistoryOperand(ChannelCall call, string name, StateChannelRef? key, string ruleName, RuleCompileContext context) {
        RuleException Invalid(string detail) => new(
            detail: detail,
            refusal: RuleRefusal.StateCellUnaddressable,
            ruleName: ruleName
        );
        var tokens = call.Tokens();

        if (
            (tokens.Length != 3) ||
            (key is not null)
        ) {
            throw Invalid(detail: "history read requires $history:<row>:<age> and no key");
        }

        var row = (context.FindRow(name: tokens[1]) ?? throw Invalid(detail: $"'{tokens[1]}' names no state row"));

        if (row.EffectiveDomain is not StateDomain.Ring history) {
            throw Invalid(detail: $"'{tokens[1]}' is not a history row");
        }

        var literal = int.TryParse(
            tokens[2],
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var age
        );

        if (
            literal &&
            (age >= history.Capacity)
        ) {
            throw Invalid(detail: $"age must be 0..{(history.Capacity - 1)} on '{tokens[1]}'");
        }

        CompiledExpressionToken[]? ageExpression = null;

        if (!literal) {
            if (!ExpressionSpelling.TryParse(
                error: out var error,
                program: out var parsed,
                text: tokens[2]
            )) {
                throw Invalid(detail: $"age '{tokens[2]}' on '{tokens[1]}' is neither 0..{(history.Capacity - 1)} nor an expression: {error}");
            }

            ageExpression = CompileExpression(
                context: context,
                expression: parsed,
                kind: CellKind.Int,
                ruleName: ruleName,
                verb: $"'{name}' age"
            );
            RuleDataflow.CollectExpressionFacts(
                into: context.Needs,
                tokens: ageExpression
            );
        }

        return new ResolvedOperand(
            describe: name,
            operand: new HistoryOperand(
                age: age,
                ageExpression: ageExpression,
                ring: history,
                rowOrdinal: ResolveRowOrdinal(
                    context: context,
                    name: tokens[1]
                ),
                valueKind: row.Kind
            )
        );
    }
    // A live row's cell: the key resolves exactly as a fixed row's would, but no cell can be proven declared at
    // compile — a zone's members come and go — so a literal key is only proven well-formed.
    private static ResolvedOperand ResolveLiveRowCell(LiveRow live, StateChannelRef? key, in OperandSite site, RuleCompileContext context, string describe) {
        var ruleName = site.RuleName;
        var kind = ((live.Table is { } table)
            ? table.Kind
            : (context.FindRowAt(rowOrdinal: live.Family!.Value.FirstOrdinal)?.Kind ?? CellKind.Int)
        );

        live.CollectFacts(into: context.Needs);
        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            reference: key,
            keyFieldLabel: site.KeyFieldLabel,
            ruleName: ruleName,
            verb: site.Verb
        )) {
            return new ResolvedOperand(
                describe: describe,
                operand: new StateCellOperand(
                    key: default,
                    keyFrom: dynamicKey,
                    rowFrom: live,
                    rowOrdinal: -1,
                    valueKind: kind
                )
            );
        }
        if (key is null) {
            throw new RuleException(
                detail: $"'{site.Verb}' names live row '{live.Spelling}' without a '{site.KeyFieldLabel}' — a live row is keyed by its cells, so name the one you mean",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }
        if (!CellName.TryParse(
            candidate: key?.Spelling,
            name: out var parsed,
            reason: out var reason
        )) {
            throw new RuleException(
                detail: $"'{site.Verb}' {site.KeyFieldLabel} '{key}' {reason}",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new ResolvedOperand(
            describe: describe,
            operand: new StateCellOperand(
                key: context.Catalog.Keys.Intern(name: parsed),
                keyFrom: null,
                rowFrom: live,
                rowOrdinal: -1,
                valueKind: kind
            )
        );
    }
    // $phase:<row> — the row's own generation, the same value a PhaseGuard checks against it.
    private static ResolvedOperand ResolvePhaseOperand(ChannelCall call, string name, StateChannelRef? key, string ruleName, RuleCompileContext context) {
        var tokens = call.Tokens();

        if (
            (key is not null) ||
            (tokens.Length != 2) ||
            (context.FindRow(name: tokens[1])?.Phase is null)
        ) {
            throw new RuleException(
                detail: "phase query requires $phase:<row>, without key",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return new ResolvedOperand(
            describe: name,
            operand: new PhaseOperand(rowOrdinal: ResolveRowOrdinal(
                context: context,
                name: tokens[1]
            ))
        );
    }
    private static ResolvedOperand ResolveReduceOperand(ChannelCall call, string name, StateChannelRef? key, string ruleName, string keyFieldLabel, RuleCompileContext context, string describe) {
        RefuseKeyOnReservedChannel(
            key: key,
            keyFieldLabel: keyFieldLabel,
            name: name,
            ruleName: ruleName
        );

        var op = StateReduceOp.None;

        if (
            (call.Count < 2) ||
            !TryParseReduceOp(
            op: out op,
            text: call.Text(index: 0)
        ) ||
            string.IsNullOrEmpty(value: call.Text(index: 1))
        ) {
            throw new RuleException(
                detail: $"'{name}' does not spell '{RuleFacts.ReducePrefix}<max|min|sum|count|arrangementRank>:<row>'",
                refusal: RuleRefusal.ReduceChannelMalformed,
                ruleName: ruleName
            );
        }

        var parts = call.Texts(start: 1);
        var rowName = parts[0];
        string? filterRowName = null;
        (decimal Lower, decimal Upper)? bounds = null;

        for (var index = 1; (index < parts.Length);) {
            if (
                (parts[index] == "where") &&
                (filterRowName is null) &&
                ((index + 1) < parts.Length) &&
                (parts[(index + 1)].Length > 0)
            ) {
                filterRowName = parts[(index + 1)];
                index += 2;
            } else if (
                (parts[index] == "between") &&
                (bounds is null) &&
                ((index + 2) < parts.Length) &&
                decimal.TryParse(
                parts[(index + 1)],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var lower
            ) &&
                decimal.TryParse(
                parts[(index + 2)],
                NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var upper
            ) &&
                (lower <= upper)
            ) {
                bounds = (lower, upper);
                index += 3;
            } else {
                throw new RuleException(
                    detail: $"'{name}' takes optional ':where:<filterRow>' and ':between:<lower>:<upper>' filters once each, with lower <= upper",
                    refusal: RuleRefusal.ReduceChannelMalformed,
                    ruleName: ruleName
                );
            }
        }

        // A live row selected out of a zone table is an ordered keyed zone by construction, in the table's one kind.
        var capacity = 0L;
        var domainOrdinal = -1;
        var keyed = true;
        var ordered = true;
        var rowKind = CellKind.Int;
        var rowOrdinal = -1;
        LiveRow? live = null;

        if (
            (op == StateReduceOp.Count) &&
            (filterRowName is null) &&
            (bounds is null) &&
            CellName.TryParse(candidate: rowName, name: out var poolName, reason: out _) &&
            context.Catalog.TryGetPool(name: poolName, pool: out var pool) &&
            (pool is not null)
        ) {
            capacity = pool.Capacity;
            rowOrdinal = pool.DomainRowOrdinal;
        } else if (!TryResolveLiveRow(
            context: context,
            name: rowName,
            row: out live,
            ruleName: ruleName,
            where: $"'{name}' row"
        )) {
            var reduceRow = ResolveNumericRow(
                channel: name,
                context: context,
                malformed: RuleRefusal.ReduceChannelMalformed,
                name: rowName,
                requireKeyed: false,
                ruleName: ruleName
            );

            capacity = context.RowCapacity(name: rowName);
            keyed = reduceRow.IsKeyed;
            ordered = (reduceRow.EffectiveDomain is StateDomain.KeysOf { Ordered: true });
            rowKind = reduceRow.Kind;
            rowOrdinal = ResolveRowOrdinal(
                context: context,
                name: rowName
            );
            if (reduceRow.EffectiveDomain is StateDomain.KeysOf domain) {
                domainOrdinal = ResolveRowOrdinal(
                    context: context,
                    name: domain.Row.Value
                );
            }
        } else {
            live!.CollectFacts(into: context.Needs);
            if (live.Table is { } table) {
                domainOrdinal = ResolveRowOrdinal(
                    context: context,
                    name: table.TokenDomain
                );
                rowKind = table.Kind;
            } else {
                rowKind = (context.FindRowAt(rowOrdinal: live.Family!.Value.FirstOrdinal)?.Kind ?? CellKind.Int);
            }
        }

        if (
            (op == StateReduceOp.ArrangementRank) &&
            (!ordered || (filterRowName is not null) || (bounds is not null))
        ) {
            throw new RuleException(
                detail: $"'{name}' ranks an ordered zone's arrangement and takes no filters",
                refusal: RuleRefusal.ReduceChannelMalformed,
                ruleName: ruleName
            );
        }

        var filterOrdinal = -1;

        if (filterRowName is not null) {
            _ = ResolveNumericRow(
                channel: name,
                context: context,
                malformed: RuleRefusal.ReduceChannelMalformed,
                name: filterRowName,
                requireKeyed: true,
                ruleName: ruleName
            );
            if (!keyed) {
                throw new RuleException(
                    detail: $"'{name}' applies a keyed filter to non-keyed row '{rowName}'",
                    refusal: RuleRefusal.ReduceChannelMalformed,
                    ruleName: ruleName
                );
            }

            filterOrdinal = ResolveRowOrdinal(
                context: context,
                name: filterRowName
            );
        }

        return new ResolvedOperand(
            describe: describe,
            operand: new ReductionOperand(
                capacity: capacity,
                domainOrdinal: domainOrdinal,
                filterOrdinal: filterOrdinal,
                range: ((bounds is { } range)
                ? (LiteralToRaw(
                        kind: rowKind,
                        literal: range.Lower,
                        ruleName: ruleName,
                        verb: "reduce"
                    ), LiteralToRaw(
                        kind: rowKind,
                        literal: range.Upper,
                        ruleName: ruleName,
                        verb: "reduce"
                    ))
                : null),
                reduce: op,
                rowFrom: live,
                rowOrdinal: rowOrdinal,
                valueKind: ((op is StateReduceOp.Count or StateReduceOp.ArrangementRank)
                ? CellKind.Int
                : rowKind)
            )
        );
    }
    // $symmetry:<function>[:<argument>]:<row> — the row is the last token, the function the first, and whatever
    // sits between is the argument the function takes.
    private static ResolvedOperand ResolveSymmetryOperand(ChannelCall call, string name, StateChannelRef? key, in OperandSite site, RuleCompileContext context, string describe) {
        static RuleException Malformed(string ruleName, string name, string detail) => new(
            detail: $"'{name}' {detail} — a symmetry channel spells '{RuleFacts.SymmetryPrefix}<ring|antipode|canonicalRay|cycle:<steps>|reflect:<node|cell:<row>[.<key>]>|orthogonal:<node|cell:<row>[.<key>]>|innerProduct:<node|cell:<row>[.<key>]>|projectionX|projectionY>:<row>'",
            refusal: RuleRefusal.SymmetryChannelMalformed,
            ruleName: ruleName
        );

        var ruleName = site.RuleName;
        var tokens = call.Texts();

        if (tokens.Length < 2) {
            throw Malformed(
                detail: "names no source row",
                name: name,
                ruleName: ruleName
            );
        }

        var rowName = tokens[^1];
        var function = (tokens[0] switch {
            "ring" => SymmetryFunction.Ring,
            "antipode" => SymmetryFunction.Antipode,
            "canonicalRay" => SymmetryFunction.CanonicalRay,
            "cycle" => SymmetryFunction.Cycle,
            "reflect" => SymmetryFunction.Reflect,
            "orthogonal" => SymmetryFunction.Orthogonal,
            "innerProduct" => SymmetryFunction.InnerProduct,
            "projectionX" => SymmetryFunction.ProjectionX,
            "projectionY" => SymmetryFunction.ProjectionY,
            _ => throw Malformed(
            detail: $"names no symmetry function '{tokens[0]}'",
            name: name,
            ruleName: ruleName
        ),
        });
        var argument = string.Join(
            separator: ':',
            values: tokens[1..^1]
        );
        var takesArgument = (function is SymmetryFunction.Cycle or SymmetryFunction.Reflect or SymmetryFunction.Orthogonal or SymmetryFunction.InnerProduct);

        if (takesArgument == (argument.Length == 0)) {
            throw Malformed(
                detail: (takesArgument
                ? $"'{tokens[0]}' needs an argument"
                : $"'{tokens[0]}' takes no argument"),
                name: name,
                ruleName: ruleName
            );
        }

        var source = ResolveOperand(
            context: context,
            cell: key,
            operand: StateChannelRef.Parse(spelling: rowName),
            site: site with { AllowText = false }
        );

        if (source.Operand is not StateCellOperand { RowFrom: null } sourceCell) {
            throw Malformed(
                detail: $"names '{rowName}', which is not a fixed state row — the source of a symmetry read is a declared row's cell",
                name: name,
                ruleName: ruleName
            );
        }

        var literal = 0L;
        CompiledCellRef? other = null;

        if (takesArgument) {
            if (function == SymmetryFunction.Cycle) {
                if (!long.TryParse(
                    s: argument,
                    style: NumberStyles.AllowLeadingSign,
                    provider: CultureInfo.InvariantCulture,
                    result: out literal
                )) {
                    throw Malformed(
                        detail: $"'cycle' needs a whole number of ring steps, not '{argument}'",
                        name: name,
                        ruleName: ruleName
                    );
                }
            } else if (argument.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: "cell:"
            )) {
                var reference = argument["cell:".Length..];
                var dot = reference.IndexOf(value: '.');
                var otherKey = ((dot < 0)
                    ? null
                    : reference[(dot + 1)..]
                );
                var otherRow = ((dot < 0)
                    ? reference
                    : reference[..dot]
                );
                var resolved = ResolveOperand(
                    context: context,
                    cell: StateChannelRef.OfNullable(spelling: otherKey),
                    operand: StateChannelRef.Parse(spelling: otherRow),
                    site: site with { AllowText = false }
                );

                if (resolved.Operand is not StateCellOperand { KeyFrom: null } otherCell) {
                    throw Malformed(
                        detail: $"argument '{argument}' does not name a declared row's cell by a literal key",
                        name: name,
                        ruleName: ruleName
                    );
                }

                other = new CompiledCellRef(
                    Key: otherCell.Key,
                    RowOrdinal: otherCell.RowOrdinal
                );
            } else if (
                !long.TryParse(
                s: argument,
                style: NumberStyles.None,
                provider: CultureInfo.InvariantCulture,
                result: out literal
            ) ||
                (literal >= SymmetryLattice.NodeCount)
            ) {
                throw Malformed(
                    detail: $"argument '{argument}' is neither a node 0..{(SymmetryLattice.NodeCount - 1)} nor 'cell:<row>[.<key>]'",
                    name: name,
                    ruleName: ruleName
                );
            }
        }

        return new ResolvedOperand(
            describe: describe,
            operand: new SymmetryOperand(
                key: sourceCell.Key,
                keyFrom: sourceCell.KeyFrom,
                rowOrdinal: sourceCell.RowOrdinal,
                symmetry: function,
                symmetryArgument: literal,
                symmetryOtherCell: other,
                valueKind: ((function is SymmetryFunction.ProjectionX or SymmetryFunction.ProjectionY)
                ? CellKind.Fixed
                : CellKind.Int)
            )
        );
    }
    // $table:<name>:<key> for a single-value table, $table:<name>:<column>:<key> for a column table.
    private static ResolvedOperand ResolveTableOperand(string name, StateChannelRef? key, string ruleName, RuleCompileContext context, string keyFieldLabel) {
        RefuseKeyOnReservedChannel(
            key: key,
            keyFieldLabel: keyFieldLabel,
            name: name,
            ruleName: ruleName
        );

        if (name.Length <= RuleFacts.TablePrefix.Length) {
            throw new RuleException(
                detail: $"'{name}' does not spell '{RuleFacts.TablePrefix}<table>:<key>' or '{RuleFacts.TablePrefix}<table>:<column>:<key>'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        var rest = name[RuleFacts.TablePrefix.Length..];
        var firstColon = rest.IndexOf(value: ':');

        if (
            (firstColon <= 0) ||
            (firstColon == (rest.Length - 1))
        ) {
            throw new RuleException(
                detail: $"'{name}' does not spell '{RuleFacts.TablePrefix}<table>:<key>' or '{RuleFacts.TablePrefix}<table>:<column>:<key>'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        string[] tokens = [rest[..firstColon], rest[(firstColon + 1)..]];

        if (!context.TryTable(
            error: out var error,
            name: tokens[0],
            ordinal: out var ordinal,
            table: out var table
        )) {
            throw new RuleException(
                detail: ((ordinal < 0)
                ? $"'{name}' names table '{tokens[0]}', which the document's tables do not declare"
                : $"'{name}': table '{tokens[0]}' cannot load — {error}"),
                refusal: RuleRefusal.StateRowUnknown,
                ruleName: ruleName
            );
        }

        var column = 0;
        var spelledKey = tokens[1];

        if (table!.ColumnNames.Count > 0) {
            var columnColon = spelledKey.IndexOf(value: ':');
            var columnName = ((columnColon < 0)
                ? spelledKey
                : spelledKey[..columnColon]
            );

            column = table.Column(name: columnName);
            if (
                (column < 0) ||
                (columnColon < 0) ||
                (columnColon == (spelledKey.Length - 1))
            ) {
                throw new RuleException(
                    detail: $"'{name}': table '{tokens[0]}' has columns [{string.Join(
                        separator: ", ",
                        values: table.ColumnNames
                    )}] and is read as '{RuleFacts.TablePrefix}{tokens[0]}:<column>:<key>'",
                    refusal: RuleRefusal.StateCellUnaddressable,
                    ruleName: ruleName
                );
            }

            spelledKey = spelledKey[(columnColon + 1)..];
        }

        CompiledCellRef? keyFrom = null;
        var literal = 0L;

        if (TryResolveDynamicKey(
            cell: out var dynamicKey,
            context: context,
            reference: StateChannelRef.Parse(spelling: spelledKey),
            keyFieldLabel: "key",
            ruleName: ruleName,
            verb: name
        )) {
            keyFrom = dynamicKey;
        } else if (!long.TryParse(
            s: spelledKey,
            style: NumberStyles.AllowLeadingSign,
            provider: CultureInfo.InvariantCulture,
            result: out literal
        )) {
            throw new RuleException(
                detail: $"'{name}' key '{spelledKey}' is not an integer, a '{RuleFacts.CellKeyPrefix}<row>:<key>' indirection, a '{RuleFacts.LocalPrefix}<name>' binding, an '{RuleFacts.ExpressionKeyPrefix}' expression, or a bound key token",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        } else if (!table.TryLookup(
            column: column,
            key: literal,
            raw: out _
        )) {
            throw new RuleException(
                detail: $"'{name}' names key {literal}, which table '{tokens[0]}' does not carry",
                refusal: RuleRefusal.StateCellUndeclared,
                ruleName: ruleName
            );
        }

        return new ResolvedOperand(
            describe: name,
            operand: new TableOperand(
                column: column,
                key: literal,
                keyFrom: keyFrom,
                name: tokens[0],
                table: table,
                valueKind: table.Kind
            )
        );
    }
}
