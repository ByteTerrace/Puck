using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;

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

        // 3. Pass 3: Check for unused declarations
        foreach (var (name, node) in declaredLets) {
            if (!referencedIdentifiers.Contains(item: name)) {
                diagnostics.ReportInformation(
                    code: PuckDiagnosticCodes.LintUnusedLet,
                    message: $"Constant '{name}' is declared but its value is never used",
                    span: node.Span
                );
            }
        }

        foreach (var (name, node) in declaredTemplates) {
            if (!referencedIdentifiers.Contains(item: name)) {
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
            case LocalStatementNode local: CollectReferences(local.Expression, references, diagnostics); break;
            case ScoreStatementNode score: CollectReferences(score.Expression, references, diagnostics); break;
            case DerivedStateNode derived: CollectReferences(derived.Expression, references, diagnostics); break;
            case PatternDeclarationNode { Value: { } value }: CollectReferences(value, references, diagnostics); break;
            case WhenStatementNode whenClause: CollectReferences(whenClause.Predicate, references, diagnostics); break;
            case InterruptStatementNode interrupt: CollectReferences(interrupt.Predicate, references, diagnostics); break;
            case ComparisonPredicateNode comparison:
                CollectReferences(comparison.Left, references, diagnostics);
                CollectReferences(comparison.Right, references, diagnostics);
                break;
            case AndPredicateNode conjunction:
                foreach (var item in conjunction.Operands) { CollectReferences(item, references, diagnostics); }
                break;
            case OrPredicateNode disjunction:
                foreach (var item in disjunction.Operands) { CollectReferences(item, references, diagnostics); }
                break;
            case NotPredicateNode negation: CollectReferences(negation.Operand, references, diagnostics); break;
            case CallPredicateNode gate: CollectReferences(gate.Call, references, diagnostics); break;
            case SetCellStatementNode set: CollectReferences(set.Rhs, references, diagnostics); break;
            case AddCellStatementNode add: CollectReferences(add.Rhs, references, diagnostics); break;
            case CompoundAssignStatementNode compound: CollectReferences(compound.Rhs, references, diagnostics); break;
            case PushStatementNode push: CollectReferences(push.Rhs, references, diagnostics); break;
            case RhsOperandNode rhs: CollectReferences(rhs.Expression, references, diagnostics); break;
            case IdentifierExpressionNode ident:
                references.Add(item: ident.Name);
                break;

            case CallExpressionNode call:
                references.Add(item: call.Name);
                var qualifier = call.Name.IndexOf(value: '.');

                if (qualifier > 0) {
                    references.Add(item: call.Name[..qualifier]);
                }
                if (string.Equals(a: call.Name, b: "mix", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                    LintVectorMix(call: call, diagnostics: diagnostics);
                }
                foreach (var arg in call.Arguments) {
                    CollectReferences(
                        arg.Value,
                        references,
                        diagnostics,
                        inLet
                    );
                }
                break;

            case WorldDeclarationNode world:
                CollectReferences(world.Name, references, diagnostics, inLet);
                CollectReferences(world.Module, references, diagnostics, inLet);
                break;

            case WorldLinkNode link:
                CollectReferences(link.Left, references, diagnostics, inLet);
                CollectReferences(link.Right, references, diagnostics, inLet);
                foreach (var property in link.Properties) {
                    CollectReferences(property, references, diagnostics, inLet);
                }
                break;

            case AssetExpressionNode:
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

            case BlockNode block:
                // Check host block parameters
                if (string.Equals(
                    a: block.Identifier,
                    b: "host",
                    comparisonType: StringComparison.OrdinalIgnoreCase
                )) {
                    foreach (var stmt in block.Statements) {
                        if (
                            (stmt is PropertyNode prop) &&
                            string.Equals(
                            a: prop.Name,
                            b: "targetHertz",
                            comparisonType: StringComparison.OrdinalIgnoreCase
                        )
                        ) {
                            if (
                                (prop.Value is LiteralExpressionNode { Value: long hz }) &&
                                (hz <= 0)
                            ) {
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
                    CollectReferences(
                        stmt,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case PropertyNode prop:
                CollectReferences(
                    prop.Value,
                    references,
                    diagnostics,
                    inLet: false
                );
                break;

            case LetNode letNode:
                CollectReferences(
                    letNode.Value,
                    references,
                    diagnostics,
                    inLet: true
                );
                break;

            case StateTableDeclarationNode table:
                foreach (var modifier in table.Modifiers) {
                    CollectReferences(
                        modifier,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                foreach (var cell in table.Cells) {
                    CollectReferences(
                        cell.Value,
                        references,
                        diagnostics,
                        inLet: false
                    );
                    foreach (var modifier in cell.Modifiers) {
                        CollectReferences(
                            modifier,
                            references,
                            diagnostics,
                            inLet: false
                        );
                    }
                }
                break;

            case StateSlotDeclarationNode slot:
                if (slot.Value is not null) {
                    CollectReferences(
                        slot.Value,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                foreach (var modifier in slot.Modifiers) {
                    CollectReferences(
                        modifier,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case StatePileDeclarationNode pile:
                foreach (var modifier in pile.Modifiers) {
                    CollectReferences(
                        modifier,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case StateGridDeclarationNode grid:
                foreach (var modifier in grid.Modifiers) {
                    CollectReferences(
                        modifier,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                foreach (var cell in grid.Cells) {
                    CollectReferences(
                        cell.Value,
                        references,
                        diagnostics,
                        inLet: false
                    );
                    foreach (var modifier in cell.Modifiers) {
                        CollectReferences(
                            modifier,
                            references,
                            diagnostics,
                            inLet: false
                        );
                    }
                }
                break;

            case StateModifierNode modifier:
                foreach (var arg in modifier.Arguments) {
                    CollectReferences(
                        arg.Value,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case TemplateNode tmpl:
                foreach (var param in tmpl.Parameters) {
                    if (param.DefaultValue is not null) {
                        CollectReferences(
                            param.DefaultValue,
                            references,
                            diagnostics,
                            inLet: false
                        );
                    }
                }
                CollectReferences(
                    tmpl.Body,
                    references,
                    diagnostics,
                    inLet: false
                );
                break;

            case BinaryExpressionNode bin:
                CollectReferences(
                    bin.Left,
                    references,
                    diagnostics,
                    inLet: false
                );
                CollectReferences(
                    bin.Right,
                    references,
                    diagnostics,
                    inLet: false
                );
                break;

            case UnaryExpressionNode un:
                CollectReferences(
                    un.Operand,
                    references,
                    diagnostics,
                    inLet: false
                );
                break;

            case ArrayExpressionNode arr:
                foreach (var elem in arr.Elements) {
                    CollectReferences(
                        elem,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case ObjectExpressionNode obj:
                foreach (var p in obj.Properties) {
                    CollectReferences(
                        p.Value,
                        references,
                        diagnostics,
                        inLet: false
                    );
                }
                break;

            case ForStatementNode loop:
                CollectReferences(
                    loop.Sequence,
                    references,
                    diagnostics,
                    inLet
                );
                foreach (var statement in loop.Body) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: statement,
                        references: references
                    );
                }
                break;

            case RepeatStatementNode repeat:
                CollectReferences(
                    repeat.Count,
                    references,
                    diagnostics,
                    inLet
                );
                foreach (var statement in repeat.Body) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: statement,
                        references: references
                    );
                }
                break;

            case IndexExpressionNode index:
                CollectReferences(
                    index.Target,
                    references,
                    diagnostics,
                    inLet
                );
                CollectReferences(
                    index.Index,
                    references,
                    diagnostics,
                    inLet
                );
                break;

            case LambdaExpressionNode lambda:
                CollectReferences(
                    lambda.Body,
                    references,
                    diagnostics,
                    inLet
                );
                break;

            case OperandExpressionNode operand:
                if (operand.Syntax is { } syntax) { CollectOperandReferences(syntax, operand.Form, references); }
                foreach (var atom in operand.Atoms) {
                    CollectReferences(
                        atom.Value,
                        references,
                        diagnostics,
                        inLet
                    );
                }
                break;

            case InterpolatedStringNode interpolated:
                foreach (var hole in interpolated.Segments.OfType<InterpolationSegment.Hole>()) {
                    CollectReferences(
                        hole.Expression,
                        references,
                        diagnostics,
                        inLet
                    );
                }
                break;

            case ExportNode export:
                references.UnionWith(other: export.Names);
                break;

            case MemberAccessExpressionNode member:
                CollectReferences(
                    member.Target,
                    references,
                    diagnostics
                );
                break;

            case RangeExpressionNode range:
                if (range.Start is { } start) { CollectReferences(start, references, diagnostics); }
                if (range.End is { } end) { CollectReferences(end, references, diagnostics); }
                break;

            case ExpressionStatementNode exprStmt:
                CollectReferences(
                    exprStmt.Expression,
                    references,
                    diagnostics
                );
                break;

            case RuleBlockNode rule:
                foreach (var stmt in rule.Statements) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                break;

            case DecisionBlockNode decision:
                foreach (var stmt in decision.Statements) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                break;

            case OptionBlockNode option:
                foreach (var stmt in option.Statements) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                break;

            case OnNoChoiceBlockNode onNoChoice:
                foreach (var stmt in onNoChoice.Effects) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                break;

            case TransactionStatementNode transaction:
                foreach (var stmt in transaction.MainEffects) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                if (transaction.OnFailureEffects is not null) {
                    foreach (var stmt in transaction.OnFailureEffects) {
                        CollectReferences(
                            diagnostics: diagnostics,
                            inLet: inLet,
                            node: stmt,
                            references: references
                        );
                    }
                }
                break;

            case IfStatementNode ifStmt:
                CollectReferences(ifStmt.Condition, references, diagnostics);
                foreach (var stmt in ifStmt.Then) {
                    CollectReferences(
                        diagnostics: diagnostics,
                        inLet: inLet,
                        node: stmt,
                        references: references
                    );
                }
                if (ifStmt.Else is not null) {
                    foreach (var stmt in ifStmt.Else) {
                        CollectReferences(
                            diagnostics: diagnostics,
                            inLet: inLet,
                            node: stmt,
                            references: references
                        );
                    }
                }
                break;

            case TransformStatementNode transform:
                CollectReferences(
                    transform.Transform,
                    references,
                    diagnostics,
                    inLet
                );
                break;
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
}
