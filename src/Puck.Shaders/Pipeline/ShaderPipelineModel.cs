using System.Collections.ObjectModel;
using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.Shaders;

/// <summary>The document schema understood by the shader-pipeline planner.</summary>
public static class ShaderPipelineSchemas {
    /// <summary>The current shader-pipeline document schema.</summary>
    public const string Pipeline = "puck.shader.pipeline.v1";
}

/// <summary>Whether an image's dimensions are tied to the output extent or fixed.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineDimensionMode>))]
public enum ShaderPipelineDimensionMode {
    /// <summary>Dimensions are a scale of the frame extent.</summary>
    Relative,
    /// <summary>Dimensions are absolute pixels.</summary>
    Absolute,
}

/// <summary>How a resource is made valid before its first read.</summary>
[JsonConverter(typeof(StrictEnumConverter<ShaderPipelineInitialization>))]
public enum ShaderPipelineInitialization {
    /// <summary>No initial contents. A first-frame read is refused.</summary>
    Undefined,
    /// <summary>Clear the resource to zero before the first pass.</summary>
    Zero,
    /// <summary>The resource is supplied by the host.</summary>
    External,
}

/// <summary>Dimensions for an image resource.</summary>
public sealed record ShaderPipelineDimensions(
    ShaderPipelineDimensionMode Mode,
    double Width,
    double Height
) {
    /// <summary>Resolves this declaration against a frame extent, clamping each positive extent to one pixel.</summary>
    public (uint Width, uint Height) Resolve(uint frameWidth, uint frameHeight) {
        ArgumentOutOfRangeException.ThrowIfZero(value: frameWidth);
        ArgumentOutOfRangeException.ThrowIfZero(value: frameHeight);
        var width = (Mode == ShaderPipelineDimensionMode.Relative) ? (frameWidth * Width) : Width;
        var height = (Mode == ShaderPipelineDimensionMode.Relative) ? (frameHeight * Height) : Height;

        return (ResolveExtent(value: width, nameof(Width)), ResolveExtent(value: height, nameof(Height)));
    }

    /// <summary>Creates frame-relative dimensions.</summary>
    public static ShaderPipelineDimensions Relative(double width = 1, double height = 1) =>
        new(Mode: ShaderPipelineDimensionMode.Relative, Width: width, Height: height);

    /// <summary>Creates fixed pixel dimensions.</summary>
    public static ShaderPipelineDimensions Absolute(double width, double height) =>
        new(Mode: ShaderPipelineDimensionMode.Absolute, Width: width, Height: height);

    private static uint ResolveExtent(double value, string name) {
        if (!double.IsFinite(value) || (value <= 0) || (value > uint.MaxValue)) {
            throw new ArgumentOutOfRangeException(name, value, "Resolved shader pipeline extent must be finite, positive, and fit in UInt32.");
        }

        return Math.Max(1u, checked((uint)Math.Round(value, MidpointRounding.ToEven)));
    }
}

/// <summary>A named resource reference in a pass or pipeline output.</summary>
public sealed record ResourceReference(
    string Name,
    bool PreviousFrame = false,
    uint? Binding = null
) {
    /// <summary>Converts the convenient document spelling <c>"name"</c> to a current-frame reference.</summary>
    public static implicit operator ResourceReference(string name) => new(Name: name);
}

/// <summary>One resource declaration in a <see cref="ShaderPipelineDefinition"/>.</summary>
/// <param name="Name">The unique resource name.</param>
/// <param name="Kind">Image, buffer, or depth resource.</param>
/// <param name="Format">The backend-neutral format spelling. Required for images and depth resources.</param>
/// <param name="Dimensions">Image dimensions; omitted for buffers.</param>
/// <param name="Persistent">Keeps the resource across frames.</param>
/// <param name="History">Allows explicit <see cref="ResourceReference.PreviousFrame"/> reads.</param>
/// <param name="Initialization">How the first frame obtains valid contents.</param>
/// <param name="SizeBytes">Buffer capacity, or <see langword="null"/> for images.</param>
/// <param name="ElementType">Optional typed element type for an external or structured buffer.</param>
/// <param name="StrideBytes">Optional byte stride for a structured buffer.</param>
public sealed record ShaderPipelineResource(
    string Name,
    ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image,
    string? Format = null,
    ShaderPipelineDimensions? Dimensions = null,
    bool Persistent = false,
    bool History = false,
    ShaderPipelineInitialization Initialization = ShaderPipelineInitialization.Undefined,
    ulong? SizeBytes = null,
    ShaderValueType? ElementType = null,
    uint? StrideBytes = null
) {
    /// <summary>Gets whether the host supplies the resource rather than a pass producing it.</summary>
    [JsonIgnore]
    public bool IsExternal => Initialization == ShaderPipelineInitialization.External;
}

/// <summary>An authored output name and the resource it exposes.</summary>
public sealed record ShaderPipelineOutput(
    string Name,
    ResourceReference Resource
) {
    /// <summary>Converts <c>"name"</c> to an output with the same public and resource name.</summary>
    public static implicit operator ShaderPipelineOutput(string name) =>
        new(Name: name, Resource: new ResourceReference(Name: name));
}

/// <summary>One executable pass in a shader pipeline.</summary>
/// <param name="Name">The unique pass name.</param>
/// <param name="Source">The shader source or a source/bytecode asset identifier.</param>
/// <param name="Language">The source language, using <see cref="ShaderSourceLanguage"/>.</param>
/// <param name="EntryPoint">The entry point compiled by the shader compiler.</param>
/// <param name="Kind">Compute or fullscreen graphics.</param>
/// <param name="Inputs">Named resource bindings. Set <see cref="ResourceReference.PreviousFrame"/> explicitly for feedback.</param>
/// <param name="Outputs">One or more resource names; multiple names support MRT.</param>
/// <param name="Config">Optional config fields, using the shared shader-set config vocabulary.</param>
/// <param name="GroupSizeX">Compute workgroup width; ignored for fullscreen passes.</param>
/// <param name="GroupSizeY">Compute workgroup height; ignored for fullscreen passes.</param>
/// <param name="GroupSizeZ">Compute workgroup depth; ignored for fullscreen passes.</param>
public sealed record ShaderPipelinePass(
    string Name,
    string Source,
    ShaderSourceLanguage Language,
    string EntryPoint,
    ShaderPipelinePassKind Kind,
    IReadOnlyList<ResourceReference>? Inputs = null,
    IReadOnlyList<ResourceReference>? Outputs = null,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null,
    uint GroupSizeX = 8,
    uint GroupSizeY = 8,
    uint GroupSizeZ = 1
) {
    /// <summary>Gets an immutable empty input list when no resources are read.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> InputReferences => Inputs ?? Array.Empty<ResourceReference>();

    /// <summary>Gets an immutable empty output list when no resources are written.</summary>
    [JsonIgnore]
    public IReadOnlyList<ResourceReference> OutputReferences => Outputs ?? Array.Empty<ResourceReference>();
}

/// <summary>A complete data-authored, multi-pass shader pipeline.</summary>
[method: JsonConstructor]
public sealed record ShaderPipelineDefinition(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    IReadOnlyList<ShaderPipelineResource> Resources,
    IReadOnlyList<ShaderPipelinePass> Passes,
    IReadOnlyList<ShaderPipelineOutput> Outputs,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null
) {
    /// <summary>Initializes a pipeline using <see cref="ShaderPipelineSchemas.Pipeline"/>.</summary>
    public ShaderPipelineDefinition(
        string name,
        IReadOnlyList<ShaderPipelineResource> resources,
        IReadOnlyList<ShaderPipelinePass> passes,
        IReadOnlyList<ShaderPipelineOutput> outputs
    ) : this(Schema: ShaderPipelineSchemas.Pipeline, Name: name, Resources: resources, Passes: passes, Outputs: outputs, Config: null) { }

    /// <summary>The schema tag expected by the planner.</summary>
    public const string SchemaTag = ShaderPipelineSchemas.Pipeline;

    /// <summary>Creates the minimal single-pass definition for a file-backed shader source.</summary>
    /// <param name="name">The pipeline name.</param>
    /// <param name="sourcePath">The shader source path.</param>
    /// <param name="kind">The pass kind.</param>
    /// <param name="entryPoint">The compiler entry point.</param>
    public static ShaderPipelineDefinition FromShaderSource(
        string name,
        string sourcePath,
        ShaderPipelinePassKind kind = ShaderPipelinePassKind.Compute,
        string entryPoint = "main"
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: name);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);
        var output = new ShaderPipelineResource(
            Name: "output",
            Kind: ShaderPipelineResourceKind.Image,
            Format: "R8G8B8A8Unorm",
            Dimensions: ShaderPipelineDimensions.Relative());
        var language = Path.GetExtension(sourcePath).ToLowerInvariant() switch {
            ".hlsl" => ShaderSourceLanguage.Hlsl,
            ".vert" or ".frag" => ShaderSourceLanguage.Glsl,
            ".glsl" => ShaderSourceLanguage.ShadertoyGlsl,
            _ => ShaderSourceLanguage.ShadertoyGlsl,
        };
        if ((language == ShaderSourceLanguage.ShadertoyGlsl) && (entryPoint == "main")) {
            entryPoint = "mainImage";
        }
        var pass = new ShaderPipelinePass(
            Name: name,
            Source: Path.GetFullPath(sourcePath),
            Language: language,
            EntryPoint: entryPoint,
            Kind: kind,
            Outputs: [new ResourceReference(Name: output.Name)]);
        return new ShaderPipelineDefinition(name: name, resources: [output], passes: [pass], outputs: [(ShaderPipelineOutput)"output"]);
    }
}

/// <summary>Limits applied while compiling an execution plan.</summary>
public sealed record ShaderPipelineLimits(
    int MaxResources = 128,
    int MaxPasses = 128,
    int MaxInputsPerPass = 32,
    int MaxOutputsPerPass = 8
);

/// <summary>A planner diagnostic with an actionable code and optional pass/resource name.</summary>
public sealed record ShaderPipelineDiagnostic(
    string Code,
    string Message,
    string? Name = null
);

/// <summary>Thrown when a pipeline cannot be made into a valid immutable execution plan.</summary>
public sealed class ShaderPipelineCompilationException : Exception {
    /// <summary>Initializes an exception with the planner's complete diagnostic set.</summary>
    public ShaderPipelineCompilationException(IReadOnlyList<ShaderPipelineDiagnostic> diagnostics)
        : base(message: string.Join(separator: Environment.NewLine, values: diagnostics.Select(static diagnostic => $"[{diagnostic.Code}] {diagnostic.Message}"))) {
        Diagnostics = new ReadOnlyCollection<ShaderPipelineDiagnostic>(list: diagnostics.ToList());
    }

    /// <summary>Gets all diagnostics collected before planning stopped.</summary>
    public IReadOnlyList<ShaderPipelineDiagnostic> Diagnostics { get; }
}

/// <summary>Source-generated metadata used by <see cref="ShaderPipelineLoader"/>.</summary>
[JsonSerializable(typeof(ShaderPipelineDefinition))]
[JsonSerializable(typeof(ShaderPipelineResource))]
[JsonSerializable(typeof(ShaderPipelinePass))]
[JsonSerializable(typeof(ShaderPipelineOutput))]
[JsonSerializable(typeof(ResourceReference))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
public partial class ShaderPipelineJsonContext : JsonSerializerContext {
}