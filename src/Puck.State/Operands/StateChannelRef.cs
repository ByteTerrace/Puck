using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.State;

/// <summary>A document's one reference to a state row, a cell key, a pool field, or a reserved channel. A live zone
/// (<c>$zones[…]</c>) is a call whose one argument is a <see cref="ChannelArgument.Zone"/>.</summary>
/// <remarks>A consumer dispatches on <see cref="Call"/> or <see cref="PoolField"/> without parsing text. The implicit
/// conversion from <see cref="string"/> retains the plain-name and reserved-channel authoring convenience;
/// structurally typed pool fields are created through <see cref="OfBindingField"/> or
/// <see cref="OfStaticPoolField"/>. <see cref="Spelling"/> supplies the canonical diagnostic and source spelling.</remarks>
[JsonConverter(typeof(StateChannelRefJsonConverter))]
public sealed record StateChannelRef {
    private StateChannelRef(string? name, ChannelCall? call, StatePoolFieldRef? poolField = null) {
        Name = name;
        Call = call;
        PoolField = poolField;
    }

    /// <summary>Gets the plain name — a row name or a literal key — or <see langword="null"/> when this reference is
    /// a call or pool field.</summary>
    public string? Name { get; }
    /// <summary>Gets the reserved-channel call, live zone included, or <see langword="null"/> when this reference is
    /// a plain name.</summary>
    public ChannelCall? Call { get; }
    /// <summary>Gets the structurally typed pool-field reference, or <see langword="null"/>.</summary>
    public StatePoolFieldRef? PoolField { get; }
    /// <summary>Gets the canonical spelling of the plain name, call, or pool field.</summary>
    public string Spelling => (PoolField switch {
        { Binding: { } binding } poolField => $"{binding}.{poolField.Field}",
        { Pool: { } pool, Slot: { } slot } poolField => $"{pool}[{slot}].{poolField.Field}",
        _ => (Call switch {
        { Channel: "zones", Arguments: [ChannelArgument.Zone zone] } => $"{RuleFacts.LiveZonePrefix}{zone.Index}]",
        { } call => ChannelSpelling.Print(call: call),
            null => Name!,
        }),
    });

    /// <summary>Wraps a plain name.</summary>
    /// <param name="name">The name.</param>
    /// <returns>The reference.</returns>
    public static StateChannelRef OfName(string name) {
        ArgumentNullException.ThrowIfNull(argument: name);

        return new StateChannelRef(
            call: null,
            name: name,
            poolField: null
        );
    }
    /// <summary>Wraps a call.</summary>
    /// <param name="call">The call.</param>
    /// <returns>The reference.</returns>
    public static StateChannelRef OfCall(ChannelCall call) {
        ArgumentNullException.ThrowIfNull(argument: call);

        return new StateChannelRef(
            call: call,
            name: null,
            poolField: null
        );
    }
    /// <summary>Wraps a lexical pool binding's field.</summary>
    /// <param name="binding">The lexical binding.</param>
    /// <param name="field">The record field.</param>
    /// <returns>The typed reference.</returns>
    public static StateChannelRef OfBindingField(string binding, string field) => new(name: null, call: null, poolField: new StatePoolFieldRef(Binding: binding, Field: field));
    /// <summary>Wraps a statically addressed pool slot's field.</summary>
    /// <param name="pool">The pool.</param>
    /// <param name="slot">The static slot.</param>
    /// <param name="field">The record field.</param>
    /// <returns>The typed reference.</returns>
    public static StateChannelRef OfStaticPoolField(string pool, int slot, string field) => new(name: null, call: null, poolField: new StatePoolFieldRef(Field: field, Pool: pool, Slot: slot));
    /// <summary>Parses a colon spelling: a reserved channel or a live zone becomes <see cref="Call"/>, and anything
    /// else becomes <see cref="Name"/>.</summary>
    /// <param name="spelling">The spelling.</param>
    /// <returns>The reference.</returns>
    public static StateChannelRef Parse(string spelling) {
        ArgumentNullException.ThrowIfNull(argument: spelling);

        if (ChannelSpelling.TryParse(
            call: out var call,
            text: spelling
        )) {
            return OfCall(call: call);
        }
        if (
            spelling.StartsWith(
                comparisonType: StringComparison.Ordinal,
                value: RuleFacts.LiveZonePrefix
            ) &&
            spelling.EndsWith(value: ']')
        ) {
            return OfCall(call: new ChannelCall(
                Arguments: [new ChannelArgument.Zone(Index: spelling[RuleFacts.LiveZonePrefix.Length..^1])],
                Channel: "zones"
            ));
        }

        return OfName(name: spelling);
    }
    /// <summary>Parses a nullable spelling into a reference.</summary>
    /// <param name="spelling">The spelling, or <see langword="null"/>.</param>
    /// <returns>The reference, or <see langword="null"/> when <paramref name="spelling"/> is <see langword="null"/>.</returns>
    public static StateChannelRef? OfNullable(string? spelling) => ((spelling is null)
        ? null
        : Parse(spelling: spelling)
    );

    /// <summary>Parses a spelling into a reference.</summary>
    /// <param name="spelling">The spelling.</param>
    public static implicit operator StateChannelRef(string spelling) => Parse(spelling: spelling);

    /// <inheritdoc/>
    public override string ToString() => Spelling;
}
/// <summary>A pool field selected through a lexical binding or a static pool slot.</summary>
/// <param name="Field">The record field.</param>
/// <param name="Binding">The lexical binding, when dynamically selected.</param>
/// <param name="Pool">The pool, when statically selected.</param>
/// <param name="Slot">The slot, when statically selected.</param>
public sealed record StatePoolFieldRef(string Field, string? Binding = null, string? Pool = null, int? Slot = null);
/// <summary>Reads and writes a <see cref="StateChannelRef"/>: a plain name as a JSON string; a call as
/// <c>{ "channel": "board", "arguments": [ … ] }</c>; a lexical pool field as
/// <c>{ "binding": "piece", "field": "rank" }</c>; and a static pool field as
/// <c>{ "pool": "pieces", "slot": 0, "field": "rank" }</c>.</summary>
public sealed class StateChannelRefJsonConverter : JsonConverter<StateChannelRef>, IJsonSchemaNodeConverter {
    private static JsonNode ArgumentNode(ChannelArgument argument) => (argument switch {
        ChannelArgument.Word word => word.Text,
        ChannelArgument.Number number => number.Value,
        ChannelArgument.Zone zone => new JsonObject { ["zone"] = ExpressionNode(infix: zone.Index) },
        ChannelArgument.Expression expression => new JsonObject { ["expression"] = ExpressionNode(infix: expression.Infix) },
        _ => throw new JsonException(message: "a channel argument is a word, a number, a zone, or an expression"),
    });
    private static JsonNode ExpressionNode(string infix) => ExpressionProgramJsonConverter.ToNode(program: ExpressionProgram.Parse(text: infix));
    private static string ExpressionInfix(JsonNode? node) {
        if (!ExpressionSpelling.TryPrint(
            program: ExpressionProgramJsonConverter.FromNode(node: node),
            text: out var infix
        )) {
            throw new JsonException(message: "a channel argument's expression does not print back to infix text");
        }

        return infix;
    }
    private static ChannelArgument ReadArgument(JsonNode? node) {
        switch (node) {
            case JsonValue value when value.TryGetValue<string>(value: out var text):
                return new ChannelArgument.Word(Text: text);
            // A number built in memory holds the CLR type it was built from, which reads back only as that type, so
            // the digits it writes are what is read.
            case JsonValue value when (value.GetValueKind() == JsonValueKind.Number):
                if (!decimal.TryParse(
                    provider: CultureInfo.InvariantCulture,
                    s: value.ToJsonString(),
                    style: NumberStyles.Float,
                    result: out var number
                )) {
                    throw new JsonException(message: "a channel's numeric argument must fit Decimal");
                }

                return new ChannelArgument.Number(Value: number);
            case JsonObject { Count: 1 } obj when (obj["zone"] is { } zone):
                return new ChannelArgument.Zone(Index: ExpressionInfix(node: zone));
            case JsonObject { Count: 1 } obj when (obj["expression"] is { } expression):
                return new ChannelArgument.Expression(Infix: ExpressionInfix(node: expression));
            default:
                throw new JsonException(message: "a channel argument is a string, a number, a zone, or an expression");
        }
    }

    /// <inheritdoc/>
    public JsonObject BuildSchema(Func<Type, JsonNode> exportType) {
        ArgumentNullException.ThrowIfNull(argument: exportType);

        return new JsonObject {
            ["title"] = "StateChannelRef",
            ["description"] = "A row name or literal key as plain text, or an object identifying a reserved channel, lexical pool field, or static pool field.",
            ["anyOf"] = new JsonArray(
                new JsonObject { ["type"] = "string" },
                new JsonObject {
                    ["type"] = "object",
                    ["required"] = new JsonArray("channel"),
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject {
                        ["channel"] = new JsonObject { ["type"] = "string" },
                        ["arguments"] = new JsonObject {
                            ["type"] = "array",
                            ["items"] = new JsonObject {
                                ["anyOf"] = new JsonArray(
                                    new JsonObject { ["type"] = "string" },
                                    new JsonObject { ["type"] = "number" },
                                    new JsonObject {
                                        ["type"] = "object",
                                        ["required"] = new JsonArray("zone"),
                                        ["additionalProperties"] = false,
                                        ["properties"] = new JsonObject { ["zone"] = exportType(typeof(ExpressionProgram)) },
                                    },
                                    new JsonObject {
                                        ["type"] = "object",
                                        ["required"] = new JsonArray("expression"),
                                        ["additionalProperties"] = false,
                                        ["properties"] = new JsonObject { ["expression"] = exportType(typeof(ExpressionProgram)) },
                                    }
                                ),
                            },
                        },
                    },
                },
                new JsonObject {
                    ["type"] = "object",
                    ["required"] = new JsonArray("binding", "field"),
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["binding"] = new JsonObject { ["type"] = "string" }, ["field"] = new JsonObject { ["type"] = "string" } },
                },
                new JsonObject {
                    ["type"] = "object",
                    ["required"] = new JsonArray("pool", "slot", "field"),
                    ["additionalProperties"] = false,
                    ["properties"] = new JsonObject { ["pool"] = new JsonObject { ["type"] = "string" }, ["slot"] = new JsonObject { ["type"] = "integer" }, ["field"] = new JsonObject { ["type"] = "string" } },
                }
            ),
        };
    }
    /// <summary>Reads a reference from the converted-member IR tree: a plain name as a JSON string (refusing one that
    /// spells a reserved channel), and a call as the object shape <see cref="Write"/> documents. A document emitter
    /// or decompiler that builds or reads <see cref="JsonNode"/> trees directly (rather than serializing a
    /// <see cref="StateChannelRef"/> instance) calls this instead of duplicating the shape.</summary>
    /// <param name="node">The tree.</param>
    /// <returns>The reference.</returns>
    /// <exception cref="JsonException">The tree is not a string or the call-object shape, or a string spells a
    /// reserved channel, an argument has an invalid shape, or a number exceeds the Decimal range.</exception>
    public static StateChannelRef FromNode(JsonNode? node) {
        if ((node is JsonValue value) && value.TryGetValue<string>(value: out var text)) {
            // A live zone is a call as much as a channel is, so the test is what the text parses to.
            if (StateChannelRef.Parse(spelling: text).Call is { } spelled) {
                throw new JsonException(message: $"'{text}' spells a reserved channel as text — write it as {{ \"channel\": \"{spelled.Channel}\", … }}");
            }

            return StateChannelRef.OfName(name: text);
        }
        if (node is not JsonObject obj) {
            throw new JsonException(message: "a channel reference is a string or an object carrying 'channel'");
        }
        if ((obj["binding"] is JsonValue bindingValue) && bindingValue.TryGetValue<string>(value: out var binding) && (obj["field"] is JsonValue bindingFieldValue) && bindingFieldValue.TryGetValue<string>(value: out var bindingField) && (obj.Count == 2)) {
            return StateChannelRef.OfBindingField(binding: binding, field: bindingField);
        }
        if ((obj["pool"] is JsonValue poolValue) && poolValue.TryGetValue<string>(value: out var pool) && (obj["slot"] is JsonValue slotValue) && slotValue.TryGetValue<int>(value: out var slot) && (obj["field"] is JsonValue staticFieldValue) && staticFieldValue.TryGetValue<string>(value: out var staticField) && (obj.Count == 3)) {
            return StateChannelRef.OfStaticPoolField(field: staticField, pool: pool, slot: slot);
        }
        if ((obj["channel"] is not JsonValue channelValue) || !channelValue.TryGetValue<string>(value: out var channel)) {
            throw new JsonException(message: "a channel reference object carries 'channel'");
        }

        foreach (var (member, _) in obj) {
            if (member is not ("channel" or "arguments")) {
                throw new JsonException(message: $"a channel reference carries no member '{member}'");
            }
        }

        var arguments = new List<ChannelArgument>();

        if (obj.TryGetPropertyValue(jsonNode: out var argumentsNode, propertyName: "arguments")) {
            if (argumentsNode is not JsonArray declared) {
                throw new JsonException(message: "a channel reference's 'arguments' member must be an array");
            }

            foreach (var argument in declared) {
                arguments.Add(item: ReadArgument(node: argument));
            }
        }

        return StateChannelRef.OfCall(call: new ChannelCall(
            Arguments: arguments,
            Channel: channel
        ));
    }
    /// <summary>Renders a reference as the converted-member IR tree: a plain name as a JSON string, and a call as
    /// <see cref="Write"/> documents. A document emitter that builds <see cref="JsonNode"/> trees directly calls
    /// this instead of duplicating the shape.</summary>
    /// <param name="value">The reference.</param>
    /// <returns>The tree.</returns>
    public static JsonNode ToNode(StateChannelRef value) {
        ArgumentNullException.ThrowIfNull(argument: value);

        if (value.PoolField is { Binding: { } binding } bindingField) {
            return new JsonObject { ["binding"] = binding, ["field"] = bindingField.Field };
        }
        if (value.PoolField is { Pool: { } pool, Slot: { } slot } staticField) {
            return new JsonObject { ["pool"] = pool, ["slot"] = slot, ["field"] = staticField.Field };
        }
        if (value.Call is not { } call) {
            return JsonValue.Create(value: value.Name)!;
        }

        var node = new JsonObject { ["channel"] = call.Channel };

        if (call.Count > 0) {
            node["arguments"] = new JsonArray([.. call.Arguments.Select(selector: ArgumentNode)]);
        }

        return node;
    }
    /// <inheritdoc/>
    public override StateChannelRef? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FromNode(node: JsonNode.Parse(reader: ref reader));
    /// <inheritdoc/>
    public override void Write(Utf8JsonWriter writer, StateChannelRef value, JsonSerializerOptions options) {
        ArgumentNullException.ThrowIfNull(argument: writer);

        ToNode(value: value).WriteTo(writer: writer);
    }
}
