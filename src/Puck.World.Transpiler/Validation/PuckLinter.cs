using Puck.State;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Rewriting;

namespace Puck.World.Transpiler.Validation;

/// <summary>Static analysis linter for Puck authoring source files.</summary>
public static partial class PuckLinter {
    /// <summary>Lints an authored <see cref="DocumentNode"/>, reporting code quality warnings and suggestions.</summary>
    /// <param name="document">The parsed AST document.</param>
    /// <param name="diagnostics">The diagnostic bag to record linter findings into.</param>
    public static void Lint(DocumentNode document, DiagnosticBag diagnostics) {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);

        var declaredLets = new Dictionary<string, LetNode>(comparer: StringComparer.Ordinal);
        var declaredTemplates = new Dictionary<string, TemplateNode>(comparer: StringComparer.Ordinal);
        var referencedIdentifiers = new HashSet<string>(comparer: StringComparer.Ordinal);

        // 1. Pass 1: Collect declarations & detect shadowing
        foreach (var statement in document.Statements) {
            if (statement is LetNode letNode) {
                if (declaredLets.TryGetValue(
                    key: letNode.Name,
                    value: out var existing
                )) {
                    diagnostics.ReportWarning(
                        code: PuckDiagnosticCodes.LintDuplicateKey,
                        message: $"Constant '{letNode.Name}' shadows a previous declaration on line {existing.Line}",
                        span: letNode.Span
                    );
                } else {
                    declaredLets[letNode.Name] = letNode;
                }
            } else if (statement is TemplateNode templateNode) {
                if (declaredTemplates.TryGetValue(
                    key: templateNode.Name,
                    value: out var existing
                )) {
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
            CollectReferences(
                statement,
                referencedIdentifiers,
                diagnostics
            );
        }

        // 3. Pass 3: Check for unused declarations. A declaration an import brought in is reported against the
        // source that declares it. A statement the parser could not read may hold the use, so nothing is reported
        // unused until the source parses whole.
        if (diagnostics.HasErrors && UnreadStatementFinder.Holds(document: document)) {
            return;
        }

        foreach (var (name, node) in declaredLets) {
            if (!referencedIdentifiers.Contains(item: name)) {
                var start = diagnostics.Count;

                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintUnusedLet,
                    message: $"Constant '{name}' is declared but its value is never used",
                    span: node.Span
                );
                diagnostics.AttributeFrom(sourcePath: node.DefinitionPath, startIndex: start);
            }
        }

        foreach (var (name, node) in declaredTemplates) {
            if (!referencedIdentifiers.Contains(item: name)) {
                var start = diagnostics.Count;

                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintUnusedLet,
                    message: $"Template '{name}' is declared but never instantiated",
                    span: node.Span
                );
                diagnostics.AttributeFrom(sourcePath: node.DefinitionPath, startIndex: start);
            }
        }
    }

    // Every node is walked through SyntaxWalk.Children; the arms below are only the nodes that name something, report
    // something, or read a key as text. A colour literal written directly into a `let`'s value — through calls,
    // indexing, lambdas and interpolation, but not inside an array, object or arithmetic — is the constant it names,
    // so it is not reported.
    private static void CollectReferences(SyntaxNode node, HashSet<string> references, DiagnosticBag diagnostics, bool inLet = false) {
        var findings = diagnostics.Count;

        switch (node) {
            case IdentifierExpressionNode ident:
                references.Add(item: ident.Name);
                break;

            case CallExpressionNode call:
                references.Add(item: call.Name);
                if (QualifiedName.Parse(text: call.Name) is { IsQualified: true, Head.Length: > 0 } qualified) {
                    references.Add(item: qualified.Head);
                }
                if (string.Equals(a: call.Name, b: "mix", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    LintVectorMix(call: call, diagnostics: diagnostics);
                }
                break;

            case OperandExpressionNode operand:
                if (operand.Syntax is { } syntax) { CollectOperandReferences(syntax, operand.Form, references); }
                break;

            // A key is read as its text, which already holds every identifier an atom inside it names.
            case RowRefNode target:
                CollectKeyReferences(references: references, target: target);
                return;

            case ExportNode export:
                references.UnionWith(other: export.Names);
                break;

            // A test's given values and expectations read the same constants the document's own rows do, and a world
            // block names a world the composition declares — each is a use of what it names.
            case TestDeclarationNode test:
                foreach (var (world, _) in (((IEnumerable<(string? World, TestGivenNode Cell)>?)test.Given?.Lines) ?? [])) {
                    if (world is { } named) { references.Add(item: named); }
                }
                foreach (var (world, _) in (((IEnumerable<(string? World, TestStepNode Step)>?)test.When?.Lines) ?? [])) {
                    if (world is { } named) { references.Add(item: named); }
                }
                foreach (var (world, _) in (((IEnumerable<(string? World, TestExpectationNode Expectation)>?)test.Expect?.Lines) ?? [])) {
                    if (world is { } named) { references.Add(item: named); }
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

            case LiteralExpressionNode { Value: string strVal } lit when (!inLet && strVal.StartsWith(value: '#') && ((strVal.Length == 7) || (strVal.Length == 9))):
                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintShadowedDeclaration,
                    message: $"Inline color literal '{strVal}' — consider declaring as a named 'let' constant",
                    span: lit.Span
                );
                break;
        }

        var childInLet = ((node is LetNode) || (inLet && (node is CallExpressionNode or ArgumentNode or IndexExpressionNode or LambdaExpressionNode or OperandExpressionNode or InterpolatedStringNode)));

        foreach (var child in SyntaxWalk.Children(node: node)) {
            CollectReferences(
                diagnostics: diagnostics,
                inLet: childInLet,
                node: child,
                references: references
            );
        }
        // A template an import brought in is reported against the source that declares it.
        if (node is TemplateNode template) {
            diagnostics.AttributeFrom(sourcePath: template.DefinitionPath, startIndex: findings);
        }
    }
    // An assignment target's key is held as source text, so every identifier in it is counted as a use; a key naming
    // a row or a local only keeps a same-named constant from being reported.
    private static void CollectKeyReferences(RowRefNode target, HashSet<string> references) {
        if (target.Key is not { } key) {
            return;
        }

        var start = -1;

        for (var index = 0; (index <= key.Length); index++) {
            var identifier = ((index < key.Length) && IdentifierSpelling.IsPart(character: key[index]));

            if (identifier && (start < 0)) {
                start = index;
            } else if (!identifier && (start >= 0)) {
                if (!char.IsDigit(c: key[start])) {
                    _ = references.Add(item: key[start..index]);
                }
                start = -1;
            }
        }
    }
    private static void LintVectorMix(CallExpressionNode call, DiagnosticBag diagnostics) {
        ExpressionNode? termsExpr = null;

        foreach (var arg in call.Arguments) {
            if (string.Equals(a: arg.Name, b: "terms", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                termsExpr = arg.Value;
                break;
            }
        }

        if ((termsExpr is null) && (call.Arguments.Count > 1)) {
            termsExpr = call.Arguments[1].Value;
        } else if ((termsExpr is null) && (call.Arguments.Count == 1) && (call.Arguments[0].Value is ArrayExpressionNode)) {
            termsExpr = call.Arguments[0].Value;
        }

        if (termsExpr is not ArrayExpressionNode arr) {
            return;
        }

        var weights = new List<(long Weight, SourceSpan Span)>();

        foreach (var elem in arr.Elements) {
            if (elem is ObjectExpressionNode obj) {
                foreach (var prop in obj.Properties) {
                    if (string.Equals(a: prop.Name, b: "weight", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                        if (prop.Value is LiteralExpressionNode { Value: long wVal }) {
                            weights.Add(item: (wVal, prop.Span));
                        } else if (prop.Value is UnaryExpressionNode { Operator: "-", Operand: LiteralExpressionNode { Value: long posW } }) {
                            weights.Add(item: (-posW, prop.Span));
                        }
                    }
                }
            }
        }

        if (weights.Count == 0) {
            return;
        }

        var sumAbs = 0L;

        foreach (var (weight, _) in weights) {
            sumAbs += Math.Abs(value: weight);
        }

        if (sumAbs <= 0) {
            return;
        }

        foreach (var (weight, span) in weights) {
            var absW = Math.Abs(value: weight);

            if ((64L * absW) < sumAbs) {
                diagnostics.ReportWarning(
                    code: PuckDiagnosticCodes.VectorMixStall,
                    message: $"Vector mix term weight {weight} has relative share below 1/64; consider mean over a history table as the alternative.",
                    span: span
                );
            }
        }
    }

    private sealed class UnreadStatementFinder : PuckSyntaxRewriter {
        private bool m_found;

        public static bool Holds(DocumentNode document) {
            var finder = new UnreadStatementFinder();

            _ = finder.Rewrite(document: document);

            return finder.m_found;
        }

        protected override StatementNode? RewriteStatement(StatementNode statement) {
            m_found |= (statement is ErrorStatementNode);

            return base.RewriteStatement(statement: statement);
        }
    }
}
