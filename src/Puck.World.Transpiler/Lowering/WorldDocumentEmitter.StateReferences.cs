using System.Text.Json.Nodes;
using Puck.Assets.Documents;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

public static partial class WorldDocumentEmitter {
    private static void ValidateStateCrossReferences(List<PendingStateReference> pending, JsonArray worldArr, DocumentScope scope) {
        if (pending.Count == 0) {
            return;
        }

        var byName = new Dictionary<string, JsonObject>(comparer: StringComparer.Ordinal);

        foreach (var rowNode in worldArr) {
            if (
                (rowNode is JsonObject rowObj) &&
                (rowObj["name"] is JsonValue nameVal) &&
                nameVal.TryGetValue<string>(value: out var name)
            ) {
                byName[name] = rowObj;
            }
        }

        foreach (var reference in pending) {
            switch (reference.Kind) {
                case PendingStateReferenceKind.PileTokenDomain:
                    ValidatePileTokenDomainReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
                case PendingStateReferenceKind.GridPositions:
                    ValidateGridPositionsReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
                case PendingStateReferenceKind.GridInverse:
                    ValidateGridInverseReference(
                        byName: byName,
                        reference: reference,
                        scope: scope
                    );

                    break;
            }
        }
    }
    private static bool IsRowKindInt(JsonObject row) => ((row["kind"] is JsonValue kindVal) && kindVal.TryGetValue<string>(value: out var kind) && (kind == "Int"));
    private static bool IsPlainTokenDomainRow(JsonObject row) {
        if (row["domain"] is JsonObject domainObj) {
            return (
                (domainObj["$type"] is JsonValue typeVal) &&
                typeVal.TryGetValue<string>(value: out var type) &&
                (type == "keys")
            );
        }

        // Undeclared domain infers Keys exactly when the row carries a capacity or more than one cell, or one
        // cell under an author-chosen key — StateRow.InferDomain's own rule, restated over the row's raw JSON.
        var hasCapacity = row.ContainsKey(propertyName: "capacity");
        var cells = (row["cells"] as JsonArray);

        if (hasCapacity || (cells is { Count: > 1 })) {
            return true;
        }

        return ((cells is { Count: 1 }) && (((cells[0] as JsonObject)?["key"]?.ToString()) != "$value"));
    }
    private static void ValidatePileTokenDomainReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var domainRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: DiagnosticText.Compose(fix: $"Declare a token-domain row named '{reference.RowName}' in 'state.world', or fix the name", problem: $"The pile '{reference.OwnRowName}' names the row '{reference.RowName}', which is not declared"),
                span: reference.Span
            );

            return;
        }
        if (!IsPlainTokenDomainRow(row: domainRow)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: DiagnosticText.Compose(fix: "Name a plain row whose cells are the tokens the pile holds", problem: $"The pile '{reference.OwnRowName}' names '{reference.RowName}', which is not a plain token-domain row"),
                span: reference.Span
            );

            return;
        }
        if (reference.Capacity is not { } capacity) {
            return;
        }

        // A table's declared cells are only its initial population — more keys are legal up to `capacity`, or
        // unbounded (StateCapacity.MaxCellsPerRow) when no capacity is declared — so only a declared capacity is a
        // real ceiling here; the row's current cell count is never one, and checking against it would refuse a
        // capacity this table is free to grow into.
        var domainCount = (((domainRow["capacity"] is JsonValue capVal) && capVal.TryGetValue<int>(value: out var domainCapacity))
            ? domainCapacity
            : ((int?)null)
        );

        if ((domainCount is { } count) && (capacity > count)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationCapacityExceedsDomain,
                message: DiagnosticText.Compose(fix: $"Lower the capacity to {count} or fewer, or add tokens to '{reference.RowName}'", problem: $"The pile '{reference.OwnRowName}' has capacity {capacity}, and its token domain '{reference.RowName}' provides only {count} tokens"),
                span: reference.Span
            );
        }
    }
    private static void ValidateGridPositionsReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var positionsRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: DiagnosticText.Compose(fix: $"Declare an integer row named '{reference.RowName}' in 'state.world', or fix the name", problem: $"The 'positions' of the grid '{reference.OwnRowName}' names the row '{reference.RowName}', which is not declared"),
                span: reference.Span
            );

            return;
        }

        var domainType = ((positionsRow["domain"] as JsonObject)?["$type"] as JsonValue);

        if (
            !IsRowKindInt(row: positionsRow) ||
            (domainType is null) ||
            !domainType.TryGetValue<string>(value: out var domainTypeName) ||
            (domainTypeName != "keysOf")
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: DiagnosticText.Compose(fix: "Name an Int row declared with 'keysOf', so each token has an integer position", problem: $"The 'positions' of the grid '{reference.OwnRowName}' names '{reference.RowName}', which is not an integer keysOf row"),
                span: reference.Span
            );

            return;
        }

        positionsRow["valuesFrom"] = reference.OwnRowName;
    }
    private static void ValidateGridInverseReference(PendingStateReference reference, IReadOnlyDictionary<string, JsonObject> byName, DocumentScope scope) {
        if (!byName.TryGetValue(
            key: reference.RowName,
            value: out var tokensRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: DiagnosticText.Compose(fix: $"Declare a row named '{reference.RowName}' in 'state.world', or fix the name", problem: $"The 'inverse.tokens' of the grid '{reference.OwnRowName}' names the row '{reference.RowName}', which is not declared"),
                span: reference.Span
            );

            return;
        }
        if (!byName.TryGetValue(
            key: reference.SecondaryRowName!,
            value: out var codesRow
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationUnknownReference,
                message: DiagnosticText.Compose(fix: $"Declare a row named '{reference.SecondaryRowName}' in 'state.world', or fix the name", problem: $"The 'inverse.codes' of the grid '{reference.OwnRowName}' names the row '{reference.SecondaryRowName}', which is not declared"),
                span: reference.Span
            );

            return;
        }
        if (
            !IsRowKindInt(row: tokensRow) ||
            !IsPlainTokenDomainRow(row: tokensRow)
        ) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: DiagnosticText.Compose(fix: "Name an Int row that has keyed cells", problem: $"The 'inverse.tokens' row '{reference.RowName}' of the grid '{reference.OwnRowName}' is not a keyed integer row"),
                span: reference.Span
            );
        }
        if (!IsRowKindInt(row: codesRow)) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: DiagnosticText.Compose(fix: "Name an Int row", problem: $"The 'inverse.codes' row '{reference.SecondaryRowName}' of the grid '{reference.OwnRowName}' is not an integer row"),
                span: reference.Span
            );
        }

        var tokenCells = ((tokensRow["cells"] as JsonArray) ?? []);
        var codeCells = ((codesRow["cells"] as JsonArray) ?? []);
        var sameShape = (tokenCells.Count == codeCells.Count);

        for (var index = 0; (sameShape && (index < tokenCells.Count)); index++) {
            sameShape = ((((tokenCells[index] as JsonObject)?["key"])?.ToString()) == (((codeCells[index] as JsonObject)?["key"])?.ToString()));
        }
        if (!sameShape) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.StateDeclarationReferenceShapeMismatch,
                message: DiagnosticText.Compose(fix: "Give both rows the same keys in the same order", problem: $"The 'inverse.codes' row '{reference.SecondaryRowName}' of the grid '{reference.OwnRowName}' has different keys from 'inverse.tokens' '{reference.RowName}'"),
                span: reference.Span
            );
        }
    }
}
