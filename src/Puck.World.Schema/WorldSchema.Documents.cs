using System.Text.Json;
using System.Text.Json.Nodes;

namespace Puck.World;

public static partial class WorldSchema {
    /// <summary>Exports the JSON Schema of a document family another assembly's serializer carries, as one unsplit
    /// document with the same identity block, descriptions and nested-shape handling as the world's own documents. The
    /// document's tag member, <c>$schema</c> or <c>schema</c>, is pinned to <paramref name="schemaId"/>.</summary>
    /// <param name="options">The source-generated serializer options that read the document.</param>
    /// <param name="type">The document's root type.</param>
    /// <param name="schemaId">The schema's identity, the tag the document carries.</param>
    /// <param name="title">The schema's title.</param>
    /// <param name="documented">A type from each further assembly whose types the document embeds, so that assembly's
    /// XML documentation file is read too, or <see langword="null"/> when <paramref name="type"/>'s assembly declares
    /// them all.</param>
    /// <returns>The generated schema root. Descriptions are read from the XML documentation file of
    /// <paramref name="type"/>'s assembly and of each <paramref name="documented"/> type's, beside the world's own; when a
    /// file is missing, every description is omitted, as <see cref="HasXmlDocumentation"/> reports for the world's
    /// files.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static JsonObject ExportDocument(JsonSerializerOptions options, Type type, string schemaId, string title, IReadOnlyList<Type>? documented = null) {
        ArgumentNullException.ThrowIfNull(argument: options);
        ArgumentNullException.ThrowIfNull(argument: type);
        ArgumentNullException.ThrowIfNull(argument: schemaId);
        ArgumentNullException.ThrowIfNull(argument: title);

        var root = ExportDocument(
            index: LoadXmlDocIndex(files: [
                .. XmlDocumentationFiles,
                ($"{type.Assembly.GetName().Name}.xml", type),
                .. (documented ?? []).Select(selector: static anchor => ($"{anchor.Assembly.GetName().Name}.xml", anchor)),
            ]),
            options: options,
            schemaId: schemaId,
            title: title,
            type: type
        );

        if (root["properties"]?["$schema"] is JsonObject tag) {
            tag["const"] = schemaId;
        }

        return root;
    }
}
