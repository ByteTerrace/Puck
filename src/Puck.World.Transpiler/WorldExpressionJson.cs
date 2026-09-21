using System.Text.Json.Nodes;
using Puck.State;
using Puck.Transpiler.Diagnostics;

namespace Puck.World.Transpiler;

/// <summary>The one crossing between a world document's expression IR and the infix text the <c>.puck</c> grammar
/// and the SQL dialect write: lowering parses text to the IR the document holds, and decompiling prints that IR
/// back.</summary>
/// <remarks>Both directions run through <see cref="ExpressionSpelling"/>, so print-then-parse is the identity over
/// every program a document can carry. Text that reaches a program-valued member without parsing is reported as
/// PUCK002 rather than lowered to an empty program, so a lowering arm that writes something the expression grammar
/// cannot read is refused where it is written.</remarks>
public static class WorldExpressionJson {
    // Which members of a lowered object the document model types as ExpressionProgram. Three of them are decided
    // by more than the member's own name: "left"/"right" are programs on a compareValue predicate and something
    // else everywhere else, "score" is a decision option's and not a search row's (which stays text), and "value"
    // is a pattern row's.
    private static IEnumerable<string> ProgramMembers(JsonObject obj, string? array) {
        yield return "expression";
        yield return "cohesionAffinity";
        yield return "alignmentAffinity";
        if ((obj["$type"] is JsonValue discriminator) && (discriminator.GetValue<string>() == "compareValue")) {
            yield return "left";
            yield return "right";
        }
        if (array == "options") {
            yield return "score";
        }
        if (array == "patterns") {
            yield return "value";
        }
    }

    /// <summary>Rewrites every program-valued member of a lowered document that still carries its authored infix
    /// text into the IR, in place.</summary>
    /// <param name="node">The lowered document, or a subtree of one.</param>
    /// <remarks>The lowering paths write an operand's text where the model holds a program; this is the one pass
    /// that turns that text into the program, so no emitter arm can forget to.</remarks>
    /// <param name="diagnostics">Where a member whose text does not parse is reported.</param>
    public static void Lower(JsonNode? node, DiagnosticBag? diagnostics = null) => Lower(
        array: null,
        diagnostics: diagnostics,
        node: node
    );

    private static void Lower(JsonNode? node, string? array, DiagnosticBag? diagnostics) {
        switch (node) {
            case JsonObject obj:
                foreach (var member in ProgramMembers(
                    array: array,
                    obj: obj
                )) {
                    if (
                        (obj[member] is JsonValue text) &&
                        (text.GetValueKind() == System.Text.Json.JsonValueKind.String)
                    ) {
                        obj[member] = Node(
                            diagnostics: diagnostics,
                            text: text.GetValue<string>()
                        );
                    }
                }
                foreach (var (member, child) in obj.ToArray()) {
                    if (
                        (member == "lanes") &&
                        (child is JsonArray lanes)
                    ) {
                        for (var index = 0; (index < lanes.Count); index++) {
                            if (
                                (lanes[index] is JsonValue lane) &&
                                (lane.GetValueKind() == System.Text.Json.JsonValueKind.String)
                            ) {
                                lanes[index] = Node(
                                    diagnostics: diagnostics,
                                    text: lane.GetValue<string>()
                                );
                            }
                        }
                        continue;
                    }
                    Lower(
                        array: member,
                        diagnostics: diagnostics,
                        node: child
                    );
                }
                break;
            case JsonArray list:
                foreach (var child in list.ToArray()) {
                    Lower(
                        array: array,
                        diagnostics: diagnostics,
                        node: child
                    );
                }
                break;
            default:
                break;
        }
    }
    /// <summary>Returns the IR tree an operand's authored text lowers to.</summary>
    /// <param name="text">The infix spelling.</param>
    /// <param name="diagnostics">Where a spelling that does not parse is reported as PUCK002.</param>
    /// <returns>The IR tree; an empty program when the spelling does not parse.</returns>
    public static JsonNode Node(string? text, DiagnosticBag? diagnostics = null) {
        if (!ExpressionSpelling.TryParse(
            error: out var error,
            program: out var program,
            text: text
        )) {
            diagnostics?.ReportError(
                code: PuckDiagnosticCodes.OperandParse,
                message: $"'{text}' {error}",
                span: default
            );
        }

        return ExpressionProgramJsonConverter.ToNode(program: program);
    }
    /// <summary>Returns the infix spelling an expression-valued document member prints back as.</summary>
    /// <param name="node">The member's IR tree.</param>
    /// <returns>The spelling, or an empty string when the tree is absent or not a well-formed program.</returns>
    public static string Text(JsonNode? node) {
        if (node is null) {
            return string.Empty;
        }

        return (ExpressionSpelling.TryPrint(
            program: ExpressionProgramJsonConverter.FromNode(node: node),
            text: out var text
        )
            ? text
            : string.Empty
        );
    }
    /// <summary>Returns the infix spelling a comparison's operand prints back as, parenthesized exactly when the
    /// joined <c>left cmp right</c> text would otherwise re-parse as a different program.</summary>
    /// <param name="node">The operand's IR tree.</param>
    /// <returns>The spelling, or an empty string when the tree is absent or not a well-formed program.</returns>
    public static string ComparisonOperand(JsonNode? node) {
        if (node is null) {
            return string.Empty;
        }

        return (ExpressionSpelling.TryPrintComparisonOperand(
            program: ExpressionProgramJsonConverter.FromNode(node: node),
            text: out var text
        )
            ? text
            : string.Empty
        );
    }
    /// <summary>Returns the infix spelling a decompiler writes into unquoted <c>.puck</c> source for an
    /// expression-valued document member: a reserved channel as an author's call, and a plain row read one of the
    /// caller's <see cref="ExpressionSpelling.WithLocals"/> shadows backquoted, so it still reads the row once the
    /// printed source is recompiled under those locals.</summary>
    /// <param name="node">The member's IR tree.</param>
    /// <returns>The spelling, or an empty string when the tree is absent or not a well-formed program.</returns>
    public static string SourceText(JsonNode? node) {
        if (node is null) {
            return string.Empty;
        }

        return (ExpressionSpelling.TryPrintSource(
            program: ExpressionProgramJsonConverter.FromNode(node: node),
            text: out var text
        )
            ? text
            : string.Empty
        );
    }
    /// <summary>Returns <see cref="SourceText"/> parenthesized on <see cref="ComparisonOperand"/>'s terms.</summary>
    /// <param name="node">The operand's IR tree.</param>
    /// <returns>The spelling, or an empty string when the tree is absent or not a well-formed program.</returns>
    public static string SourceComparisonOperand(JsonNode? node) {
        if (node is null) {
            return string.Empty;
        }

        return (ExpressionSpelling.TryPrintSourceComparisonOperand(
            program: ExpressionProgramJsonConverter.FromNode(node: node),
            text: out var text
        )
            ? text
            : string.Empty
        );
    }
}
