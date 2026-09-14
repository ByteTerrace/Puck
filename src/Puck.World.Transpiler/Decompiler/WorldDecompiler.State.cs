using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Puck.World.Transpiler.Decompiler;

// `state { world { ... } }` — prints each row as `table`/`slot` sugar when the whole row is representable without
// loss, and falls back to `row { }` (the explicit form) otherwise. See src/Puck.World.Transpiler/README.md's
// syntax-contract section for the exact eligibility rule this mirrors from the emitter.
public static partial class WorldDecompiler {
    private static readonly HashSet<string> TableSugarRowKeys = new(comparer: StringComparer.Ordinal) { "name", "kind", "min", "max", "capacity", "overflow", "cells", "advance", "domain" };
    private static readonly HashSet<string> SlotSugarRowKeys = new(comparer: StringComparer.Ordinal) { "name", "kind", "min", "max", "overflow", "value", "advance" };
    private static readonly HashSet<string> CellSugarKeys = new(comparer: StringComparer.Ordinal) { "key", "value", "advance", "behavior" };

    private static void DecompileStateBlock(StringBuilder sb, JsonObject state, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}state {{"
        );

        var first = true;

        foreach (var (k, v) in state) {
            if (!first) {
                sb.AppendLine();
            }
            first = false;

            if (
                string.Equals(
                a: k,
                b: "world",
                comparisonType: StringComparison.OrdinalIgnoreCase
            ) &&
                (v is JsonArray worldArr)
            ) {
                DecompileStateWorldBlock(
                    indentLevel: (indentLevel + 1),
                    sb: sb,
                    world: worldArr
                );
            } else {
                EmitField(
                    indentLevel: (indentLevel + 1),
                    key: k,
                    sb: sb,
                    value: v
                );
            }
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static void DecompileStateWorldBlock(StringBuilder sb, JsonArray world, int indentLevel) {
        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}world {{"
        );

        var first = true;

        foreach (var rowNode in world) {
            if (rowNode is not JsonObject rowObj) {
                continue;
            }
            if (!first) {
                sb.AppendLine();
            }
            first = false;

            DecompileStateRow(
                indentLevel: (indentLevel + 1),
                row: rowObj,
                sb: sb
            );
        }

        sb.AppendLine(
            CultureInfo.InvariantCulture,
            $"{indent}}}"
        );
    }
    private static void DecompileStateRow(StringBuilder sb, JsonObject row, int indentLevel) {
        if (!CanSugarStateRow(
            isTable: out var isTable,
            row: row
        )) {
            DecompileNamedBlock(
                sb,
                "row",
                null,
                row,
                indentLevel
            );

            return;
        }

        var indent = new string(
            c: ' ',
            count: (indentLevel * 4)
        );
        var name = (row["name"]!.ToString());
        var kind = (row["kind"]!.ToString());

        if (isTable) {
            var header = new StringBuilder(value: $"{indent}table {name} : {kind}");

            AppendStateRowModifiers(
                header: header,
                includeCapacity: true,
                kind: kind,
                row: row
            );

            var cells = ((row["cells"] as JsonArray) ?? []);

            if (cells.Count == 0) {
                sb.AppendLine(
                    CultureInfo.InvariantCulture,
                    $"{header} {{ }}"
                );

                return;
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{header} {{"
            );

            var cellIndent = new string(
                c: ' ',
                count: ((indentLevel + 1) * 4)
            );

            foreach (var cellNode in cells) {
                var cellObj = ((JsonObject)cellNode!);
                var key = (cellObj["key"]!.ToString());
                var line = new StringBuilder(value: $"{cellIndent}{key} = {FormatStateScalarLiteral(
                    kind: kind,
                    node: cellObj["value"]
                )}");

                if (cellObj["advance"] is JsonObject cellAdvance) {
                    line.Append(value: $" advance(perSecond: {FormatStateRate(advance: cellAdvance)})");
                }
                if (
                    (cellObj["behavior"] is JsonValue behaviorVal) &&
                    behaviorVal.TryGetValue<string>(value: out var behavior) &&
                    (behavior == "none")
                ) {
                    line.Append(value: " behavior(none)");
                }

                sb.AppendLine(value: line.ToString());
            }

            sb.AppendLine(
                CultureInfo.InvariantCulture,
                $"{indent}}}"
            );
        } else {
            var header = new StringBuilder(value: $"{indent}slot {name} : {kind}");

            if (row["value"] is { } valueNode) {
                header.Append(value: $" = {FormatStateScalarLiteral(
                    kind: kind,
                    node: valueNode
                )}");
            }

            AppendStateRowModifiers(
                header: header,
                includeCapacity: false,
                kind: kind,
                row: row
            );

            sb.AppendLine(value: header.ToString());
        }
    }
    private static void AppendStateRowModifiers(StringBuilder header, JsonObject row, string kind, bool includeCapacity) {
        if (
            includeCapacity &&
            (row["capacity"] is JsonValue capVal) &&
            capVal.TryGetValue<int>(value: out var capacity)
        ) {
            header.Append(value: $" capacity({capacity})");
        }

        if (
            row.ContainsKey(propertyName: "min") ||
            row.ContainsKey(propertyName: "max") ||
            row.ContainsKey(propertyName: "overflow")
        ) {
            var args = new List<string>();

            if (row["min"] is { } minNode) {
                args.Add(item: $"minimum: {FormatStateScalarLiteral(
                    kind: kind,
                    node: minNode
                )}");
            }
            if (row["max"] is { } maxNode) {
                args.Add(item: $"maximum: {FormatStateScalarLiteral(
                    kind: kind,
                    node: maxNode
                )}");
            }
            if (
                (row["overflow"] is JsonValue overflowVal) &&
                overflowVal.TryGetValue<string>(value: out var overflow)
            ) {
                args.Add(item: $"overflow: {overflow}");
            }

            header.Append(value: $" bounds({string.Join(
                separator: ", ",
                values: args
            )})");
        }

        if (row["advance"] is JsonObject rowAdvance) {
            header.Append(value: $" advance(perSecond: {FormatStateRate(advance: rowAdvance)})");
        }
    }
    private static string FormatStateScalarLiteral(JsonNode? node, string kind) {
        if (node is null) {
            return "null";
        }

        switch (kind) {
            case "Fixed":
                // Already a decimal-text JSON string (see the emitter's LowerStateFixedValue) — printed verbatim.
                return node.ToString();
            case "Text":
                return $"\"{EscapeString(s: node.ToString())}\"";
            case "Bool":
                return (((node is JsonValue boolVal) && boolVal.TryGetValue<bool>(value: out var b))
                    ? (b ? "true" : "false")
                    : node.ToString()
                );
            default:
                return (((node is JsonValue intVal) && intVal.TryGetValue<long>(value: out var l))
                    ? l.ToString(provider: CultureInfo.InvariantCulture)
                    : node.ToString()
                );
        }
    }
    private static string FormatStateRate(JsonObject advance) {
        var numerator = (((advance["perSecondNumerator"] is JsonValue nv) && nv.TryGetValue<long>(value: out var n))
            ? n
            : 0L
        );
        var denominator = (((advance["perSecondDenominator"] is JsonValue dv) && dv.TryGetValue<long>(value: out var d))
            ? d
            : 1L
        );

        if (denominator == 1L) {
            return numerator.ToString(provider: CultureInfo.InvariantCulture);
        }

        // Eligibility (CanSugarAdvance) already proved this reduces to a terminating decimal, so exact `decimal`
        // division reproduces the same fraction `TryReduceDecimalRate` would parse back from the printed text.
        return (((decimal)numerator) / denominator).ToString(provider: CultureInfo.InvariantCulture);
    }
    // Whether `denominator` (already reduced against `numerator`'s magnitude) is a power of 2 and/or 5 — the exact
    // condition under which the rate has a finite decimal expansion the DSL's own literal grammar can spell.
    private static bool CanExpressRateAsLiteral(long numerator, long denominator) {
        if (denominator <= 0L) {
            return false;
        }
        if (denominator == 1L) {
            return true;
        }

        var gcd = GreatestCommonDivisor(
            a: Math.Abs(value: numerator),
            b: denominator
        );
        var reduced = (denominator / Math.Max(
            val1: gcd,
            val2: 1L
        ));

        while ((reduced % 2L) == 0L) {
            reduced /= 2L;
        }
        while ((reduced % 5L) == 0L) {
            reduced /= 5L;
        }

        return (reduced == 1L);
    }
    private static long GreatestCommonDivisor(long a, long b) {
        while (b != 0L) {
            (a, b) = (b, (a % b));
        }

        return Math.Abs(value: a);
    }
    private static bool CanSugarAdvance(JsonObject advance) {
        if (advance.Count != 2) {
            return false;
        }
        if (
            (advance["perSecondNumerator"] is not JsonValue nv) ||
            !nv.TryGetValue<long>(value: out var numerator)
        ) {
            return false;
        }
        if (
            (advance["perSecondDenominator"] is not JsonValue dv) ||
            !dv.TryGetValue<long>(value: out var denominator)
        ) {
            return false;
        }

        return CanExpressRateAsLiteral(
            denominator: denominator,
            numerator: numerator
        );
    }
    // The whole row must be representable without loss — every member spellable as a modifier, every cell
    // spellable as `key = value [modifier]*`, and every `advance` rate spellable as one literal.
    private static bool CanSugarStateRow(JsonObject row, out bool isTable) {
        isTable = false;

        if (
            (row["name"] is not JsonValue nameVal) ||
            !nameVal.TryGetValue<string>(value: out _)
        ) {
            return false;
        }
        if (
            (row["kind"] is not JsonValue kindVal) ||
            !kindVal.TryGetValue<string>(value: out var kind) ||
            (kind is not ("Int" or "Fixed" or "Bool" or "Text"))
        ) {
            return false;
        }

        var hasCapacity = row.ContainsKey(propertyName: "capacity");
        var hasCells = (row["cells"] is JsonArray);
        var hasValue = row.ContainsKey(propertyName: "value");

        if (hasValue && hasCells) {
            return false;
        }

        var domainObj = (row["domain"] as JsonObject);
        var domainIsKeys = (
            (domainObj is not null) &&
            (domainObj.Count == 1) &&
            (domainObj["$type"] is JsonValue domainType) &&
            domainType.TryGetValue<string>(value: out var domainTypeName) &&
            (domainTypeName == "keys")
        );

        if (row.ContainsKey(propertyName: "domain") && !domainIsKeys) {
            return false;
        }

        isTable = (hasCapacity || hasCells || domainIsKeys);

        var allowed = (isTable
            ? TableSugarRowKeys
            : SlotSugarRowKeys
        );

        foreach (var (k, _) in row) {
            if (!allowed.Contains(item: k)) {
                return false;
            }
        }

        if (
            (row["advance"] is JsonObject rowAdvance) &&
            !CanSugarAdvance(advance: rowAdvance)
        ) {
            return false;
        }

        if (hasCells) {
            foreach (var cellNode in ((JsonArray)row["cells"]!)) {
                if (cellNode is not JsonObject cellObj) {
                    return false;
                }
                if (
                    (cellObj["key"] is not JsonValue cellKeyVal) ||
                    !cellKeyVal.TryGetValue<string>(value: out _)
                ) {
                    return false;
                }
                if (!cellObj.ContainsKey(propertyName: "value")) {
                    return false;
                }

                foreach (var (ck, _) in cellObj) {
                    if (!CellSugarKeys.Contains(item: ck)) {
                        return false;
                    }
                }

                if (
                    (cellObj["advance"] is JsonObject cellAdvance) &&
                    !CanSugarAdvance(advance: cellAdvance)
                ) {
                    return false;
                }
            }
        }

        return true;
    }
}
