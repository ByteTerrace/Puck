using Puck.World.Protocol;

namespace Puck.World;

/// <summary>One state cell a rule reads or writes — a literal <c>row.key</c>, or a whole row when the key is resolved
/// live (a <c>$cell:</c> indirection, a bound <c>$each</c>, a push, a generate, a transform).</summary>
/// <param name="Row">The state row.</param>
/// <param name="Key">The literal key, or <see langword="null"/> for any key of the row.</param>
/// <param name="IsSet">For a write, whether it replaces the cell (a set) rather than accumulating into it (an add).</param>
public readonly record struct WorldRuleAccess(string Row, string? Key, bool IsSet = false) {
    /// <summary>Gets a value indicating whether two accesses can touch the same cell.</summary>
    /// <param name="other">The other access.</param>
    public bool Overlaps(WorldRuleAccess other) =>
        string.Equals(a: Row, b: other.Row, comparisonType: StringComparison.Ordinal) &&
        ((Key is null) || (other.Key is null) || string.Equals(a: Key, b: other.Key, comparisonType: StringComparison.Ordinal));

    /// <summary>Formats the access as <c>row.key</c> or <c>row.*</c>.</summary>
    public string Describe() => $"{Row}.{Key ?? "*"}";
}

/// <summary>The read and write sets of a compiled rule, walked from its gate, bindings, and effects (through
/// transactions and decision branches) — what the work sheet's writer count and the hazard read-back both rest
/// on.</summary>
public static class WorldRuleDataflow {
    /// <summary>Lists every state cell the rule reads: gate operands, binding and effect expressions, copy sources,
    /// and the cells its key indirections resolve through.</summary>
    /// <param name="rule">The compiled rule or interaction.</param>
    public static IReadOnlyList<WorldRuleAccess> Reads(CompiledWorldRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        var reads = new List<WorldRuleAccess>();
        ReadPredicates(predicates: rule.Gate, into: reads);
        foreach (var binding in (rule.Bindings ?? [])) {
            ReadExpression(tokens: binding.Expression, into: reads);
        }
        ReadEffects(effects: rule.Effects, into: reads);
        if (rule.Decision is { } decision) {
            ReadPredicates(predicates: decision.Interrupt ?? [], into: reads);
            ReadEffects(effects: decision.OnNoChoice, into: reads);
            foreach (var option in decision.Options) {
                ReadPredicates(predicates: option.Gate, into: reads);
                ReadExpression(tokens: option.Score, into: reads);
                ReadEffects(effects: option.Effects, into: reads);
            }
        }
        return Distinct(accesses: reads);
    }

    /// <summary>Lists every state cell the rule writes, through transactions and decision branches.</summary>
    /// <param name="rule">The compiled rule or interaction.</param>
    public static IReadOnlyList<WorldRuleAccess> Writes(CompiledWorldRule rule) {
        ArgumentNullException.ThrowIfNull(argument: rule);
        var writes = new List<WorldRuleAccess>();
        WriteEffects(effects: rule.Effects, into: writes);
        if (rule.Decision is { } decision) {
            WriteEffects(effects: decision.OnNoChoice, into: writes);
            foreach (var option in decision.Options) {
                WriteEffects(effects: option.Effects, into: writes);
            }
        }
        return Distinct(accesses: writes);
    }

    private static List<WorldRuleAccess> Distinct(List<WorldRuleAccess> accesses) {
        var seen = new HashSet<WorldRuleAccess>();
        var result = new List<WorldRuleAccess>(capacity: accesses.Count);
        foreach (var access in accesses) {
            if (seen.Add(item: access)) {
                result.Add(item: access);
            }
        }
        return result;
    }

    private static void ReadPredicates(CompiledWorldPredicate[] predicates, List<WorldRuleAccess> into) {
        foreach (var predicate in predicates) {
            if (predicate.Left is { } left) { ReadOperand(operand: left, into: into); }
            if (predicate.Comparand is { } comparand) { ReadOperand(operand: comparand, into: into); }
            if (predicate.LeftExpression is { } leftExpression) { ReadExpression(tokens: leftExpression, into: into); }
            if (predicate.RightExpression is { } rightExpression) { ReadExpression(tokens: rightExpression, into: into); }
        }
    }
    private static void ReadExpression(CompiledWorldExpressionToken[] tokens, List<WorldRuleAccess> into) {
        foreach (var token in tokens) {
            if (token.Operand is { } operand) { ReadOperand(operand: operand, into: into); }
        }
    }
    private static void ReadOperand(CompiledWorldOperand operand, List<WorldRuleAccess> into) {
        switch (operand.Value) {
            case StateCellOperand cell:
                into.Add(item: new WorldRuleAccess(Row: cell.Row, Key: (cell.KeyFrom is null) ? cell.Key : null));
                ReadRef(reference: cell.KeyFrom, into: into);
                break;
            case IStateAddressedOperand addressed:
                into.Add(item: new WorldRuleAccess(Row: addressed.Row, Key: null));
                ReadRef(reference: addressed.KeyFrom, into: into);
                break;
            case ReductionOperand reduction:
                into.Add(item: new WorldRuleAccess(Row: reduction.Row, Key: null));
                break;
            case HistoryOperand history:
                into.Add(item: new WorldRuleAccess(Row: history.Row, Key: null));
                break;
            case PhaseOperand phase:
                into.Add(item: new WorldRuleAccess(Row: phase.Row, Key: null));
                break;
            case TableOperand table:
                ReadRef(reference: table.KeyFrom, into: into);
                break;
        }
    }
    private static void ReadRef(CompiledCellRef? reference, List<WorldRuleAccess> into) {
        if (reference is { } cell) {
            into.Add(item: new WorldRuleAccess(Row: cell.Row, Key: (cell.Binding == RuleBinding.None) ? cell.Key : null));
        }
    }
    private static void ReadEffects(CompiledWorldEffect[] effects, List<WorldRuleAccess> into) {
        foreach (var effect in effects) {
            switch (effect.Value) {
                case TransactionEffect transaction:
                    ReadEffects(effects: transaction.Effects, into: into);
                    ReadEffects(effects: transaction.OnFailure, into: into);
                    continue;
                case IValueSourcedEffect sourced:
                    if (sourced.From is { } from) { ReadOperand(operand: from, into: into); }
                    if (sourced.Expression is { } expression) { ReadExpression(tokens: expression, into: into); }
                    break;
            }
            if (effect.Value is IStateAddressedEffect addressed && (addressed is IStateWriteEffect or RemoveStateCellEffect or GenerateEffect)) {
                ReadRef(reference: addressed.KeyFrom, into: into);
            }
        }
    }
    private static void WriteEffects(CompiledWorldEffect[] effects, List<WorldRuleAccess> into) {
        foreach (var effect in effects) {
            switch (effect.Value) {
                case TransactionEffect transaction:
                    WriteEffects(effects: transaction.Effects, into: into);
                    WriteEffects(effects: transaction.OnFailure, into: into);
                    break;
                case IStateWriteEffect write:
                    into.Add(item: new WorldRuleAccess(Row: write.Row, Key: (write.KeyFrom is null) ? write.Key : null, IsSet: (write.Write == WorldDocumentWriteKind.Set)));
                    break;
                case RemoveStateCellEffect remove:
                    into.Add(item: new WorldRuleAccess(Row: remove.Row, Key: (remove.KeyFrom is null) ? remove.Key : null, IsSet: true));
                    break;
                case GenerateEffect generate:
                    into.Add(item: new WorldRuleAccess(Row: generate.Row, Key: null, IsSet: true));
                    break;
                case PushStateEffect push:
                    into.Add(item: new WorldRuleAccess(Row: push.Row, Key: null, IsSet: true));
                    break;
                case TransformStateEffect transform:
                    foreach (var row in TransformRows(transform: transform.Transform)) {
                        into.Add(item: new WorldRuleAccess(Row: row, Key: null, IsSet: true));
                    }
                    break;
            }
        }
    }
    private static IEnumerable<string> TransformRows(WorldStateTransform transform) => transform switch {
        WorldStateTransform.Transfer transfer => [transfer.From, transfer.To],
        WorldStateTransform.SetRay ray => [ray.Row],
        WorldStateTransform.Shuffle shuffle => [shuffle.Row],
        WorldStateTransform.SortZone zone => [zone.Row],
        WorldStateTransform.SortKeyed keyed => [keyed.Row],
        WorldStateTransform.WriteSet set => [set.Row],
        WorldStateTransform.Push push => [push.Row],
        WorldStateTransform.Observe observe => [observe.Row],
        _ => [],
    };
}
