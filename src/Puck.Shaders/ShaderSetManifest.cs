using System.Text.Json;
using System.Text.Json.Serialization;

using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// A <c>puck.shader.manifest.v1</c> shader-set manifest: one <c>&lt;id&gt;.puck.shader.json</c> beside its HLSL and compiled
/// bytecode, and the whole declaration of a shader set: its stage stems, the resources its stages read, and its config
/// schema (what a document may author for it). The set's interface (<see cref="FrameLayout"/>) is the frame group at
/// set 0 and a pass group at set 3 holding its extent, its config and its resources (<see cref="Bindings"/>), and its
/// stages include the declarations generated from it, so where anything binds follows from the interface and is never
/// written in the manifest. Bytecode freshness is the build's: the <c>.hash</c> sidecar beside every bytecode file is checked on
/// every build against the source and the bytecode, so <see cref="Load(string)"/> checks format only.
/// </summary>
/// <param name="Schema">The schema tag; must equal <see cref="SchemaTag"/>.</param>
/// <param name="Name">The shader set's id; must equal the manifest's file stem (the text before <see cref="FileSuffix"/>).</param>
/// <param name="Stages">The per-stage source stems.</param>
/// <param name="Bindings">The resources the stages read from the pass group, in the order its interface lays them
/// out.</param>
/// <param name="TargetFloor">The compile-time capability floor the bytecode was built against.</param>
/// <param name="Description">What the set does; carried into the emitted config JSON Schema.</param>
/// <param name="Config">The config schema, name → field, in the order a schema emits them; <see langword="null"/> when
/// the set takes no configuration.</param>
public sealed partial record ShaderSetManifest(
    [property: JsonPropertyName("$schema")] string Schema,
    string Name,
    ShaderSetManifestStages Stages,
    IReadOnlyList<ShaderSetManifestBinding> Bindings,
    ShaderSetManifestTargetFloor TargetFloor,
    string? Description = null,
    IReadOnlyDictionary<string, ShaderConfigField>? Config = null
) {
    /// <summary>The file suffix every manifest carries; the text before it is the set's id.</summary>
    public const string FileSuffix = ".puck.shader.json";
    /// <summary>The required <c>$schema</c> value of every <see cref="ShaderSetManifest"/> document.</summary>
    public const string SchemaTag = "puck.shader.manifest.v1";

    private static readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> EmptyBytecode = new Dictionary<string, ReadOnlyMemory<byte>>(comparer: StringComparer.Ordinal);

    /// <summary>Gets the bytecode <see cref="Load(string, WorkCounterSet)"/> read and validated, keyed by
    /// <c>"&lt;stem&gt;&lt;extension&gt;"</c> (e.g. <c>"sdf-film-grain.frag.spv"</c>) exactly as
    /// <see cref="BytecodePath"/> would combine them, one entry per stage's <c>.spv</c> and, when shipped, its sibling
    /// <c>.dxil</c>. A consumer that builds an executor from this manifest reads a stage's bytes from here instead of
    /// reading the file again.</summary>
    [JsonIgnore]
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>> Bytecode { get; private init; } = EmptyBytecode;
    /// <summary>Gets the directory the manifest was loaded from — where its stage stems resolve.</summary>
    [JsonIgnore]
    public string Directory { get; private init; } = "";

    /// <summary>Gets a value indicating whether the set is a vertex+fragment (graphics) set rather than a compute set.</summary>
    [JsonIgnore]
    public bool IsGraphics => (Stages.Compute is null);

    /// <summary>Gets the set's interface as it is laid out: the frame group, and the pass group holding its extent, its
    /// config and its <see cref="Members"/> (<see cref="ShaderPipelineParameterLayout.Grouped"/>), whose generated
    /// declarations the set's stages include as <c>&lt;name&gt;.interface.hlsli</c>.</summary>
    [JsonIgnore]
    public ShaderPipelineParameterLayout FrameLayout { get; private init; } = null!;
    /// <summary>Gets the pass-group members <see cref="Bindings"/> declare, in order.</summary>
    /// <exception cref="InvalidDataException">A binding's kind is one a manifest does not declare.</exception>
    [JsonIgnore]
    public IReadOnlyList<ShaderInterfaceMember> Members => [.. Bindings.Select(selector: static binding => binding.ToMember())];

    /// <summary>Returns the path of one stage's bytecode beside this manifest.</summary>
    /// <param name="stem">The stage's source stem (<see cref="Stages"/>).</param>
    /// <param name="bytecodeExtension"><c>".spv"</c> or <c>".dxil"</c>.</param>
    /// <returns>The path.</returns>
    public string BytecodePath(string stem, string bytecodeExtension) =>
        Path.Combine(
            path1: Directory,
            path2: $"{stem}{bytecodeExtension}"
        );
    /// <summary>Reads a manifest's interface without its bytecode: the frame group, and the pass group holding the
    /// extent, the set's config and its <see cref="Members"/> (<see cref="ShaderFrameInterface.ForPass"/>), which
    /// <see cref="FrameLayout"/> lays out once the set loads. A set's declarations are generated from this before its
    /// bytecode can be built.</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <returns>The interface.</returns>
    /// <exception cref="InvalidDataException">The manifest is malformed, its config schema is invalid, or its name or
    /// a config field does not make a frame interface.</exception>
    /// <exception cref="IOException">The manifest cannot be read.</exception>
    public static ShaderInterface ReadFrameInterface(string manifestPath) {
        var manifest = ReadDeclaration(manifestPath: manifestPath);

        return ShaderFrameInterface.ForPass(
            config: manifest.Config,
            name: manifest.Name,
            ports: manifest.Members
        );
    }
    /// <summary>Reads a manifest's declaration without its bytecode: its stages, bindings and validated config schema,
    /// which a package catalog offers before any set is loaded. Its <see cref="Bytecode"/> is empty and its
    /// <see cref="FrameLayout"/> unset; <see cref="Load(string)"/> loads a set to run it.</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <returns>The declaration.</returns>
    /// <exception cref="InvalidDataException">The manifest is malformed or its config schema is invalid.</exception>
    /// <exception cref="IOException">The manifest cannot be read.</exception>
    public static ShaderSetManifest ReadDeclaration(string manifestPath) {
        ShaderSetManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize(
                json: File.ReadAllText(path: manifestPath),
                jsonTypeInfo: ShaderManifestJsonContext.Default.ShaderSetManifest
            ) ?? throw new InvalidDataException(message: $"Shader set manifest is empty or 'null': {manifestPath}"));
        } catch (JsonException exception) {
            throw new InvalidDataException(
                message: $"Shader set manifest '{manifestPath}' is malformed: {exception.Message}",
                innerException: exception
            );
        }

        manifest.ValidateConfigSchema();

        return manifest;
    }
    /// <summary>Reads, parses, and validates a manifest file: the <c>$schema</c> tag, the name against the file stem,
    /// the stage shape, the config schema (defaults well-typed and in range), the interface (the set's name an
    /// interface name, each binding a kind a manifest declares, no name repeating another member's), and, for every
    /// stage present, that the sibling <c>.spv</c> exists and it and any sibling <c>.dxil</c> are well-formed bytecode
    /// (<see cref="ShaderBytecode.ValidateFormat"/>). The load and the bytecode bytes it read are counted into
    /// <see cref="LoadWork"/>, and the validated bytes are kept on the returned manifest's <see cref="Bytecode"/> so a
    /// consumer building an executor from it reads no file again.</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <returns>The validated manifest.</returns>
    /// <exception cref="FileNotFoundException">The manifest, or a stage's <c>.spv</c>, does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed under any rule above, or a bytecode file fails
    /// format validation.</exception>
    public static ShaderSetManifest Load(string manifestPath) =>
        Load(
            manifestPath: manifestPath,
            work: LoadWork
        );
    /// <summary>Reads, parses, and validates a manifest file as <see cref="Load(string)"/> does, counting the load and
    /// the bytecode bytes it read into <paramref name="work"/> rather than the process's <see cref="LoadWork"/>.</summary>
    /// <param name="manifestPath">The manifest file's path.</param>
    /// <param name="work">The counts the load adds to; it must count <see cref="Loads"/> and
    /// <see cref="BytecodeBytes"/>.</param>
    /// <returns>The validated manifest.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="work"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="work"/> does not count <see cref="Loads"/> and
    /// <see cref="BytecodeBytes"/>.</exception>
    /// <exception cref="FileNotFoundException">The manifest, or a stage's <c>.spv</c>, does not exist.</exception>
    /// <exception cref="InvalidDataException">The manifest is malformed under any rule <see cref="Load(string)"/>
    /// checks, or a bytecode file fails format validation.</exception>
    public static ShaderSetManifest Load(string manifestPath, WorkCounterSet work) {
        ArgumentNullException.ThrowIfNull(argument: work);

        if (!File.Exists(path: manifestPath)) {
            throw new FileNotFoundException(
                fileName: manifestPath,
                message: $"Shader set manifest not found: {manifestPath}"
            );
        }

        work.Count(kind: Loads);

        ShaderSetManifest manifest;

        try {
            manifest = (JsonSerializer.Deserialize(
                json: File.ReadAllText(path: manifestPath),
                jsonTypeInfo: ShaderManifestJsonContext.Default.ShaderSetManifest
            )
                ?? throw new InvalidDataException(message: $"Shader set manifest is empty or 'null': {manifestPath}"));
        } catch (JsonException exception) {
            throw new InvalidDataException(
                message: $"Shader set manifest '{manifestPath}' is malformed: {exception.Message}",
                innerException: exception
            );
        }

        if (!string.Equals(
            a: manifest.Schema,
            b: SchemaTag,
            comparisonType: StringComparison.Ordinal
        )) {
            throw new InvalidDataException(message: $"Shader set manifest '{manifestPath}' declares '$schema' = '{manifest.Schema}'; expected '{SchemaTag}'.");
        }

        var fileName = Path.GetFileName(path: manifestPath);
        var stem = (fileName.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: FileSuffix
        )
            ? fileName[..^FileSuffix.Length]
            : null
        );

        if (
            (stem is null) ||
            !string.Equals(
            a: stem,
            b: manifest.Name,
            comparisonType: StringComparison.Ordinal
        )
        ) {
            throw new InvalidDataException(message: $"Shader set manifest '{manifestPath}' is named '{manifest.Name}'; the file must be '{manifest.Name}{FileSuffix}'.");
        }

        var directory = (Path.GetDirectoryName(path: Path.GetFullPath(path: manifestPath)) ?? "");
        var isGraphics = ((manifest.Stages.Vertex is not null) && (manifest.Stages.Fragment is not null) && (manifest.Stages.Compute is null));
        var isCompute = ((manifest.Stages.Vertex is null) && (manifest.Stages.Fragment is null) && (manifest.Stages.Compute is not null));

        if (
            !isGraphics &&
            !isCompute
        ) {
            throw new InvalidDataException(message: $"'{manifest.Name}' manifest must declare either vertex+fragment stages or a compute stage.");
        }

        manifest.ValidateConfigSchema();

        var layout = ShaderPipelineParameterLayout.Grouped(
            config: manifest.Config,
            interfaceName: manifest.Name,
            members: manifest.Members
        );

        var bytecode = new Dictionary<string, ReadOnlyMemory<byte>>(comparer: StringComparer.Ordinal);

        ValidateStage(
            bytecode: bytecode,
            directory: directory,
            manifestName: manifest.Name,
            stem: manifest.Stages.Vertex,
            stageName: "vertex",
            work: work
        );
        ValidateStage(
            bytecode: bytecode,
            directory: directory,
            manifestName: manifest.Name,
            stem: manifest.Stages.Fragment,
            stageName: "fragment",
            work: work
        );
        ValidateStage(
            bytecode: bytecode,
            directory: directory,
            manifestName: manifest.Name,
            stem: manifest.Stages.Compute,
            stageName: "compute",
            work: work
        );

        return (manifest with { Bytecode = bytecode, Directory = directory, FrameLayout = layout });
    }
    private static void ValidateStage(Dictionary<string, ReadOnlyMemory<byte>> bytecode, string directory, string manifestName, string? stem, string stageName, WorkCounterSet work) {
        if (stem is null) {
            return;
        }

        var spirvName = $"{stem}.spv";
        var spirvPath = Path.Combine(
            path1: directory,
            path2: spirvName
        );

        if (!File.Exists(path: spirvPath)) {
            throw new FileNotFoundException(
                fileName: spirvPath,
                message: $"'{manifestName}' manifest's {stageName} stage '{stem}' has no compiled '{stem}.spv' beside it."
            );
        }

        bytecode[spirvName] = ValidateBytecodeFile(
            manifestName: manifestName,
            path: spirvPath,
            stageName: stageName,
            work: work
        );

        var dxilName = $"{stem}.dxil";
        var dxilPath = Path.Combine(
            path1: directory,
            path2: dxilName
        );

        if (File.Exists(path: dxilPath)) {
            bytecode[dxilName] = ValidateBytecodeFile(
                manifestName: manifestName,
                path: dxilPath,
                stageName: stageName,
                work: work
            );
        }
    }
    private static ReadOnlyMemory<byte> ValidateBytecodeFile(string manifestName, string path, string stageName, WorkCounterSet work) {
        var bytecode = File.ReadAllBytes(path: path);

        work.Add(
            amount: bytecode.LongLength,
            kind: BytecodeBytes
        );

        try {
            ShaderBytecode.ValidateFormat(bytecode: bytecode);
        } catch (ArgumentException exception) {
            throw new InvalidDataException(
                message: $"'{manifestName}' manifest's {stageName} bytecode failed format validation: {path} ({exception.Message})",
                innerException: exception
            );
        }

        return bytecode;
    }
}

/// <summary>The source-generated (AOT/trim-safe) serialization context for <see cref="ShaderSetManifest"/>.</summary>
[JsonSerializable(typeof(ShaderSetManifest))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class ShaderManifestJsonContext : JsonSerializerContext {
}
