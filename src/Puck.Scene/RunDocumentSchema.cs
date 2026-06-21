using System.Text.Json;
using System.Text.Json.Schema;

namespace Puck.Scene;

/// <summary>
/// Emits the JSON Schema for <see cref="PuckRunDocument"/> via .NET's <see cref="JsonSchemaExporter"/>, driven by the
/// SAME source-generation options used to read documents — so the schema's STRUCTURE (properties, polymorphic
/// discriminators, required shape) tracks the types that actually bind. A tools verb writes the result to
/// <c>schema/run.schema.json</c> for editor IntelliSense and a CI drift gate; keeping the export here (rather than in
/// the reflection-free tools app) means the tools app never touches the serializer's reflection paths.
/// <para>One known looseness: the schema lists each enum's canonical PascalCase names (the recommended authoring form),
/// but the BCL string-enum converter additionally accepts case-insensitive and integer forms at parse time. The schema
/// is therefore the canonical contract, marginally stricter than the tolerant runtime — not an exact mirror of it.</para>
/// </summary>
public static class RunDocumentSchema {
    /// <summary>Exports the run-document JSON Schema as an indented JSON string.</summary>
    /// <returns>The schema document text.</returns>
    public static string Export() {
        var schema = RunDocument.Options.GetJsonSchemaAsNode(type: typeof(PuckRunDocument));

        return schema.ToJsonString(options: new JsonSerializerOptions {
            WriteIndented = true,
        });
    }
}
