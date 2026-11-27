using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// A `table`, `slot` or `grid` declaration spells no cell kind; the kind is read from what the declaration spells.
// A `space(...)` modifier or an `embed`/`vector` call is Vector. Otherwise every value decides together: a string is
// Text, `true`/`false` is Bool, and among numbers a fraction or a unit makes the row Fixed where whole numbers alone
// leave it Int. A name bound by `let` reads as the value it is bound to. Fractional `bounds` or `advance` arguments
// also widen a numeric row to Fixed; with no signal at all the row is Int, the default of the document member. A
public static partial class WorldDocumentEmitter {
    // A table's cells decide its kind wherever they came from: each written cell in the scope its loop left it, and
    // each value of its `= value` initializer as the initializer evaluated it.
    private static string InferTableKind(StateTableDeclarationNode table, IReadOnlyList<(StateCellEntryNode Cell, DocumentScope Scope)> written, IReadOnlyList<(string Key, JsonNode? Value)> initial, DocumentScope scope) {
        if (AdmittedRowKinds.Contains(item: table.Kind)) {
            return table.Kind;
        }

        return InferRowKind(
            kinds: [
                .. written.Select(selector: static cell => ValueKind(scope: cell.Scope, value: cell.Cell.Value)),
                .. initial.Select(selector: static cell => JsonValueKind(value: cell.Value)),
            ],
            modifiers: table.Modifiers,
            scope: scope
        );
    }
    private static string InferSlotKind(StateSlotDeclarationNode slot, DocumentScope scope) => InferRowKind(
        kinds: [ValueKind(scope: scope, value: slot.Value)],
        modifiers: slot.Modifiers,
        scope: scope
    );
    private static string InferGridKind(StateGridDeclarationNode grid, IReadOnlyList<(StateCellEntryNode Cell, DocumentScope Scope)> written, DocumentScope scope) => InferRowKind(
        kinds: [.. written.Select(selector: static cell => ValueKind(scope: cell.Scope, value: cell.Cell.Value))],
        modifiers: grid.Modifiers,
        scope: scope
    );
    private static string InferRowKind(IReadOnlyList<StateModifierNode> modifiers, IEnumerable<string?> kinds, DocumentScope scope) {
        if (modifiers.Any(predicate: static modifier => string.Equals(a: modifier.Name, b: "space", comparisonType: StringComparison.OrdinalIgnoreCase))) {
            return "Vector";
        }

        string? decided = null;

        foreach (var kind in kinds) {

            // A whole number sits in a Fixed row as well as an Int one, so a fraction anywhere widens the row. Any
            // other disagreement keeps the first kind, and the cell that disagrees is refused where it is lowered.
            if ((decided is null) || ((decided == "Int") && (kind == "Fixed"))) {
                decided = (kind ?? decided);
            }
        }

        string? modifierKind = null;

        foreach (var modifier in modifiers) {
            if (modifier.Name is not ("bounds" or "advance")) {
                continue;
            }

            foreach (var argument in modifier.Arguments) {
                // A `bounds` range carries its two ends as one argument, so each end is read on its own.
                ExpressionNode?[] ends = ((argument.Value is RangeExpressionNode range)
                    ? [range.Start, range.End]
                    : [argument.Value]);

                if (ends.Any(predicate: end => (ValueKind(scope: scope, value: end) == "Fixed"))) {
                    modifierKind = "Fixed";
                }
            }
        }

        if ((decided == "Int") && (modifierKind == "Fixed")) {
            return "Fixed";
        }

        return (decided ?? (modifierKind ?? "Int"));
    }
    // Reads the kind of a literal or resolved compile-time value. An absent value or an unbound row name gives no
    // signal; vector constructors retain their kind before their specialized lowering runs.
    private static string? ValueKind(ExpressionNode? value, DocumentScope scope) {
        switch (value) {
            case null:
                return null;
            case CallExpressionNode { Name: "embed" or "vector" }:
                return "Vector";
            case LiteralExpressionNode { Unit: not null }:
                return "Fixed";
            case LiteralExpressionNode literal:
                return (literal.Value switch {
                    bool => "Bool",
                    string => "Text",
                    null => null,
                    // `RawText` holds the authored digits only for a fractional or exponent literal, so its presence
                    // is the fraction signal; the parsed CLR type is not.
                    _ => ((literal.RawText is not null) ? "Fixed" : "Int"),
                });
            case IdentifierExpressionNode identifier:
                return (scope.TryLowerBinding(
                    fieldKey: null,
                    name: identifier.Name,
                    value: out _
                )
                    ? LoweredValueKind(expr: value, scope: scope)
                    : null
                );
            case MemberAccessExpressionNode { Target: IdentifierExpressionNode } member:
                return (scope.TryLowerBinding(
                    fieldKey: null,
                    name: QualifiedName.From(expression: member)!.ToString(),
                    value: out _
                )
                    ? LoweredValueKind(expr: value, scope: scope)
                    : null
                );
            default:
                return LoweredValueKind(expr: value, scope: scope);
        }
    }
    private static string? LoweredValueKind(ExpressionNode expr, DocumentScope scope) => JsonValueKind(value: DocumentLowering.LowerValue(
        expr: expr,
        scope: scope
    ));
    private static string? JsonValueKind(JsonNode? value) => value switch {
        JsonValue bound when bound.TryGetValue<bool>(value: out _) => "Bool",
        JsonValue bound when bound.TryGetValue<string>(value: out _) => "Text",
        JsonValue bound when bound.TryGetValue<decimal>(value: out var number) => ((decimal.Truncate(d: number) != number) ? "Fixed" : "Int"),
        JsonValue bound when bound.TryGetValue<double>(value: out var real) => ((Math.Truncate(d: real) != real) ? "Fixed" : "Int"),
        JsonValue bound when bound.TryGetValue<long>(value: out _) => "Int",
        JsonValue bound when bound.TryGetValue<ulong>(value: out _) => "Int",
        _ => null,
    };
}
