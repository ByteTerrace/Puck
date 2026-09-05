using System.Text.Json.Nodes;

namespace Puck.Abstractions.Documents;

/// <summary>Describes the whole JSON Schema node for a custom System.Text.Json converter whose wire form is more
/// than a primitive type or a token list — a value that is either a string or an object, say — so a schema exporter
/// can restore what converter metadata otherwise hides.</summary>
public interface IJsonSchemaNodeConverter {
    /// <summary>Builds the schema node for the converted type.</summary>
    /// <param name="exportType">Exports another type's schema through the same exporter, so an object arm can
    /// reference the shape the converter itself deserializes.</param>
    /// <returns>The schema node; its members replace the exporter's unconstrained node.</returns>
    JsonObject BuildSchema(Func<Type, JsonNode> exportType);
}
