using System.Text.Json;
using System.Text.Json.Nodes;

using Json.Schema;

namespace Puck.World.Schema.Tests;

/// <summary>Builds JSON Schemas and reads their verdicts for the schema laws.</summary>
internal static class SchemaVerdicts {
    /// <summary>Builds <paramref name="schema"/> into a registry of its own, as Draft 2020-12 unless the schema
    /// names its own <c>$schema</c>.</summary>
    /// <remarks>Draft 2020-12 is the dialect <see cref="WorldSchema"/> declares; a fragment a law builds without
    /// <c>$schema</c> would otherwise take the library's default dialect, which refuses the generator's
    /// <c>x-</c> annotation keywords. A shared registry refuses a second registration of the same base URI, and
    /// the laws build the same bundle, or bundles carrying the same embedded <c>$id</c>, more than once.</remarks>
    public static JsonSchema Build(JsonNode schema) =>
        JsonSchema.FromText(
            buildOptions: new BuildOptions {
                Dialect = Dialect.Draft202012,
                SchemaRegistry = new SchemaRegistry(),
            },
            jsonText: schema.ToJsonString()
        );
    /// <summary>Returns a value indicating whether <paramref name="schema"/> admits <paramref name="instance"/>.</summary>
    /// <remarks>A Flag evaluation collects no annotations or error output, so it is the cheapest route to the
    /// verdict; <see cref="Explain"/> pays for the detail when a law needs it.</remarks>
    public static bool Admits(this JsonSchema schema, JsonNode? instance) =>
        schema.Evaluate(
            instance: ToElement(node: instance),
            options: new EvaluationOptions { OutputFormat = OutputFormat.Flag }
        ).IsValid;
    /// <summary>Returns the List-format evaluation of <paramref name="instance"/>, which names every failing site.</summary>
    public static string Explain(this JsonSchema schema, JsonNode? instance) =>
        JsonSerializer.Serialize(
            options: new JsonSerializerOptions { WriteIndented = true },
            value: schema.Evaluate(
                instance: ToElement(node: instance),
                options: new EvaluationOptions { OutputFormat = OutputFormat.List }
            )
        );

    private static JsonElement ToElement(JsonNode? node) {
        using var document = JsonDocument.Parse(json: (node?.ToJsonString() ?? "null"));

        return document.RootElement.Clone();
    }
}
