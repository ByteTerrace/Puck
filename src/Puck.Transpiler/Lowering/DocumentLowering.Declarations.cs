using Puck.Assets.Documents;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

namespace Puck.Transpiler.Lowering;

// Row declarations (`table`, `slot`, `pile`, `grid`) whose name or cell keys are computed: the one place a computed
// name or key becomes the text a vocabulary lowers, so a declaration a loop or a template produced reads exactly as
// one written out.
public static partial class DocumentLowering {
    /// <summary>Returns a row declaration with its name settled: an interpolated name (<c>table $"hand{s}"</c>) is
    /// evaluated in <paramref name="scope"/>, and the declaration comes back carrying the text as its name and no
    /// expression. Any other statement, or a declaration whose name is written out, is returned as it is.</summary>
    /// <param name="statement">The statement as a section's expansion produced it.</param>
    /// <param name="scope">The scope its loop bindings and template arguments are live in.</param>
    /// <returns>The statement with a written-out name.</returns>
    /// <remarks>A name that evaluates to no text is refused as <see cref="PuckDiagnosticCodes.InvalidValue"/> and the
    /// declaration keeps an empty name, which the vocabulary's row-name check refuses again by name.</remarks>
    public static StatementNode ResolveDeclarationName(StatementNode statement, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(statement);
        ArgumentNullException.ThrowIfNull(scope);

        var expression = (statement switch {
            StateTableDeclarationNode table => table.NameExpression,
            StateSlotDeclarationNode slot => slot.NameExpression,
            StatePileDeclarationNode pile => pile.NameExpression,
            StateGridDeclarationNode grid => grid.NameExpression,
            _ => null,
        });

        if (expression is null) {
            return statement;
        }

        var name = ComputedText(expression: expression, scope: scope, what: "A row declaration's name");

        return Named(name: name, statement: statement);
    }
    // The declaration renamed, its computed name spent.
    private static StatementNode Named(StatementNode statement, string name) => (statement switch {
        StateTableDeclarationNode table => (table with { Name = name, NameExpression = null }),
        StateSlotDeclarationNode slot => (slot with { Name = name, NameExpression = null }),
        StatePileDeclarationNode pile => (pile with { Name = name, NameExpression = null }),
        StateGridDeclarationNode grid => (grid with { Name = name, NameExpression = null }),
        _ => statement,
    });
    /// <summary>Flattens a <c>table</c>'s or <c>grid</c>'s cell body into its cell entries, each with its key written
    /// out and paired with the scope its value and modifiers lower in: a <c>let</c> binds for the entries after it,
    /// a <c>for</c> repeats its body per element and an <c>if</c> keeps the branch its condition selects, nested to any
    /// depth, as a section's rows expand.</summary>
    /// <param name="body">The body as written.</param>
    /// <param name="scope">The declaration's scope.</param>
    /// <returns>The cell entries in order, every computed key (<c>$"…" = v</c>, <c>[expression] = v</c>) evaluated.</returns>
    /// <remarks>A computed key that evaluates to neither text nor a number is refused as
    /// <see cref="PuckDiagnosticCodes.InvalidValue"/> and its entry produces nothing.</remarks>
    public static IEnumerable<(StateCellEntryNode Cell, DocumentScope Scope)> ExpandCellBody(IReadOnlyList<StatementNode> body, DocumentScope scope) {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(scope);

        var active = scope;

        foreach (var statement in body) {
            switch (statement) {
                case LetNode binding:
                    active = BindLocalLet(binding: binding, scope: active);

                    break;
                case StateCellEntryNode { KeyExpression: null } cell:
                    yield return (cell, active);

                    break;
                case StateCellEntryNode { KeyExpression: { } computed } cell: {
                        var key = ComputedText(expression: computed, scope: active, what: "A computed cell key");

                        if (key.Length > 0) {
                            yield return ((cell with { Key = key, KeyExpression = null }), active);
                        }

                        break;
                    }
                case ForStatementNode loop:
                    foreach (var (produced, iteration) in ExpandForStatements(loop: loop, scope: active)) {
                        foreach (var expanded in ExpandCellBody(body: [produced], scope: iteration)) {
                            yield return expanded;
                        }
                    }

                    break;
                case ConditionalStatementNode branch:
                    foreach (var (produced, chosen) in ExpandConditionalStatements(branch: branch, scope: active)) {
                        foreach (var expanded in ExpandCellBody(body: [produced], scope: chosen)) {
                            yield return expanded;
                        }
                    }

                    break;
                case ErrorStatementNode:
                    break;
                default:
                    scope.Diagnostics.ReportError(
                        code: PuckDiagnosticCodes.UnrecognizedSectionStatement,
                        message: DiagnosticText.Compose(
                            fix: "Write 'key = value' entries, or a 'let', 'for' or 'if' producing them",
                            problem: "A cell body holds a statement that is no cell entry"
                        ),
                        span: statement.Span
                    );

                    break;
            }
        }
    }
    // A computed name or key: the text an interpolation or bracketed expression evaluates to, a number by the spelling a
    // document writes it in. Anything else is refused where it is written, and reads as the empty text.
    private static string ComputedText(ExpressionNode expression, DocumentScope scope, string what) {
        if (KeyText(node: LowerValue(expr: expression, scope: scope)) is { Length: > 0 } text) {
            return text;
        }

        scope.Diagnostics.ReportError(
            code: PuckDiagnosticCodes.InvalidValue,
            message: DiagnosticText.Compose(
                fix: "Compute it from text or a whole number, as $\"pile{z}\" or [z + 1] does",
                problem: $"{what} evaluates to no text"
            ),
            span: expression.Span
        );

        return string.Empty;
    }
}
