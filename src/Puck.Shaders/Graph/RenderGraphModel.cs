using System.Text.Json;
using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>The document schema of a frame graph.</summary>
public static class RenderGraphSchemas {
    /// <summary>The current frame-graph document schema.</summary>
    public const string Graph = "puck.render.graph.v1";
}
/// <summary>One pass of engine work a graph names by package instead of by shader source: SDF rendering, the overlay,
/// or a shipped post-process set. The package decides how it records its work; the graph decides which versions it
/// reads and writes, and the planner orders it among the shader passes by those versions.</summary>
/// <param name="Name">The pass's unique name, shared with the shader passes.</param>
/// <param name="Package">The package id, which the host's <see cref="RenderGraphPackageCatalog"/> declares.</param>
/// <param name="Inputs">The versions the package reads, one per input port in port order. A reference may read a
/// history version's previous frame. A package binds its own descriptors, so no reference declares a binding.</param>
/// <param name="Outputs">The image versions the package writes, one per output port in port order.</param>
public sealed record RenderGraphPackagePass(
    string Name,
    string Package,
    IReadOnlyList<ResourceReference>? Inputs = null,
    IReadOnlyList<ResourceReference>? Outputs = null
) {
    /// <summary>Gets an empty list when the pass reads nothing.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> InputReferences => (Inputs ?? []);
    /// <summary>Gets an empty list when the pass writes nothing.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> OutputReferences => (Outputs ?? []);
}
/// <summary>A frame as a document: passes connected by named image and buffer versions, planned by the one pipeline
/// planner (<see cref="ShaderPipelineCompiler"/>). Every view renders an instance of a graph, and a version declared
/// <see cref="ShaderPipelineInitialization.External"/> is an input the host binds, such as another instance's output.
/// <para>
/// Its members are the pipeline document's members, with the same shapes, plus <see cref="Packages"/>: a
/// <c>puck.shader.pipeline.v1</c> document's content is a graph with no packages, so a pipeline is a graph a world
/// names.
/// </para>
/// </summary>
/// <param name="Schema">The schema tag, <c>puck.render.graph.v1</c>.</param>
/// <param name="Name">The graph name.</param>
/// <param name="Resources">The versions, as a pipeline declares them.</param>
/// <param name="Outputs">The public versions; the first is what a consumer reads by default.</param>
/// <param name="Passes">The shader passes, as a pipeline declares them, or <see langword="null"/> for none.</param>
/// <param name="Packages">The engine-package passes, or <see langword="null"/> for none.</param>
[method: JsonConstructor]
public sealed record RenderGraphDefinition(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    IReadOnlyList<ShaderPipelineResource> Resources,
    IReadOnlyList<string> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShaderPipelinePass>? Passes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RenderGraphPackagePass>? Packages = null
) {
    /// <summary>Gets an empty list when the graph declares no shader passes.</summary>
    [JsonIgnore]
    public IReadOnlyList<ShaderPipelinePass> ShaderPasses => (Passes ?? []);
    /// <summary>Gets an empty list when the graph declares no package passes.</summary>
    [JsonIgnore]
    public IReadOnlyList<RenderGraphPackagePass> PackagePasses => (Packages ?? []);

    /// <summary>Parses a graph document.</summary>
    /// <param name="json">The document text.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="JsonException">The text is not a graph document, or names a member the document does not
    /// declare.</exception>
    /// <exception cref="InvalidDataException">The document is <c>null</c>.</exception>
    public static RenderGraphDefinition Parse(string json) {
        ArgumentNullException.ThrowIfNull(argument: json);

        return (JsonSerializer.Deserialize(
            json: json,
            jsonTypeInfo: RenderGraphJsonContext.Default.RenderGraphDefinition
        ) ?? throw new InvalidDataException(message: "The graph document is null."));
    }
}
/// <summary>Source-generated metadata for <see cref="RenderGraphDefinition"/>, configured as
/// <see cref="ShaderPipelineJsonContext"/> is, so a pipeline document's members read the same in both.</summary>
[JsonSerializable(typeof(RenderGraphDefinition))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class RenderGraphJsonContext : JsonSerializerContext {
}
