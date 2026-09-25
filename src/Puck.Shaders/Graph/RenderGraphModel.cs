using System.Text.Json;
using System.Text.Json.Serialization;
using Puck.Hosting;

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
/// <param name="Inputs">The versions the package reads, one per input port in port order, each carrying what its port
/// carries (<see cref="RenderGraphPackagePort"/>). A reference may read a history version's previous frame. A package
/// binds its own descriptors, so no reference declares a binding.</param>
/// <param name="Outputs">The versions the package writes, one per output port in port order, each carrying what its
/// port carries.</param>
/// <param name="Config">The values of the package's config schema (<see cref="RenderGraphPackage.Config"/>), each
/// absent field at its default, or <see langword="null"/> for every default. They are the pass's frame block config,
/// which the graph compiler binds against the schema and refuses by name when they do not bind.</param>
public sealed record RenderGraphPackagePass(
    string Name,
    string Package,
    IReadOnlyList<ResourceReference>? Inputs = null,
    IReadOnlyList<ResourceReference>? Outputs = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Config = null
) {
    /// <summary>Gets an empty list when the pass reads nothing.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> InputReferences => (Inputs ?? []);
    /// <summary>Gets an empty list when the pass writes nothing.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> OutputReferences => (Outputs ?? []);
}
/// <summary>A frame as a document: passes connected by named image and buffer versions, planned by the one pipeline
/// planner (<see cref="ShaderPipelineCompiler"/>). It is the one document a pass graph is written in. A pipeline is a
/// graph a world names, whose passes are all shader passes, and a lone <c>.hlsl</c> source reads as the one-pass graph
/// <see cref="FromShaderSource"/> makes. Every view renders an instance of a graph, and a version declared
/// <see cref="ShaderPipelineInitialization.External"/> is an input the host binds, such as another instance's
/// output.</summary>
/// <param name="Schema">The schema tag, <c>puck.render.graph.v1</c>.</param>
/// <param name="Name">The graph name.</param>
/// <param name="Resources">The versions.</param>
/// <param name="Outputs">The public versions, each named by its version name; the first is what a consumer reads by
/// default.</param>
/// <param name="Passes">The shader passes, in any order, or <see langword="null"/> for none; the planner orders
/// them.</param>
/// <param name="Packages">The engine-package passes, or <see langword="null"/> for none. Only a host that offers packages
/// plans them (<see cref="RenderGraphCompiler"/>); a pipeline instance runs shader passes alone.</param>
[method: JsonConstructor]
public sealed record RenderGraphDefinition(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    IReadOnlyList<ShaderPipelineResource> Resources,
    IReadOnlyList<string> Outputs,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ShaderPipelinePass>? Passes = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<RenderGraphPackagePass>? Packages = null
) {
    /// <summary>Initializes a graph using <see cref="RenderGraphSchemas.Graph"/>.</summary>
    /// <param name="name">The graph name.</param>
    /// <param name="resources">The resource versions.</param>
    /// <param name="passes">The shader passes, in any order; the planner orders them.</param>
    /// <param name="outputs">The public versions, each named by its version name; the first is published by
    /// default.</param>
    /// <param name="packages">The engine-package passes, or <see langword="null"/> for none.</param>
    public RenderGraphDefinition(
        string name,
        IReadOnlyList<ShaderPipelineResource> resources,
        IReadOnlyList<ShaderPipelinePass> passes,
        IReadOnlyList<string> outputs,
        IReadOnlyList<RenderGraphPackagePass>? packages = null
    ) : this(
        Name: name,
        Outputs: outputs,
        Packages: packages,
        Passes: passes,
        Resources: resources,
        Schema: RenderGraphSchemas.Graph
    ) { }

    /// <summary>Gets an empty list when the graph declares no shader passes.</summary>
    [JsonIgnore]
    public IReadOnlyList<ShaderPipelinePass> ShaderPasses => (Passes ?? []);
    /// <summary>Gets an empty list when the graph declares no package passes.</summary>
    [JsonIgnore]
    public IReadOnlyList<RenderGraphPackagePass> PackagePasses => (Packages ?? []);

    /// <summary>Creates the one-pass graph a lone shader source reads as: one pass writing one frame-relative
    /// <c>R8G8B8A8Unorm</c> image named <c>output</c>, its one public version.</summary>
    /// <param name="name">The graph name, which also names its pass.</param>
    /// <param name="sourcePath">The shader source path.</param>
    /// <param name="kind">The pass kind; when omitted, the pass is a compute pass.</param>
    /// <param name="entryPoint">The compiler entry point.</param>
    /// <returns>The graph.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> or <paramref name="sourcePath"/> is empty, or the source
    /// is not an <c>.hlsl</c> file.</exception>
    public static RenderGraphDefinition FromShaderSource(
        string name,
        string sourcePath,
        ShaderPipelineDocumentPassKind? kind = null,
        string entryPoint = "main"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        var extension = Path.GetExtension(path: sourcePath);

        if (!extension.Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".hlsl"
        )) {
            throw new ArgumentException(
                message: $"Shader source extension '{extension}' is unsupported; a one-off shader is an .hlsl file.",
                paramName: nameof(sourcePath)
            );
        }
        var output = new ShaderPipelineResource(
            Name: "output",
            Kind: ShaderPipelineResourceKind.Image,
            Format: "R8G8B8A8Unorm",
            Dimensions: ShaderPipelineDimensions.Relative()
        );
        var pass = new ShaderPipelinePass(
            Name: name,
            Source: Path.GetFullPath(path: sourcePath),
            EntryPoint: entryPoint,
            Kind: (kind ?? ShaderPipelineDocumentPassKind.Compute),
            Outputs: [new ResourceReference(Name: output.Name)]
        );

        return new RenderGraphDefinition(
            name: name,
            outputs: [output.Name],
            passes: [pass],
            resources: [output]
        );
    }
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
/// <summary>Source-generated metadata for <see cref="RenderGraphDefinition"/> and the members it is made of. A member the
/// document does not declare is refused.</summary>
[JsonSerializable(typeof(RenderGraphDefinition))]
[JsonSerializable(typeof(ShaderPipelineResource))]
[JsonSerializable(typeof(ShaderPipelinePass))]
[JsonSerializable(typeof(ResourceReference))]
[JsonSerializable(typeof(ShaderPipelineGeometry))]
[JsonSerializable(typeof(ShaderPipelineVertexAttribute))]
[JsonSerializable(typeof(ShaderPipelineDispatch))]
[JsonSerializable(typeof(ShaderPipelineCountTerm))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class RenderGraphJsonContext : JsonSerializerContext {
}
