using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>A converter whose value is a whole type graph read through serializer options of its own (the creation
/// document a placement embeds): the world schema exports that graph with those options through its own transform, so
/// every shape in it is titled, typed, described and hoisted as the world's own shapes are.</summary>
internal interface IJsonSchemaGraphConverter : IJsonSchemaNodeConverter {
    /// <summary>Gets the serializer options the graph is read with.</summary>
    JsonSerializerOptions SchemaOptions { get; }
    /// <summary>Gets the graph's root type.</summary>
    Type SchemaType { get; }
    /// <summary>Gets the <c>$id</c> the graph's schema carries when exported on its own.</summary>
    string SchemaId { get; }

    /// <summary>Corrects one exported node for what the graph's options mean that the exporter cannot see, before the
    /// world schema's own transform runs on it.</summary>
    /// <param name="context">The exporter's context for the node.</param>
    /// <param name="node">The node as the exporter produced it.</param>
    /// <returns>The corrected node.</returns>
    JsonNode PrepareSchemaNode(JsonSchemaExporterContext context, JsonNode node);
}
