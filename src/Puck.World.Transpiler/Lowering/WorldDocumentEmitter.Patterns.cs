using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.World.Transpiler.Lowering;

// The `pattern` declaration lowers to one `patterns` row. The pattern node is written through the document's own
// serializer rather than a second hand-rolled tree, so the discriminators can never drift from the model, and the
// row is compiled here so an author reads the state-budget refusal against the line that declared it rather than
// against a validation run.
public static partial class WorldDocumentEmitter {
    private static readonly JsonTypeInfo<PatternNode> PatternNodeTypeInfo =
        ((JsonTypeInfo<PatternNode>)WorldJsonContext.Default.Options.GetTypeInfo(type: typeof(PatternNode)));

    private static void LowerPatternDeclaration(PatternDeclarationNode pattern, JsonObject parent, DocumentScope scope) {
        if (parent["patterns"] is not JsonArray patterns) {
            patterns = [];
            parent["patterns"] = patterns;
        }

        var row = new JsonObject {
            ["name"] = pattern.Name,
            ["kind"] = pattern.Kind,
        };
        var symbols = new JsonArray();

        foreach (var symbol in pattern.Symbols) {
            symbols.AppendNode(item: new JsonObject {
                ["name"] = symbol.Name,
                ["min"] = symbol.Minimum,
                ["max"] = symbol.Maximum,
            });
        }

        row["symbols"] = symbols;
        row["pattern"] = JsonSerializer.SerializeToNode(
            jsonTypeInfo: PatternNodeTypeInfo,
            value: pattern.Match
        );

        if (pattern.Attribute is { } attribute) {
            row["attribute"] = attribute;
        }
        if (pattern.Value is { } value) {
            row["value"] = ResolveOperandConstants(
                scope: scope,
                text: ResolveEnumsInText(
                    scope: scope,
                    text: value
                )
            );
        }
        if (pattern.MaximumStates is { } budget) {
            row["maxStates"] = budget;
        }

        scope.SourceMap?.Register(
            jsonPointer: $"{scope.CurrentPointer}/patterns/{patterns.Count}",
            span: pattern.Span
        );
        patterns.AppendNode(item: row);
        CheckPatternBudget(
            pattern: pattern,
            scope: scope
        );
    }
    private static void CheckPatternBudget(PatternDeclarationNode pattern, DocumentScope scope) {
        var budget = (pattern.MaximumStates ?? PatternCapacity.DefaultStates);

        if (!Enum.TryParse(
            ignoreCase: false,
            result: out CellKind kind,
            value: pattern.Kind
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PatternExpression,
                message: $"pattern '{pattern.Name}' reads its word as kind '{pattern.Kind}', which is not a cell kind",
                span: pattern.Span
            );

            return;
        }
        if (budget is < 1 or > PatternCapacity.MaxStates) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PatternStateBudget,
                message: $"pattern '{pattern.Name}' budgets {budget} machine states; 1..{PatternCapacity.MaxStates} are admitted",
                span: pattern.Span
            );

            return;
        }

        var declared = new List<PatternSymbol>();

        foreach (var symbol in pattern.Symbols) {
            if (!CellName.TryParse(
                candidate: symbol.Name,
                name: out var symbolName,
                reason: out var reason
            )) {
                scope.Diagnostics.ReportError(
                    code: PuckDiagnosticCodes.PatternExpression,
                    message: $"pattern '{pattern.Name}' symbol '{symbol.Name}' {reason}",
                    span: pattern.Span
                );

                return;
            }

            declared.Add(item: new PatternSymbol(
                Max: symbol.Maximum,
                Min: symbol.Minimum,
                Name: symbolName
            ));
        }

        if (!CellName.TryParse(
            candidate: pattern.Name,
            name: out var rowName,
            reason: out var nameReason
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PatternExpression,
                message: $"pattern name '{pattern.Name}' {nameReason}",
                span: pattern.Span
            );

            return;
        }

        // Compiled against the representation ceiling rather than the authored budget, so a machine that fits the
        // ceiling but not the budget is reported as the budget it overran and everything else as what it is.
        if (!CompiledPattern.TryCompile(
            compiled: out var compiled,
            reason: out var refusal,
            row: new PatternRow(
                Kind: kind,
                MaxStates: PatternCapacity.MaxStates,
                Name: rowName,
                Pattern: pattern.Match,
                Symbols: declared
            )
        )) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PatternExpression,
                message: refusal,
                span: pattern.Span
            );

            return;
        }
        if (compiled!.StateCount > budget) {
            scope.Diagnostics.ReportError(
                code: PuckDiagnosticCodes.PatternStateBudget,
                message: string.Create(
                    provider: CultureInfo.InvariantCulture,
                    handler: $"pattern '{pattern.Name}' needs {compiled.StateCount} machine states, past the {budget} it budgets"
                ),
                span: pattern.Span
            );
        }
    }
}
