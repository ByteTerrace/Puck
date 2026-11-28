using System.Globalization;

namespace Puck.State.Rules;

public static partial class RuleCompiler {
    // A tally key is an integer: a literal one, or any live key spelling (a local, an expression, a cell's value), whose
    // integer the interpreter resolves. A tally is never read or written whole by key-less spelling except the reset.
    private static (long Key, CompiledCellRef? KeyFrom) ResolveTallyKey(StateChannelRef? cell, string? key, StateRowDeclaration row, string ruleName, string verb, string keyFieldLabel, RuleCompileContext context) {
        if (TryResolveDynamicKey(cell: out var dynamicKey, context: context, keyFieldLabel: keyFieldLabel, reference: cell, ruleName: ruleName, verb: verb)) {
            return (0L, dynamicKey);
        }
        if (key is null) {
            throw new RuleException(
                detail: $"'{verb}' names tally '{row.Name.Value}' without a '{keyFieldLabel}' — a tally counts by key, so read and add to '{row.Name.Value}[key]'",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }
        if (!long.TryParse(s: key, style: NumberStyles.AllowLeadingSign, provider: CultureInfo.InvariantCulture, result: out var literal)) {
            throw new RuleException(
                detail: $"'{verb}' {keyFieldLabel} '{key}' on tally '{row.Name.Value}' is not an integer — a tally's keys are 64-bit integers",
                refusal: RuleRefusal.StateCellUnaddressable,
                ruleName: ruleName
            );
        }

        return (literal, null);
    }
    private static (long Key, CompiledCellRef? KeyFrom) ResolveTallyKey(StateChannelRef? cell, string? key, StateRowDeclaration row, string ruleName, in OperandSite site, RuleCompileContext context) =>
        ResolveTallyKey(cell: cell, context: context, key: key, keyFieldLabel: site.KeyFieldLabel, row: row, ruleName: ruleName, verb: site.Verb);
    // A tally takes two writes: an add to one key's count, and the key-less set to zero that empties it. Every other
    // write would land on the engine's table cells, so it is refused by name.
    private static IRuleEffect ResolveTallyWrite(StateChannelRef rowName, StateRowDeclaration row, StateDomain.Tally tally, LiveRow? liveRow, StateChannelRef? key, StateWriteKind write, decimal? value, StateChannelRef? fromState, StateChannelRef? fromKey, decimal? valueSeconds, string? text, ExpressionProgram? expression, string? vector, string ruleName, RuleCompileContext context, string verb) {
        RuleException Refuse(string detail) => new(detail: detail, refusal: RuleRefusal.EffectKindInadmissible, ruleName: ruleName);

        if ((text is not null) || (vector is not null) || (valueSeconds is not null)) {
            throw Refuse(detail: $"'{verb}' writes tally '{rowName}' with a text, vector or duration source — a tally counts with a whole number");
        }

        // A family member a firing selects live is written as its first candidate's shape: every member is a tally of
        // the one capacity the family declares.
        var rowOrdinal = ((liveRow is { } selection) ? FirstCandidate(row: selection) : ResolveRowOrdinal(context: context, name: rowName.Spelling));

        if (liveRow is { } members) {
            RequireOneTallyShape(live: members, context: context, ruleName: ruleName, tally: tally, verb: verb);
        }
        if (write == StateWriteKind.Set) {
            if ((key is null) && (value == 0m) && (expression is null) && (fromState is null)) {
                return new TallyResetEffect(describe: $"{verb} {rowName} = 0 (empties the tally)", rowFrom: liveRow, rowOrdinal: rowOrdinal);
            }
            throw Refuse(detail: $"'{verb}' sets tally '{rowName}' — a tally is counted with '{rowName}[key] += n' and emptied with '{rowName} = 0'");
        }

        var (literalKey, keyFrom) = ResolveTallyKey(cell: key, context: context, key: key?.Spelling, keyFieldLabel: "key", row: row, ruleName: ruleName, verb: verb);

        return new TallyAddEffect(
            capacity: tally.Capacity,
            describe: $"{verb} {rowName}[{key}] += n",
            keyFrom: keyFrom,
            literalKey: literalKey,
            rowFrom: liveRow,
            rowOrdinal: rowOrdinal,
            source: ResolveWholeNumberSource(context: context, expression: expression, fromKey: fromKey, fromState: fromState, ruleName: ruleName, value: value, verb: verb)
        );
    }
    // Every member a live selection can reach is a tally of one capacity, so the capacity the effect walks with is each
    // member's.
    private static void RequireOneTallyShape(LiveRow live, StateDomain.Tally tally, string ruleName, string verb, RuleCompileContext context) {
        var rows = new List<CellAccess>();

        live.CollectRows(into: rows, isSet: false);
        foreach (var row in rows) {
            if (context.FindRowAt(rowOrdinal: row.RowOrdinal)?.EffectiveDomain is not StateDomain.Tally { } member || (member.Capacity != tally.Capacity)) {
                throw new RuleException(
                    detail: $"'{verb}' selects '{live.Spelling}' live, whose members are not all tallies of capacity {tally.Capacity.ToString(provider: CultureInfo.InvariantCulture)}",
                    refusal: RuleRefusal.EffectKindInadmissible,
                    ruleName: ruleName
                );
            }
        }
    }
    // Exactly one whole-number source: a literal, an Int expression, or an Int copy of a cell.
    private static CompiledValueSource ResolveWholeNumberSource(decimal? value, ExpressionProgram? expression, StateChannelRef? fromState, StateChannelRef? fromKey, string ruleName, RuleCompileContext context, string verb) {
        if (
            ((((value is not null) ? 1 : 0) + ((expression is not null) ? 1 : 0)) + ((fromState is not null) ? 1 : 0)) != 1 ||
            ((fromKey is not null) && (fromState is null))
        ) {
            throw new RuleException(
                detail: $"'{verb}' must name exactly one whole-number 'value', 'expression' or Int 'fromState'",
                refusal: RuleRefusal.EffectSourceAmbiguous,
                ruleName: ruleName
            );
        }
        if (value is { } literal) {
            return CompiledValueSource.Constant(rawValue: LiteralToRaw(kind: CellKind.Int, literal: literal, ruleName: ruleName, verb: verb));
        }
        if (expression is not null) {
            return CompiledValueSource.FromExpression(
                expression: CompileExpression(context: context, expression: expression, kind: CellKind.Int, ruleName: ruleName, verb: verb),
                kind: CellKind.Int
            );
        }

        var operand = ResolveOperand(
            cell: fromKey,
            context: context,
            operand: fromState!,
            site: new OperandSite(AllowText: false, FieldLabel: "fromState", KeyFieldLabel: "fromKey", RuleName: ruleName, Verb: verb)
        );

        return ConvertOperand(operand.Operand, CellKind.Int, ruleName, verb);
    }
}
