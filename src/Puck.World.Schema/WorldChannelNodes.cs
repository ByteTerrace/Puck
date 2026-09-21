using System.Text.Json.Nodes;

namespace Puck.World;

/// <summary>Visits one registered name-bearing site of a raw document tree.</summary>
/// <param name="holder">The object holding the member.</param>
/// <param name="jsonName">The member's JSON name.</param>
/// <param name="value">The member's value.</param>
/// <param name="field">The member's registration.</param>
/// <param name="memberType">The type the document model gives the member.</param>
public delegate void WorldNameVisitor(JsonObject holder, string jsonName, JsonNode value, WorldNameField field, Type memberType);

/// <summary>The one crossing between a document's <see cref="StateChannelRef"/> members and the colon spelling a
/// lowering or a decompile works in: a document holds a reserved channel as a call node, and text that builds or
/// prints a document holds it as <see cref="StateChannelRef.Spelling"/>.</summary>
/// <remarks>Both directions run over <see cref="WorldModuleNamespace.Visit"/>, so they reach exactly the members the
/// document model types as a <see cref="StateChannelRef"/> and no member list is kept beside the model. An
/// expression's operands are the exception: <see cref="ExpressionProgramJsonConverter"/> reads and writes a program
/// whole, nodes included, so neither direction touches one. <see cref="ChannelSpelling"/> reads and writes both forms,
/// so lower-then-raise is the identity over every reference a document can hold.</remarks>
public static class WorldChannelNodes {
    private static bool Holds(WorldNameField field, Type memberType) => (
        ((Nullable.GetUnderlyingType(nullableType: memberType) ?? memberType) == typeof(StateChannelRef)) &&
        (field.Owner != typeof(InstructionPayload.State)) &&
        (field.Owner != typeof(VectorOperand.Cell))
    );

    /// <summary>Returns the value a visitor reads at a site as text: the colon spelling of a call node a
    /// <see cref="StateChannelRef"/> member holds, and every other value as it is.</summary>
    /// <param name="value">The site's value.</param>
    /// <param name="memberType">The type the document model gives the member.</param>
    /// <returns>A string value holding the spelling, or <paramref name="value"/> itself.</returns>
    /// <exception cref="System.Text.Json.JsonException">The member holds an object that is not a valid state
    /// reference.</exception>
    public static JsonNode Spelled(JsonNode value, Type memberType) {
        ArgumentNullException.ThrowIfNull(argument: memberType);
        ArgumentNullException.ThrowIfNull(argument: value);

        return (((value is JsonObject) && ((Nullable.GetUnderlyingType(nullableType: memberType) ?? memberType) == typeof(StateChannelRef)))
            ? JsonValue.Create(value: StateChannelRefJsonConverter.FromNode(node: value).Spelling)!
            : value
        );
    }
    /// <summary>Returns the node a document holds for a reference's colon spelling: a string for a plain name, a call
    /// node for a reserved channel.</summary>
    /// <param name="spelling">The colon spelling.</param>
    /// <returns>The node.</returns>
    public static JsonNode Node(string spelling) => StateChannelRefJsonConverter.ToNode(value: StateChannelRef.Parse(spelling: spelling));
    /// <summary>Rewrites every <see cref="StateChannelRef"/> member that still holds its colon spelling into the node
    /// the document holds, in place. A plain name stays the string it is.</summary>
    /// <param name="document">The lowered document, or a subtree of one.</param>
    /// <param name="type">The model type <paramref name="document"/> holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    public static void Lower(JsonNode? document, Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        WorldModuleNamespace.Visit(
            node: document,
            type: type,
            visitor: static (holder, jsonName, value, field, memberType) => {
                if (
                    Holds(field: field, memberType: memberType) &&
                    (value is JsonValue leaf) &&
                    leaf.TryGetValue<string>(value: out var spelling)
                ) {
                    holder[propertyName: jsonName] = Node(spelling: spelling);
                }
            }
        );
    }
    /// <summary>Rewrites every <see cref="StateChannelRef"/> member that holds a reserved call node into its colon
    /// spelling, in place: the form a decompile prints from and a name rewrite reads. Typed pool fields stay
    /// structural.</summary>
    /// <param name="document">The document, or a subtree of one.</param>
    /// <param name="type">The model type <paramref name="document"/> holds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="type"/> is <see langword="null"/>.</exception>
    /// <exception cref="System.Text.Json.JsonException">A member holds an object that is not a valid state
    /// reference.</exception>
    public static void Raise(JsonNode? document, Type type) {
        ArgumentNullException.ThrowIfNull(argument: type);

        WorldModuleNamespace.Visit(
            node: document,
            type: type,
            visitor: static (holder, jsonName, value, field, memberType) => {
                if (
                    Holds(field: field, memberType: memberType) &&
                    (value is JsonObject)
                ) {
                    var reference = StateChannelRefJsonConverter.FromNode(node: value);

                    if (reference.Call is not null) {
                        holder[propertyName: jsonName] = reference.Spelling;
                    }
                }
            }
        );
    }
}
