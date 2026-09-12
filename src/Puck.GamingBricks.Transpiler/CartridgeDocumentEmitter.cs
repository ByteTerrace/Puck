using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

using Puck.GamingBricks.Forge;
using Puck.State;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Lowers a parsed Puck DSL document into canonical <c>puck.cartridge.v1</c> JSON.</summary>
public static class CartridgeDocumentEmitter {
    // The compound-assignment spellings, and the engine opcode each stands for. A plain `=` carries no opcode.
    private static readonly Dictionary<string, string> s_operations = new(StringComparer.Ordinal) {
        ["&"] = nameof(ExpressionOp.BitAnd),
        ["-"] = nameof(ExpressionOp.Subtract),
        ["*"] = nameof(ExpressionOp.Multiply),
        ["/"] = nameof(ExpressionOp.Divide),
        ["%"] = nameof(ExpressionOp.Modulo),
        ["^"] = nameof(ExpressionOp.BitXor),
        ["|"] = nameof(ExpressionOp.BitOr),
        ["<<"] = nameof(ExpressionOp.ShiftLeft),
        [">>"] = nameof(ExpressionOp.ShiftRight),
    };

    // The infix comparators, and the engine comparison each stands for.
    private static readonly Dictionary<string, string> s_comparisons = new(StringComparer.Ordinal) {
        ["=="] = nameof(ActionStateComparison.Equal),
        ["!="] = nameof(ActionStateComparison.NotEqual),
        ["<"] = nameof(ActionStateComparison.Less),
        ["<="] = nameof(ActionStateComparison.LessOrEqual),
        [">"] = nameof(ActionStateComparison.Greater),
        [">="] = nameof(ActionStateComparison.GreaterOrEqual),
    };

    // Which call-form action arguments carry an expression rather than a plain string. A field named here is
    // converted; everything else is written through as the lowered value.
    private static readonly HashSet<string> s_operandArguments = new(StringComparer.Ordinal) {
        "amount", "colour", "column", "palette", "rate", "row", "tile", "weight",
    };

    // The rule-body actions written as a call. `save`, `load`, `stop` and `break` take no arguments at all.
    private static readonly HashSet<string> s_callActions = new(StringComparer.Ordinal) {
        "blend", "blit", "clock", "fade", "load", "map", "play", "plot", "save", "stop",
    };

    /// <summary>Lowers a parsed document into a canonical cartridge JSON object.</summary>
    /// <param name="document">The parsed document.</param>
    /// <param name="diagnostics">The bag refusals are reported into, or <see langword="null"/> for a fresh one.</param>
    /// <param name="sourceMap">The JSON-pointer-to-span map to fill, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Cancels evaluation and expansion.</param>
    /// <returns>The canonical object, paired with the diagnostics raised while building it.</returns>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(DocumentNode document, DiagnosticBag? diagnostics = null, SourceMap? sourceMap = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(document);

        diagnostics ??= new DiagnosticBag();

        var root = new JsonObject();
        var scope = new DocumentScope(
            vocabulary: CartridgeVocabulary.Instance,
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            schema: (document.Schema ?? CartridgeVocabulary.Schema)
        ) { Budget = new DocumentEvaluationBudget { CancellationToken = cancellationToken } };

        if (document.Schema is not null) {
            root["schema"] = document.Schema;
        }

        scope.IndexDeclarations(document.Statements);
        try {
            foreach (var statement in document.Statements) {
                ProcessStatement(statement, root, scope);
            }
        } catch (DocumentEvaluationException error) {
            diagnostics.ReportError(error.Code, error.Message, error.Span);
        }

        return new CompilationResult<JsonObject>((JsonObject)DocumentLowering.Canonicalize(node: root)!, diagnostics);
    }

    /// <summary>Lowers a parsed document and serializes it to canonical JSON bytes.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>Deterministic canonical UTF-8 bytes.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static byte[] CompileToUtf8Bytes(DocumentNode document) =>
        CanonicalJsonDocument.Serialize(node: LowerWithDiagnostics(document: document).RequireValue());

    /// <summary>Lowers a parsed document and serializes it to canonical JSON text.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>Canonical JSON text.</returns>
    /// <exception cref="InvalidOperationException">Lowering reports an error.</exception>
    public static string CompileToJson(DocumentNode document) =>
        Encoding.UTF8.GetString(bytes: CompileToUtf8Bytes(document: document));

    private static void ProcessStatement(StatementNode statement, JsonObject target, DocumentScope scope) {
        using var evaluation = scope.Budget.Enter(statement.Span);
        switch (statement) {
            case LetNode:
            case TemplateNode:
                break;

            case PropertyNode property: {
                    var lowered = DocumentLowering.LowerValue(expr: property.Value, scope: scope, fieldKey: property.Name);

                    scope.SourceMap?.Register($"{scope.CurrentPointer}/{property.Name}", property.Span);
                    DocumentLowering.AssignOrExtend(target, property.Name, lowered);

                    break;
                }

            case RuleBlockNode rule:
                scope.SourceMap?.Register($"{scope.CurrentPointer}/rules/{(target["rules"] as JsonArray)?.Count ?? 0}", rule.Span);
                Append(target: target, section: "rules", item: LowerRule(rule: rule, scope: scope));

                break;

            case ExpressionStatementNode { Expression: CallExpressionNode call }:
                DocumentLowering.ExpandTemplate(call: call, target: target, scope: scope, sink: ProcessStatement);

                break;

            case BlockNode block:
                LowerBlock(block: block, target: target, scope: scope);

                break;

            // A `for` is a compile-time loop: the document carries the rows it produced, never the loop. A cartridge
            // author generates repeated rules and section rows exactly as a world author generates repeated shapes.
            case ForStatementNode loop:
                DocumentLowering.ExpandFor(loop: loop, target: target, scope: scope, sink: ProcessStatement);

                break;

            // A statement no case above admits would otherwise be dropped without a trace — a misspelled row keyword
            // silently losing the whole row and everything nested in it.
            default:
                Refuse(scope: scope, span: statement.Span, message: $"{Spell(statement: statement)} has no place in a cartridge document");

                break;
        }
    }

    // How a refused statement is named back to its author: what they wrote, never the parser's type for it.
    private static string Spell(StatementNode statement) => statement switch {
        BlockNode block => $"a '{block.Identifier}' block",
        FlagStatementNode flag => $"a bare '{flag.Name}'",
        ImportNode => "an 'import'",
        PropertyNode property => $"a '{property.Name}' property",
        ExpressionStatementNode => "an expression statement",
        _ => "this statement",
    };

    // A named block is one row of the section its identifier names (`sound "chime" { }` appends to `sounds`); an
    // unnamed one is that section's own object (`palettes { }`).
    private static void LowerBlock(BlockNode block, JsonObject target, DocumentScope scope) {
        var obj = new JsonObject();
        var pointer = scope.CurrentPointer;
        // Resolved rather than read straight off the node, so a row generated inside a `for` can carry an
        // interpolated name (`sound $"tone-{index}" { }`) and land as its own row rather than as a nameless section.
        var name = DocumentLowering.ResolveBlockName(block: block, scope: scope);

        var section = Pluralize(block.Identifier);
        scope.CurrentPointer = name is null ? $"{pointer}/{block.Identifier}" : $"{pointer}/{section}/{(target[section] as JsonArray)?.Count ?? 0}";
        scope.SourceMap?.Register(scope.CurrentPointer, block.Span);

        foreach (var statement in block.Statements) {
            ProcessStatement(statement, obj, scope);
        }

        scope.CurrentPointer = pointer;

        if (name is null) {
            target[block.Identifier] = obj;

            return;
        }

        obj["name"] = name;

        Append(target: target, section: Pluralize(identifier: block.Identifier), item: obj);
    }

    private static JsonObject LowerRule(RuleBlockNode rule, DocumentScope scope) {
        JsonNode? conditions = null;
        var body = new JsonArray();

        foreach (var statement in rule.Statements) {
            if (statement is WhenStatementNode gate) {
                conditions = LowerGate(predicate: gate.Predicate, scope: scope);

                continue;
            }

            if (LowerAction(statement: statement, scope: scope) is { } action) {
                body.AppendNode(item: action);
            }
        }

        var result = new JsonObject { ["name"] = rule.Name };

        // A rule with no gate carries no gate member; the absent one is what "every frame" is spelled as.
        if (conditions is not null) {
            result["when"] = conditions;
        }

        result["body"] = body;

        return result;
    }

    // A gate lowers to the engine's own predicate vocabulary, so and/or/not are the composition arms rather than
    // three different cartridge spellings. A button test is a comparison of the reserved input channel against one,
    // which is what lets input sit under or and not like any other operand.
    private static JsonNode? LowerGate(PredicateNode predicate, DocumentScope scope) {
        switch (predicate) {
            case AndPredicateNode and:
                return Composite(discriminator: "all", operands: and.Operands, scope: scope);

            case OrPredicateNode or:
                return Composite(discriminator: "any", operands: or.Operands, scope: scope);

            case NotPredicateNode not: {
                var inner = LowerGate(predicate: not.Operand, scope: scope);

                return ((inner is null) ? null : new JsonObject { ["$type"] = "not", ["predicate"] = inner });
            }

            case ComparisonPredicateNode comparison: {
                if (!s_comparisons.TryGetValue(key: comparison.Comparator, value: out var spelling)) {
                    Refuse(scope: scope, span: comparison.Span, message: $"'{comparison.Comparator}' is not a cartridge comparison");

                    return null;
                }

                var left = CartridgeOperand.FromText(text: comparison.LeftText, scope: scope, reason: out var leftReason);
                var right = CartridgeOperand.FromText(text: comparison.RightText, scope: scope, reason: out var rightReason);

                if ((left is null) || (right is null)) {
                    Refuse(scope: scope, span: comparison.Span, message: (leftReason ?? rightReason)!);

                    return null;
                }

                return Compare(left: left, comparison: spelling, right: right);
            }

            case CallPredicateNode { Call.Name: "key" } key: {
                if (key.Call.Arguments.Count != 2) {
                    Refuse(scope: scope, span: key.Span, message: "a key condition reads 'key(<button>, <held|pressed|released>)'");

                    return null;
                }

                var button = ArgumentWord(argument: key.Call.Arguments[0]);
                var mode = ArgumentWord(argument: key.Call.Arguments[1]);

                return Compare(left: JsonValue.Create(value: $"{CartridgeExpressions.KeyPrefix}{button}:{mode}"), comparison: nameof(ActionStateComparison.Equal), right: JsonValue.Create(value: "1"));
            }

            default:
                Refuse(scope: scope, span: predicate.Span, message: "a cartridge gate composes comparisons and key tests through and, or and not");

                return null;
        }
    }

    private static JsonObject Compare(JsonNode? left, string comparison, JsonNode? right) => new() {
        ["$type"] = "compareValue",
        ["left"] = left,
        ["comparison"] = comparison,
        ["right"] = right,
        ["kind"] = nameof(CellKind.Int),
    };

    private static JsonNode? Composite(string discriminator, IReadOnlyList<PredicateNode> operands, DocumentScope scope) {
        var predicates = new JsonArray();

        foreach (var operand in operands) {
            var inner = LowerGate(predicate: operand, scope: scope);

            if (inner is null) {
                return null;
            }

            predicates.AppendNode(item: inner);
        }

        // One arm needs no wrapper: a single-element conjunction and the comparison itself are the same gate, and the
        // flatter shape is what a guard is recognised by.
        return ((predicates.Count == 1) ? predicates[0]!.DeepClone() : new JsonObject { ["$type"] = discriminator, ["predicates"] = predicates });
    }

    private static JsonNode? LowerAction(StatementNode statement, DocumentScope scope) {
        switch (statement) {
            case SetCellStatementNode set:
                return LowerAssignment(target: set.Target, operation: null, rhs: set.Rhs, span: set.Span, scope: scope);

            case AddCellStatementNode add:
                return LowerAssignment(target: add.Target, operation: nameof(ExpressionOp.Add), rhs: add.Rhs, span: add.Span, scope: scope);

            case CompoundAssignStatementNode compound: {
                if (!s_operations.TryGetValue(key: compound.Operator, value: out var operation)) {
                    Refuse(scope: scope, span: compound.Span, message: $"'{compound.Operator}=' is not a cartridge operation");

                    return null;
                }

                return LowerAssignment(target: compound.Target, operation: operation, rhs: compound.Rhs, span: compound.Span, scope: scope);
            }

            case IfStatementNode branch: {
                var result = new JsonObject { ["kind"] = "if" };

                if (LowerGate(predicate: branch.Condition, scope: scope) is { } gate) {
                    result["when"] = gate;
                }

                result["then"] = LowerActions(statements: branch.Then, scope: scope);

                if (branch.Else is not null) {
                    result["else"] = LowerActions(statements: branch.Else, scope: scope);
                }

                return result;
            }

            case RepeatStatementNode loop: {
                var count = DocumentLowering.LowerValue(expr: loop.Count, scope: scope);

                if (!DocumentLowering.TryReadNumber(node: count, number: out var iterations) || (iterations != Math.Truncate(d: iterations)) || (iterations < 1) || (iterations > 255)) {
                    Refuse(scope: scope, span: loop.Span, message: "a repeat count is a whole number in 1..255 known at compile time");

                    return null;
                }

                return new JsonObject {
                    ["kind"] = "repeat",
                    ["count"] = (int)iterations,
                    ["index"] = loop.Index,
                    ["body"] = LowerActions(statements: loop.Body, scope: scope),
                };
            }

            case BreakStatementNode:
                return new JsonObject { ["kind"] = "break" };

            case ExpressionStatementNode { Expression: CallExpressionNode call }:
                return LowerCallAction(call: call, scope: scope);

            default:
                Refuse(scope: scope, span: statement.Span, message: $"{Spell(statement: statement)} is not a cartridge rule step");

                return null;
        }
    }

    private static JsonArray LowerActions(IReadOnlyList<StatementNode> statements, DocumentScope scope) {
        var arr = new JsonArray();

        foreach (var statement in statements) {
            if (LowerAction(statement: statement, scope: scope) is { } action) {
                arr.AppendNode(item: action);
            }
        }

        return arr;
    }

    // A null operation is plain assignment, which no opcode spells: the field is omitted rather than carrying a name.
    private static JsonNode? LowerAssignment(RowRefNode target, string? operation, RhsNode rhs, SourceSpan span, DocumentScope scope) {
        var destination = CartridgeOperand.FromRowRef(rowRef: target, scope: scope, reason: out var targetReason);

        if (destination is null) {
            Refuse(scope: scope, span: span, message: targetReason!);

            return null;
        }

        if (rhs is not RhsOperandNode operand) {
            Refuse(scope: scope, span: span, message: "a cartridge value is a literal, variable or array element");

            return null;
        }

        var value = CartridgeOperand.FromText(text: operand.Text, scope: scope, reason: out var valueReason);

        if (value is null) {
            Refuse(scope: scope, span: span, message: valueReason!);

            return null;
        }

        var step = new JsonObject {
            ["kind"] = "set",
            ["target"] = destination,
            ["value"] = value,
        };

        if (operation is not null) {
            step["operation"] = operation;
        }

        return step;
    }

    private static JsonNode? LowerCallAction(CallExpressionNode call, DocumentScope scope) {
        if (!s_callActions.Contains(item: call.Name)) {
            Refuse(scope: scope, span: call.Span, message: $"'{call.Name}' is not a cartridge rule step");

            return null;
        }

        var result = new JsonObject { ["kind"] = call.Name };
        var positionalIndex = 0;

        foreach (var argument in call.Arguments) {
            var key = (argument.Name ?? CartridgeVocabulary.Instance.NameCallArgument(callName: call.Name, positionalIndex: positionalIndex) ?? $"arg{positionalIndex}");
            if (s_operandArguments.Contains(item: key)) {
                var operand = CartridgeOperand.FromExpression(argument.Value, scope, out var reason);

                if (operand is null) {
                    Refuse(scope: scope, span: argument.Value.Span, message: $"'{key}': {reason}");

                    return null;
                }

                result[key] = operand;
            } else {
                result[key] = DocumentLowering.LowerValue(expr: argument.Value, scope: scope, fieldKey: key);
            }

            positionalIndex++;
        }

        return result;
    }

    // An argument written as a bare word (`held`, `left`) parses as an identifier; its name is the word itself.
    private static string ArgumentWord(ArgumentNode argument) => argument.Value switch {
        IdentifierExpressionNode ident => ident.Name,
        LiteralExpressionNode { Value: string text } => text,
        _ => string.Empty,
    };

    private static string Pluralize(string identifier) => identifier.EndsWith(value: "s", comparisonType: StringComparison.Ordinal) ? identifier : (identifier + "s");

    private static void Append(JsonObject target, string section, JsonNode item) {
        if (target[section] is not JsonArray arr) {
            arr = [];
            target[section] = arr;
        }

        arr.AppendNode(item: item);
    }

    private static void Refuse(DocumentScope scope, SourceSpan span, string message) =>
        scope.Diagnostics.ReportError(PuckDiagnosticCodes.SemanticValidation, message, span);
}
