using System.Text.Json;
using System.Text.Json.Serialization;

using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// A <c>puck.shader.v1</c> shader-set manifest: one <c>&lt;id&gt;.puck.shader.json</c> beside its HLSL and compiled
/// bytecode, and the whole declaration of a shader set — its stage stems, its descriptor bindings, its config
/// schema (what a document may author for it), and its push-constant block (which fields, in what order, filled from
/// which source). The binding layout is authored by hand and cross-checked against the pipeline description built
/// for the same set at <see cref="ValidateBindings(GpuGraphicsPipelineDescription)"/> /
/// <see cref="ValidateBindings(GpuComputePipelineDescription)"/> time — never read from a native shader-reflection
/// dependency. Bytecode freshness is the build's: the <c>.hash</c> sidecar beside every bytecode file is checked on
/// every build against the source and the bytecode, so <see cref="Load"/> checks format only.
/// </summary>
/// <param name="Schema">The schema tag; must equal <see cref="SchemaTag"/>.</param>
/// <param name="Name">The shader set's id; must equal the manifest's file stem (the text before <see cref="FileSuffix"/>).</param>
/// <param name="Stages">The per-stage source stems.</param>
/// <param name="Bindings">The authored descriptor bindings, in binding order.</param>
/// <param name="TargetFloor">The compile-time capability floor the bytecode was built against.</param>
/// <param name="Description">What the set does; carried into the emitted config JSON Schema.</param>
/// <param name="Config">The config schema, name → field, in the order a schema emits them; <see langword="null"/> when
/// the set takes no configuration.</param>
/// <param name="PushConstants">The push-constant block; <see langword="null"/> when the set has none.</param>
public sealed partial record ShaderSetManifest(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    ShaderSetManifestStages Stages,
    IReadOnlyList<ShaderSetManifestBinding> Bindings,
    ShaderSetManifestTargetFloor TargetFloor,
    string? Description = null,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null,
    ShaderPushConstantBlock? PushConstants = null
) {
    /// <summary>The file suffix every manifest carries; the text before it is the set's id.</summary>
    public const string FileSuffix = ".puck.shader.json";
    /// <summary>The required <c>$schema</c> value of every <see cref="ShaderSetManifest"/> document.</summary>
    public const string SchemaTag = "puck.shader.v1";

    /// <summary>Gets the directory the manifest was loaded from — where its stage stems resolve.</summary>
    [JsonIgnore]
    public string Directory { get; private init; } = "";
    /// <summary>Gets a value indicating whether the set is a vertex+fragment (graphics) set rather than a compute set.</summary>
    [JsonIgnore]
    public bool IsGraphics => (Stages.Compute is null);
    /// <summary>Gets the resolved push-constant layout, or <see langword="null"/> when the set declares no block.</summary>
    [JsonIgnore]
    public ShaderPushConstantLayout? PushConstantLayout { get; private init; }

    /// <summary>Returns the path of one stage's bytecode beside this manifest.</summary>
    /// <param name="stem">The stage's source stem (<see cref="Stages"/>).</param>
    /// <param name="bytecodeExtension"><c>".spv"</c> or <c>".dxil"</c>.</param>
    /// <returns>The path.</returns>
    public string BytecodePath(string stem, string bytecodeExtension) =>
        Path.Combine(path1: Directory, path2: $"{stem}{bytecodeExtension}");
    /// <summary>Reads, parses, and validates a manifest file: the <c>$schema</c> tag, the name against the file stem,
    /// the stage shape, the config schema (defaults well-typed and in range), the push-constant block (sources
    /// resolved, offsets computed), and — for every stage present — that the sibling <c>.spv</c> exists and it and
    /// any sibling <c>.dxil</c> are well-formed bytecode (<see cref="ShaderBytecode.ValidateFormat"/>).</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <returns>The validated manifest.</returns>
    /// <exception cref="FileNotFoundException">The manifest, or a stage's <c>.spv</c>, does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed under any rule above, or a bytecode file fails
    /// format validation.</exception>
    public static ShaderSetManifest Load(string manifestPath) {
        if (!File.Exists(path: manifestPath)) {
            throw new FileNotFoundException(fileName: manifestPath, message: $"Shader set manifest not found: {manifestPath}");
        }

        ShaderSetManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize(json: File.ReadAllText(path: manifestPath), jsonTypeInfo: ShaderManifestJsonContext.Default.ShaderSetManifest)
                ?? throw new InvalidDataException(message: $"Shader set manifest is empty or 'null': {manifestPath}"));
        } catch (JsonException exception) {
            throw new InvalidDataException(message: $"Shader set manifest '{manifestPath}' is malformed: {exception.Message}", innerException: exception);
        }

        if (!string.Equals(a: manifest.Schema, b: SchemaTag, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"Shader set manifest '{manifestPath}' declares '$schema' = '{manifest.Schema}'; expected '{SchemaTag}'.");
        }

        var fileName = Path.GetFileName(path: manifestPath);
        var stem = (fileName.EndsWith(comparisonType: StringComparison.Ordinal, value: FileSuffix) ? fileName[..^FileSuffix.Length] : null);

        if ((stem is null) || !string.Equals(a: stem, b: manifest.Name, comparisonType: StringComparison.Ordinal)) {
            throw new InvalidDataException(message: $"Shader set manifest '{manifestPath}' is named '{manifest.Name}'; the file must be '{manifest.Name}{FileSuffix}'.");
        }

        var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: manifestPath)) ?? "");
        var isGraphics = ((manifest.Stages.Vertex is not null) && (manifest.Stages.Fragment is not null) && (manifest.Stages.Compute is null));
        var isCompute = ((manifest.Stages.Vertex is null) && (manifest.Stages.Fragment is null) && (manifest.Stages.Compute is not null));

        if (!isGraphics && !isCompute) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest must declare either vertex+fragment stages or a compute stage.");
        }

        var bindings = new HashSet<uint>();

        foreach (var binding in manifest.Bindings) {
            if (!bindings.Add(item: binding.VulkanBinding)) {
                throw new InvalidDataException(message: $"'{manifest.Name}' manifest declares Vulkan binding {binding.VulkanBinding} twice.");
            }
        }

        manifest.ValidateConfigSchema();

        var layout = ((manifest.PushConstants is { } block) ? ShaderPushConstantLayout.Resolve(block: block, config: manifest.Config, manifestName: manifest.Name) : null);

        ValidateStage(directory: directory, manifestName: manifest.Name, stem: manifest.Stages.Vertex, stageName: "vertex");
        ValidateStage(directory: directory, manifestName: manifest.Name, stem: manifest.Stages.Fragment, stageName: "fragment");
        ValidateStage(directory: directory, manifestName: manifest.Name, stem: manifest.Stages.Compute, stageName: "compute");

        return (manifest with { Directory = directory, PushConstantLayout = layout });
    }
    /// <summary>Refuses, naming the exact binding, when an authored <see cref="Bindings"/> entry disagrees with what
    /// <paramref name="description"/> actually requests. A graphics description carries no per-slot binding list
    /// (only a sampler count and a storage-buffer flag), so the check is coarse: the manifest's sampledImage-kind
    /// binding count must equal <see cref="GpuGraphicsPipelineDescription.TextureSamplerCount"/>, the manifest
    /// must declare a storageBuffer-kind binding if and only if <see cref="GpuGraphicsPipelineDescription.EnableStorageBuffer"/>
    /// is set, and the push-constant range size must equal <see cref="PushConstantLayout"/>'s.</summary>
    /// <param name="description">The pipeline description built for this manifest's graphics set.</param>
    /// <exception cref="InvalidDataException">A binding disagrees with the description.</exception>
    public void ValidateBindings(GpuGraphicsPipelineDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);

        var sampledImageCount = 0;
        var hasStorageBuffer = false;

        foreach (var binding in Bindings) {
            if (binding.Kind == ShaderSetManifestBindingKind.SampledImage) {
                sampledImageCount++;
            } else if (binding.Kind == ShaderSetManifestBindingKind.StorageBuffer) {
                hasStorageBuffer = true;
            }
        }

        if (sampledImageCount != description.TextureSamplerCount) {
            throw new InvalidDataException(message: $"'{Name}' manifest declares {sampledImageCount} sampledImage binding(s); the pipeline description requests {description.TextureSamplerCount}.");
        }
        if (hasStorageBuffer != description.EnableStorageBuffer) {
            throw new InvalidDataException(message: $"'{Name}' manifest {(hasStorageBuffer ? "declares a" : "declares no")} storageBuffer binding; the pipeline description {(description.EnableStorageBuffer ? "requests one" : "requests none")}.");
        }

        var declaredPushBytes = (PushConstantLayout?.SizeBytes ?? 0);
        var requestedPushBytes = (description.PushConstantBinding?.Size ?? 0);

        if (declaredPushBytes != requestedPushBytes) {
            throw new InvalidDataException(message: $"'{Name}' manifest's push-constant block is {declaredPushBytes} bytes; the pipeline description requests {requestedPushBytes}.");
        }
    }
    /// <summary>Refuses, naming the exact binding, when an authored <see cref="Bindings"/> entry at a given
    /// Vulkan binding index disagrees with the kind <paramref name="description"/> requests at that same index.</summary>
    /// <param name="description">The pipeline description built for this manifest's compute set.</param>
    /// <exception cref="InvalidDataException">A binding index, kind, or count disagrees with the description.</exception>
    public void ValidateBindings(GpuComputePipelineDescription description) {
        ArgumentNullException.ThrowIfNull(argument: description);

        var byBinding = new Dictionary<uint, ShaderSetManifestBinding>();

        foreach (var binding in Bindings) {
            byBinding[binding.VulkanBinding] = binding;
        }

        foreach (var requested in description.Bindings) {
            if (!byBinding.TryGetValue(key: requested.Binding, value: out var declared)) {
                throw new InvalidDataException(message: $"'{Name}' manifest declares no binding at Vulkan binding {requested.Binding}; the pipeline description requests one.");
            }
            if (!KindsAgree(manifestKind: declared.Kind, descriptionKind: requested.Kind)) {
                throw new InvalidDataException(message: $"'{Name}' manifest declares binding {requested.Binding} as '{declared.Kind}'; the pipeline description requests '{requested.Kind}' there.");
            }
            if (declared.Count != requested.Count) {
                throw new InvalidDataException(message: $"'{Name}' manifest declares binding {requested.Binding} with count {declared.Count}; the pipeline description requests count {requested.Count}.");
            }
        }
    }

    private static bool KindsAgree(ShaderSetManifestBindingKind manifestKind, GpuComputeBindingKind descriptionKind) {
        return manifestKind switch {
            ShaderSetManifestBindingKind.StorageBuffer => ((descriptionKind == GpuComputeBindingKind.StorageBufferRead) || (descriptionKind == GpuComputeBindingKind.StorageBufferReadWrite)),
            ShaderSetManifestBindingKind.SampledImage => (descriptionKind == GpuComputeBindingKind.SampledImage),
            ShaderSetManifestBindingKind.StorageImage => (descriptionKind == GpuComputeBindingKind.StorageImage),
            ShaderSetManifestBindingKind.AccelerationStructure => (descriptionKind == GpuComputeBindingKind.AccelerationStructure),
            _ => false,
        };
    }
    private static void ValidateStage(string directory, string manifestName, string? stem, string stageName) {
        if (stem is null) {
            return;
        }

        var spirvPath = Path.Combine(path1: directory, path2: $"{stem}.spv");

        if (!File.Exists(path: spirvPath)) {
            throw new FileNotFoundException(fileName: spirvPath, message: $"'{manifestName}' manifest's {stageName} stage '{stem}' has no compiled '{stem}.spv' beside it.");
        }

        ValidateBytecodeFile(manifestName: manifestName, path: spirvPath, stageName: stageName);

        var dxilPath = Path.Combine(path1: directory, path2: $"{stem}.dxil");

        if (File.Exists(path: dxilPath)) {
            ValidateBytecodeFile(manifestName: manifestName, path: dxilPath, stageName: stageName);
        }
    }
    private static void ValidateBytecodeFile(string manifestName, string path, string stageName) {
        try {
            ShaderBytecode.ValidateFormat(bytecode: File.ReadAllBytes(path: path));
        } catch (ArgumentException exception) {
            throw new InvalidDataException(message: $"'{manifestName}' manifest's {stageName} bytecode failed format validation: {path} ({exception.Message})", innerException: exception);
        }
    }
}

/// <summary>The source-generated (AOT/trim-safe) serialization context for <see cref="ShaderSetManifest"/>.</summary>
[JsonSerializable(typeof(ShaderSetManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ShaderManifestJsonContext : JsonSerializerContext {
}
