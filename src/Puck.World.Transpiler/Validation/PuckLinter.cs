using Puck.World.Transpiler.Ast;
using Puck.World.Transpiler.Diagnostics;

namespace Puck.World.Transpiler.Validation;

/// <summary>Static analysis linter for Puck authoring source files.</summary>
public static partial class PuckLinter {
    /// <summary>Lints an authored <see cref="DocumentNode"/>, reporting code quality warnings and suggestions.</summary>
    /// <param name="document">The parsed AST document.</param>
    /// <param name="diagnostics">The diagnostic bag to record linter findings into.</param>
    public static void Lint(DocumentNode document, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var declaredLets = new Dictionary<string, LetNode>(StringComparer.Ordinal);
        var declaredTemplates = new Dictionary<string, TemplateNode>(StringComparer.Ordinal);
        var referencedIdentifiers = new HashSet<string>(StringComparer.Ordinal);

        // 1. Pass 1: Collect declarations & detect shadowing
        foreach (var statement in document.Statements) {
            if (statement is LetNode letNode) {
                if (declaredLets.TryGetValue(letNode.Name, out var existing)) {
                    diagnostics.ReportWarning(
                        code: PuckDiagnosticCodes.LintDuplicateKey,
                        message: $"Constant '{letNode.Name}' shadows a previous declaration on line {existing.Line}",
                        span: letNode.Span
                    );
                } else {
                    declaredLets[letNode.Name] = letNode;
                }
            } else if (statement is TemplateNode templateNode) {
                if (declaredTemplates.TryGetValue(templateNode.Name, out var existing)) {
                    diagnostics.ReportWarning(
                        code: PuckDiagnosticCodes.LintDuplicateKey,
                        message: $"Template '{templateNode.Name}' shadows a previous declaration on line {existing.Line}",
                        span: templateNode.Span
                    );
                } else {
                    declaredTemplates[templateNode.Name] = templateNode;
                }
            }
        }

        // 2. Pass 2: Collect all referenced identifiers across statements and expressions
        foreach (var statement in document.Statements) {
            CollectReferences(statement, referencedIdentifiers, diagnostics);
        }

        // 3. Pass 3: Check for unused declarations
        foreach (var (name, node) in declaredLets) {
            if (!referencedIdentifiers.Contains(name)) {
                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintUnusedLet,
                    message: $"Constant '{name}' is declared but its value is never used",
                    span: node.Span
                );
            }
        }

        foreach (var (name, node) in declaredTemplates) {
            if (!referencedIdentifiers.Contains(name)) {
                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintUnusedLet,
                    message: $"Template '{name}' is declared but never instantiated",
                    span: node.Span
                );
            }
        }
    }

    private static void CollectReferences(SyntaxNode node, HashSet<string> references, DiagnosticBag diagnostics, bool inLet = false) {
        switch (node) {
            case IdentifierExpressionNode ident:
                references.Add(ident.Name);
                break;

            case CallExpressionNode call:
                references.Add(call.Name);
                foreach (var arg in call.Arguments) {
                    CollectReferences(arg.Value, references, diagnostics, inLet);
                }
                break;

            case ColorExpressionNode color:
                // Check if hardcoded color literal could be hoisted to a constant
                if (!inLet) {
                    diagnostics.ReportInformation(
                        code: PuckDiagnosticCodes.LintShadowedDeclaration,
                        message: $"Inline color literal '{color.Hex}' — consider declaring as a named 'let' constant",
                        span: color.Span
                    );
                }
                break;

            case LiteralExpressionNode { Value: string strVal } lit when !inLet && strVal.StartsWith('#') && (strVal.Length == 7 || strVal.Length == 9):
                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintShadowedDeclaration,
                    message: $"Inline color literal '{strVal}' — consider declaring as a named 'let' constant",
                    span: lit.Span
                );
                break;

            case BlockNode block:
                // Check host block parameters
                if (string.Equals(block.Identifier, "host", StringComparison.OrdinalIgnoreCase)) {
                    foreach (var stmt in block.Statements) {
                        if (stmt is PropertyNode prop && string.Equals(prop.Name, "targetHertz", StringComparison.OrdinalIgnoreCase)) {
                            if (prop.Value is LiteralExpressionNode { Value: long hz } && hz <= 0) {
                                diagnostics.ReportWarning(
                                    code: PuckDiagnosticCodes.LintEmptyBlock,
                                    message: $"Host targetHertz must be greater than 0, got {hz}",
                                    span: prop.Span
                                );
                            }
                        }
                    }
                }
                foreach (var stmt in block.Statements) {
                    CollectReferences(stmt, references, diagnostics, inLet: false);
                }
                break;

            case PropertyNode prop:
                CollectReferences(prop.Value, references, diagnostics, inLet: false);
                break;

            case LetNode letNode:
                CollectReferences(letNode.Value, references, diagnostics, inLet: true);
                break;

            case TemplateNode tmpl:
                foreach (var param in tmpl.Parameters) {
                    if (param.DefaultValue is not null) {
                        CollectReferences(param.DefaultValue, references, diagnostics, inLet: false);
                    }
                }
                CollectReferences(tmpl.Body, references, diagnostics, inLet: false);
                break;

            case BinaryExpressionNode bin:
                CollectReferences(bin.Left, references, diagnostics, inLet: false);
                CollectReferences(bin.Right, references, diagnostics, inLet: false);
                break;

            case ArrayExpressionNode arr:
                foreach (var elem in arr.Elements) {
                    CollectReferences(elem, references, diagnostics, inLet: false);
                }
                break;

            case ObjectExpressionNode obj:
                foreach (var p in obj.Properties) {
                    CollectReferences(p.Value, references, diagnostics, inLet: false);
                }
                break;

            case MemberAccessExpressionNode member:
                CollectReferences(member.Target, references, diagnostics);
                break;

            case RangeExpressionNode range:
                CollectReferences(range.Start, references, diagnostics);
                CollectReferences(range.End, references, diagnostics);
                break;

            case ExpressionStatementNode exprStmt:
                CollectReferences(exprStmt.Expression, references, diagnostics);
                break;
        }
    }
}
