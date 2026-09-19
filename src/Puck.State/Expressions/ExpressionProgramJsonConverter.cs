using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>Reads and writes an <see cref="ExpressionProgram"/> as the IR and nothing else:
/// <c>{ "instructions": [ { "op": "Operand", "name": "hp" }, … ] }</c>, with the shared subprogram table beside it
/// when the program carries one.</summary>
/// <remarks>An instruction is one object whose <c>op</c> names the <see cref="ExpressionOp"/> and whose remaining
/// members are that operation's payload, so the wire shape is the payload union flattened onto the instruction.
/// <see cref="ToNode"/> and <see cref="FromNode"/> are that shape, and a document emitter or decompiler that builds
/// the tree itself uses them rather than restating it. A vocabulary that spells a program as infix text instead
/// reaches <see cref="ExpressionSpellingJsonConverter"/>.</remarks>
public sealed class ExpressionProgramJsonConverter : JsonConverter<ExpressionProgram>, IJsonSchemaNodeConverter {
    private static JsonObject InstructionNode(Instruction instruction) {
        var result = new JsonObject { ["op"] = instruction.Operation.ToString() };

        switch (instruction.Payload) {
            case InstructionPayload.Argument argument:
                result["index"] = argument.Index;
                break;
            case InstructionPayload.Board board:
                result["topology"] = board.Topology;
                result["index"] = board.Index;
                break;
            case InstructionPayload.Call call:
                result["subprogram"] = call.Subprogram;
                break;
            case InstructionPayload.Constant constant:
                result["value"] = constant.Value;
                break;
            case InstructionPayload.Fold fold:
                result["family"] = fold.Family;
                result["binder"] = fold.Binder;
                result["subprogram"] = fold.Subprogram;
                break;
            case InstructionPayload.State state:
                result["name"] = state.Name;
                if (state.Key is { } key) { result["key"] = key; }
                break;
            case InstructionPayload.Vector vector:
                result["left"] = VectorNode(operand: vector.Left);
                result["right"] = VectorNode(operand: vector.Right);
                break;
            default:
                break;
        }
        return result;
    }
    private static Instruction ReadInstruction(JsonNode? node) {
        if (node is not JsonObject obj) {
            throw new JsonException(message: "an instruction is an object carrying 'op'");
        }
        if (!Enum.TryParse(
            ignoreCase: false,
            result: out ExpressionOp operation,
            value: Text(
                node: obj["op"],
                member: "op"
            )
        )) {
            throw new JsonException(message: $"'{obj["op"]}' is not an expression operation");
        }

        var members = PayloadMembers(shape: ExpressionOperators.PayloadOf(operation: operation));

        foreach (var (member, _) in obj) {
            if (
                (member != "op") &&
                (Array.IndexOf(
                    array: members,
                    value: member
                ) < 0)
            ) {
                throw new JsonException(message: $"instruction '{operation}' carries no member '{member}'");
            }
        }

        return new Instruction(
            Operation: operation,
            Payload: (ExpressionOperators.PayloadOf(operation: operation) switch {
                PayloadShape.Argument => new InstructionPayload.Argument(Index: Number(
                node: obj["index"],
                member: "index"
            )),
                PayloadShape.Board => new InstructionPayload.Board(
                Index: Text(
                    node: obj["index"],
                    member: "index"
                ),
                Topology: Text(
                    node: obj["topology"],
                    member: "topology"
                )
            ),
                PayloadShape.Call => new InstructionPayload.Call(Subprogram: Number(
                node: obj["subprogram"],
                member: "subprogram"
            )),
                PayloadShape.Constant => new InstructionPayload.Constant(Value: Literal(
                node: obj["value"],
                member: "value"
            )),
                PayloadShape.Fold => new InstructionPayload.Fold(
                Binder: Text(
                    node: obj["binder"],
                    member: "binder"
                ),
                Family: Text(
                    node: obj["family"],
                    member: "family"
                ),
                Subprogram: Number(
                    node: obj["subprogram"],
                    member: "subprogram"
                )
            ),
                PayloadShape.State => new InstructionPayload.State(
                Key: (obj["key"]?.GetValue<string>()),
                Name: Text(
                    node: obj["name"],
                    member: "name"
                )
            ),
                PayloadShape.Vector => new InstructionPayload.Vector(
                Left: ReadVector(node: obj["left"]),
                Right: ReadVector(node: obj["right"])
            ),
                _ => null,
            })
        );
    }

    /// <summary>Returns the member names an instruction of a payload shape carries beside <c>op</c>, spelled as the
    /// writer spells them.</summary>
    /// <remarks>KEEP IN SYNC with <see cref="MemberSchema"/> and <see cref="RequiredMembers"/>: the reader refuses a
    /// member missing from this list and the generated schema admits exactly this list, so a mis-cased or foreign
    /// member is refused by both.</remarks>
    /// <param name="shape">The payload shape.</param>
    /// <returns>The member names.</returns>
    public static string[] PayloadMembers(PayloadShape shape) => shape switch {
        PayloadShape.Argument => ["index"],
        PayloadShape.Board => ["topology", "index"],
        PayloadShape.Call => ["subprogram"],
        PayloadShape.Constant => ["value"],
        PayloadShape.Fold => ["family", "binder", "subprogram"],
        PayloadShape.State => ["name", "key"],
        PayloadShape.Vector => ["left", "right"],
        _ => [],
    };

    // 'index' is an int for Argument and a direction name for Board, so a member's type is answered per shape.
    private static JsonObject MemberSchema(PayloadShape shape, string member) => ((shape, member) switch {
        (PayloadShape.Argument, "index") => new JsonObject { ["type"] = "integer" },
        (_, "subprogram") => new JsonObject { ["type"] = "integer" },
        (_, "value") => new JsonObject { ["type"] = "number" },
        (_, "left" or "right") => VectorOperandSchema(),
        _ => new JsonObject { ["type"] = "string" },
    });
    private static string[] RequiredMembers(PayloadShape shape) => ((shape == PayloadShape.State)
        ? ["name"]
        : PayloadMembers(shape: shape)
    );
    private static JsonObject VectorOperandSchema() => new() {
        ["type"] = "object",
        ["title"] = "ExpressionVectorOperand",
        ["required"] = new JsonArray("$type"),
        ["anyOf"] = new JsonArray(
            VectorOperandArm(
                kind: "cell",
                members: ["name", "key"],
                required: ["name"]
            ),
            VectorOperandArm(
                kind: "literal",
                members: ["value"],
                required: ["value"]
            ),
            VectorOperandArm(
                kind: "embed",
                members: ["text", "space"],
                required: ["text"]
            )
        ),
    };
    private static JsonObject VectorOperandArm(string kind, string[] members, string[] required) {
        var properties = new JsonObject { ["$type"] = new JsonObject { ["const"] = kind } };

        foreach (var member in members) {
            properties[member] = new JsonObject { ["type"] = "string" };
        }

        return new JsonObject {
            ["properties"] = properties,
            ["required"] = new JsonArray([.. ((string[])["$type", .. required]).Select(selector: static name => ((JsonNode)name))]),
            ["additionalProperties"] = false,
        };
    }
    // One arm per payload shape, its 'op' the operations that take that shape. An arm names every member the reader
    // admits and refuses the rest, so a mis-cased 'Name' or a foreign 'row' fails the schema the same way it fails
    // the converter.
    private static JsonObject InstructionSchema() {
        var arms = new List<JsonNode?>();

        foreach (var shape in Enum.GetValues<PayloadShape>()) {
            var operations = Enum.GetValues<ExpressionOp>()
                .Where(predicate: operation => (ExpressionOperators.PayloadOf(operation: operation) == shape))
                .Select(selector: static operation => ((JsonNode)operation.ToString()))
                .ToArray();

            if (operations.Length == 0) {
                continue;
            }

            var properties = new JsonObject { ["op"] = new JsonObject { ["enum"] = new JsonArray(operations) } };

            foreach (var member in PayloadMembers(shape: shape)) {
                properties[member] = MemberSchema(
                    member: member,
                    shape: shape
                );
            }

            arms.Add(item: new JsonObject {
                ["properties"] = properties,
                ["required"] = new JsonArray([.. ((string[])["op", .. RequiredMembers(shape: shape)]).Select(selector: static name => ((JsonNode)name))]),
                ["additionalProperties"] = false,
            });
        }

        return new JsonObject {
            ["type"] = "object",
            ["title"] = "ExpressionInstruction",
            ["description"] = "One postfix instruction: 'op' names the operation and the remaining members are that operation's payload.",
            ["required"] = new JsonArray("op"),
            ["anyOf"] = new JsonArray([.. arms]),
        };
    }
    // An object of a closed shape carries that shape's members and nothing else, exactly as its schema arm says.
    private static void RefuseForeignMembers(JsonObject obj, string owner, params ReadOnlySpan<string> members) {
        foreach (var (member, _) in obj) {
            if (members.IndexOf(value: member) < 0) {
                throw new JsonException(message: $"{owner} carries no member '{member}'");
            }
        }
    }
    private static VectorOperand ReadVector(JsonNode? node) {
        if (node is not JsonObject obj) {
            throw new JsonException(message: "a vector operand is an object carrying '$type'");
        }

        var operandKind = Text(
            node: obj["$type"],
            member: "$type"
        );

        switch (operandKind) {
            case "cell":
                RefuseForeignMembers(obj, "a 'cell' vector operand", "$type", "name", "key");

                break;
            case "embed":
                RefuseForeignMembers(obj, "an 'embed' vector operand", "$type", "text", "space");

                break;
            case "literal":
                RefuseForeignMembers(obj, "a 'literal' vector operand", "$type", "value");

                break;
            default:
                break;
        }

        return (operandKind switch {
            "cell" => new VectorOperand.Cell(
            Key: (obj["key"]?.GetValue<string>()),
            Name: Text(
                node: obj["name"],
                member: "name"
            )
        ),
            "literal" => new VectorOperand.Literal(Value: Text(
            node: obj["value"],
            member: "value"
        )),
            "embed" => new VectorOperand.Embed(
            Space: (obj["space"]?.GetValue<string>()),
            Text: Text(
                node: obj["text"],
                member: "text"
            )
        ),
            var kind => throw new JsonException(message: $"'{kind}' is not a vector operand kind"),
        });
    }
    // A JsonValue parsed from text carries the number in whatever CLR type the reader chose, so the exact decimal
    // is read through whichever accessor answers rather than one assumed type.
    private static decimal Literal(JsonNode? node, string member) {
        if (node is JsonValue value) {
            if (value.TryGetValue<decimal>(value: out var exact)) {
                return exact;
            }
            if (value.TryGetValue<long>(value: out var integral)) {
                return integral;
            }
        }
        throw Missing(member: member);
    }
    private static int Number(JsonNode? node, string member) => ((node is JsonValue value)
        ? value.GetValue<int>()
        : throw Missing(member: member)
    );
    private static string Text(JsonNode? node, string member) => ((node is JsonValue value)
        ? value.GetValue<string>()
        : throw Missing(member: member)
    );
    private static JsonException Missing(string member) => new(message: $"an expression instruction carries '{member}'");
    private static JsonObject VectorNode(VectorOperand operand) => (operand switch {
        VectorOperand.Cell cell => Keyed(
        key: cell.Key,
        kind: "cell",
        member: "name",
        value: cell.Name
    ),
        VectorOperand.Literal literal => Keyed(
        key: null,
        kind: "literal",
        member: "value",
        value: literal.Value
    ),
        VectorOperand.Embed embed => Space(embed: embed),
        _ => throw new JsonException(message: "a vector operand is a cell, a literal, or an embed"),
    });
    private static JsonObject Keyed(string kind, string member, string value, string? key) {
        var result = new JsonObject {
            ["$type"] = kind,
            [member] = value,
        };

        if (key is not null) { result["key"] = key; }
        return result;
    }
    private static JsonObject Space(VectorOperand.Embed embed) {
        var result = new JsonObject {
            ["$type"] = "embed",
            ["text"] = embed.Text,
        };

        if (embed.Space is { } space) { result["space"] = space; }
        return result;
    }

    /// <summary>Reads a program from the IR tree.</summary>
    /// <param name="node">The tree.</param>
    /// <returns>The program.</returns>
    /// <exception cref="JsonException">The tree is not the IR shape.</exception>
    public static ExpressionProgram FromNode(JsonNode? node) {
        if (node is not JsonObject obj) {
            throw new JsonException(message: "an expression is an object carrying postfix 'instructions'");
        }
        if (obj["instructions"] is not JsonArray instructions) {
            throw new JsonException(message: "an expression object carries 'instructions'");
        }
        var subprograms = new List<Subprogram>();

        if (obj["subprograms"] is JsonArray declared) {
            foreach (var subprogram in declared) {
                if (subprogram is not JsonObject body) {
                    throw new JsonException(message: "a subprogram is an object carrying 'instructions'");
                }
                if (body["instructions"] is not JsonArray steps) {
                    throw new JsonException(message: "a subprogram carries 'instructions'");
                }

                RefuseForeignMembers(body, "a subprogram", "name", "arity", "instructions");
                subprograms.Add(item: new Subprogram(
                    Arity: ((body["arity"] is JsonValue arity)
                        ? arity.GetValue<int>()
                        : 0),
                    Instructions: [.. steps.Select(selector: ReadInstruction)],
                    Name: (body["name"]?.GetValue<string>() ?? string.Empty)
                ));
            }
        }

        return new ExpressionProgram(Instructions: [.. instructions.Select(selector: ReadInstruction)]) { Subprograms = subprograms };
    }
    /// <summary>Renders a program as the IR tree.</summary>
    /// <param name="program">The program.</param>
    /// <returns>The tree.</returns>
    public static JsonObject ToNode(ExpressionProgram program) {
        ArgumentNullException.ThrowIfNull(argument: program);

        var result = new JsonObject { ["instructions"] = new JsonArray([.. program.Instructions.Select(selector: InstructionNode)]) };

        if (program.Subprograms is { Count: > 0 } subprograms) {
            result["subprograms"] = new JsonArray([.. subprograms.Select(selector: static subprogram => ((JsonNode)new JsonObject {
                ["name"] = subprogram.Name,
                ["arity"] = subprogram.Arity,
                ["instructions"] = new JsonArray([.. subprogram.Instructions.Select(selector: InstructionNode)]),
            }))]);
        }
        return result;
    }
    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        ArgumentNullException.ThrowIfNull(argument: exportType);

        return new JsonObject {
            ["type"] = "object",
            ["description"] = "A postfix expression program: 'instructions' in evaluation order, each an object whose 'op' names an operation and whose remaining members are that operation's payload, plus the optional shared 'subprograms' a fold or call indexes into.",
            ["required"] = new JsonArray("instructions"),
            ["properties"] = new JsonObject {
                ["instructions"] = new JsonObject {
                    ["type"] = "array",
                    ["items"] = InstructionSchema(),
                },
                ["subprograms"] = new JsonObject {
                    ["type"] = "array",
                    ["items"] = new JsonObject {
                        ["type"] = "object",
                        ["required"] = new JsonArray("instructions"),
                        ["properties"] = new JsonObject {
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["arity"] = new JsonObject { ["type"] = "integer" },
                            ["instructions"] = new JsonObject {
                                ["type"] = "array",
                                ["items"] = InstructionSchema(),
                            },
                        },
                        ["additionalProperties"] = false,
                    },
                },
            },
        };
    }
    /// <inheritdoc/>
    public override ExpressionProgram? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FromNode(node: JsonNode.Parse(reader: ref reader));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ExpressionProgram value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        ToNode(program: value).WriteTo(writer: writer);
    }
}
/// <summary>Reads and writes an <see cref="ExpressionProgram"/> as the infix text a vocabulary spells it in
/// (<c>puck.cartridge.v1</c>), through <see cref="ExpressionSpelling"/>.</summary>
/// <remarks>A vocabulary that spells programs as text names this converter on each program-valued member, which
/// outranks the type's own IR converter. Every program is written in the canonical spelling, so two documents
/// whose programs differ only in spelling canonicalize to the same bytes.</remarks>
public sealed class ExpressionSpellingJsonConverter : JsonConverter<ExpressionProgram>, IJsonSchemaStringConverter {
    /// <inheritdoc/>
    public IReadOnlyList<string>? SchemaTokens => null;

    /// <inheritdoc/>
    public override ExpressionProgram? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        if (reader.TokenType != JsonTokenType.String) {
            throw new JsonException(message: "an expression is an infix string (\"a * 2 - b\")");
        }
        var text = reader.GetString()!;

        if (!ExpressionSpelling.TryParse(
            error: out var error,
            program: out var program,
            text: text
        )) {
            throw new JsonException(message: $"expression \"{text}\" {error}");
        }

        return program;
    }
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, ExpressionProgram value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: value);
        ArgumentNullException.ThrowIfNull(argument: writer);

        writer.WriteStringValue(value: ExpressionSpelling.Print(program: value));
    }
}
