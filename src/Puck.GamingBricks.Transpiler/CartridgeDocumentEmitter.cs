using System.Text;
using System.Text.Json.Nodes;
using Puck.Abstractions.Documents;
using Puck.Transpiler;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.Transpiler.Lowering;

namespace Puck.GamingBricks.Transpiler;

/// <summary>Lowers a parsed Puck DSL document into canonical <c>puck.cartridge.v1</c> JSON.</summary>
public static class CartridgeDocumentEmitter {
    // The compound-assignment spellings, and the `set` operation each stands for.
    private static readonly Dictionary<string, string> s_operations = new(StringComparer.Ordinal) {
        ["&"] = "and",
        ["-"] = "subtract",
        ["*"] = "mul",
        ["/"] = "div",
        ["%"] = "mod",
        ["^"] = "xor",
        ["|"] = "or",
        ["<<"] = "shl",
        [">>"] = "shr",
    };

    // The infix comparators, and the cartridge comparison each stands for.
    private static readonly Dictionary<string, string> s_comparisons = new(StringComparer.Ordinal) {
        ["=="] = "eq",
        ["!="] = "ne",
        ["<"] = "lt",
        ["<="] = "le",
        [">"] = "gt",
        [">="] = "ge",
    };

    // Which call-form action arguments are operands rather than plain strings. A `CartridgeValue` field named here
    // is converted; everything else is written through as the lowered value.
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
    /// <returns>The canonical object, paired with the diagnostics raised while building it.</returns>
    public static CompilationResult<JsonObject> LowerWithDiagnostics(DocumentNode document, DiagnosticBag? diagnostics = null, SourceMap? sourceMap = null) {
        ArgumentNullException.ThrowIfNull(document);

        diagnostics ??= new DiagnosticBag();

        var root = new JsonObject();
        var scope = new DocumentScope(
            vocabulary: CartridgeVocabulary.Instance,
            sourceMap: sourceMap,
            diagnostics: diagnostics,
            schema: (document.Schema ?? CartridgeVocabulary.Schema)
        );

        if (document.Schema is not null) {
            root["schema"] = document.Schema;
        }

        foreach (var statement in document.Statements) {
            ProcessStatement(statement, root, scope);
        }

        return new CompilationResult<JsonObject>((JsonObject)DocumentLowering.Canonicalize(node: root)!, diagnostics);
    }

    /// <summary>Lowers a parsed document and serializes it to canonical JSON bytes.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>Deterministic canonical UTF-8 bytes.</returns>
    public static byte[] CompileToUtf8Bytes(DocumentNode document) =>
        CanonicalJsonDocument.Serialize(node: LowerWithDiagnostics(document: document).Value!);

    /// <summary>Lowers a parsed document and serializes it to canonical JSON text.</summary>
    /// <param name="document">The parsed document.</param>
    /// <returns>Canonical JSON text.</returns>
    public static string CompileToJson(DocumentNode document) =>
        Encoding.UTF8.GetString(bytes: CompileToUtf8Bytes(document: document));

    private static void ProcessStatement(StatementNode statement, JsonObject target, DocumentScope scope) {
        switch (statement) {
            case LetNode let:
                scope.Constants[let.Name] = let.Value;

                break;

            case TemplateNode template:
                scope.Templates[template.Name] = template;

                break;

            case PropertyNode property:
                target[property.Name] = DocumentLowering.LowerValue(expr: property.Value, scope: scope, fieldKey: property.Name);

                break;

            case RuleBlockNode rule:
                Append(target: target, section: "rules", item: LowerRule(rule: rule, scope: scope));

                break;

            case ExpressionStatementNode { Expression: CallExpressionNode call }:
                DocumentLowering.ExpandTemplate(call: call, target: target, scope: scope, sink: ProcessStatement);

                break;

            case BlockNode block:
                LowerBlock(block: block, target: target, scope: scope);

                break;
        }
    }

    // A named block is one row of the section its identifier names (`sound "chime" { }` appends to `sounds`); an
    // unnamed one is that section's own object (`palettes { }`).
    private static void LowerBlock(BlockNode block, JsonObject target, DocumentScope scope) {
        var obj = new JsonObject();
        var pointer = scope.CurrentPointer;

        scope.CurrentPointer = $"{pointer}/{block.Identifier}";
        scope.SourceMap?.Register(scope.CurrentPointer, block.Span);

        foreach (var statement in block.Statements) {
            ProcessStatement(statement, obj, scope);
        }

        scope.CurrentPointer = pointer;

        if (block.Name is null) {
            target[block.Identifier] = obj;

            return;
        }

        obj["name"] = block.Name;

        Append(target: target, section: Pluralize(identifier: block.Identifier), item: obj);
    }

    private static JsonObject LowerRule(RuleBlockNode rule, DocumentScope scope) {
        var conditions = new JsonArray();
        var body = new JsonArray();

        foreach (var statement in rule.Statements) {
            if (statement is WhenStatementNode gate) {
                LowerConditions(predicate: gate.Predicate, into: conditions, scope: scope);

                continue;
            }

            if (LowerAction(statement: statement, scope: scope) is { } action) {
                body.AppendNode(item: action);
            }
        }

        return new JsonObject {
            ["name"] = rule.Name,
            ["when"] = conditions,
            ["body"] = body,
        };
    }

    // A cartridge gate is a conjunction and nothing else: the hardware tests each condition in order and stops at
    // the first that fails, so `and` flattens into the list and `or`/`not` have nowhere to go.
    private static void LowerConditions(PredicateNode predicate, JsonArray into, DocumentScope scope) {
        switch (predicate) {
            case AndPredicateNode and:
                foreach (var operand in and.Operands) {
                    LowerConditions(predicate: operand, into: into, scope: scope);
                }

                break;

            case ComparisonPredicateNode comparison: {
                if (!s_comparisons.TryGetValue(key: comparison.Comparator, value: out var spelling)) {
                    Refuse(scope: scope, span: comparison.Span, message: $"'{comparison.Comparator}' is not a cartridge comparison");

                    break;
                }

                var left = CartridgeOperand.FromText(text: comparison.LeftText, scope: scope, reason: out var leftReason);
                var right = CartridgeOperand.FromText(text: comparison.RightText, scope: scope, reason: out var rightReason);

                if ((left is null) || (right is null)) {
                    Refuse(scope: scope, span: comparison.Span, message: (leftReason ?? rightReason)!);

                    break;
                }

                into.AppendNode(item: new JsonObject {
                    ["kind"] = "compare",
                    ["left"] = left,
                    ["comparison"] = spelling,
                    ["right"] = right,
                });

                break;
            }

            case CallPredicateNode { Call.Name: "key" } key: {
                if (key.Call.Arguments.Count != 2) {
                    Refuse(scope: scope, span: key.Span, message: "a key condition reads 'key(<button>, <held|pressed|released>)'");

                    break;
                }

                into.AppendNode(item: new JsonObject {
                    ["kind"] = "key",
                    ["key"] = ArgumentWord(argument: key.Call.Arguments[0]),
                    ["mode"] = ArgumentWord(argument: key.Call.Arguments[1]),
                });

                break;
            }

            default:
                Refuse(scope: scope, span: predicate.Span, message: "a cartridge gate is a conjunction of comparisons and key tests");

                break;
        }
    }

    private static JsonNode? LowerAction(StatementNode statement, DocumentScope scope) {
        switch (statement) {
            case SetCellStatementNode set:
                return LowerAssignment(target: set.Target, operation: "set", rhs: set.Rhs, span: set.Span, scope: scope);

            case AddCellStatementNode add:
                return LowerAssignment(target: add.Target, operation: "add", rhs: add.Rhs, span: add.Span, scope: scope);

            case CompoundAssignStatementNode compound: {
                if (!s_operations.TryGetValue(key: compound.Operator, value: out var operation)) {
                    Refuse(scope: scope, span: compound.Span, message: $"'{compound.Operator}=' is not a cartridge operation");

                    return null;
                }

                return LowerAssignment(target: compound.Target, operation: operation, rhs: compound.Rhs, span: compound.Span, scope: scope);
            }

            case IfStatementNode branch: {
                var conditions = new JsonArray();

                LowerConditions(predicate: branch.Condition, into: conditions, scope: scope);

                var result = new JsonObject {
                    ["kind"] = "if",
                    ["when"] = conditions,
                    ["then"] = LowerActions(statements: branch.Then, scope: scope),
                };

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
                Refuse(scope: scope, span: statement.Span, message: $"'{statement.GetType().Name}' is not a cartridge rule step");

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

    private static JsonNode? LowerAssignment(RowRefNode target, string operation, RhsNode rhs, SourceSpan span, DocumentScope scope) {
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

        return new JsonObject {
            ["kind"] = "set",
            ["target"] = destination,
            ["operation"] = operation,
            ["value"] = value,
        };
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
            var lowered = DocumentLowering.LowerValue(expr: argument.Value, scope: scope, fieldKey: key);

            if (s_operandArguments.Contains(item: key)) {
                var operand = CartridgeOperand.FromLoweredValue(node: lowered, scope: scope, reason: out var reason);

                if (operand is null) {
                    Refuse(scope: scope, span: argument.Value.Span, message: $"'{key}': {reason}");

                    return null;
                }

                result[key] = operand;
            } else {
                result[key] = lowered;
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
