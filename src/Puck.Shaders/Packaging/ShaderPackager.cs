using System.Text;
using System.Text.Json;
using Puck.Abstractions;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>How building or loading a package ended.</summary>
/// <param name="Status">How it ended. <see cref="Manifest"/> and <see cref="Pipeline"/> are non-null exactly when this is
/// <see cref="ShaderPipelineLoadStatus.Compiled"/>. A missing native tool is
/// <see cref="ShaderPipelineLoadStatus.Unsupported"/>, a refusal or a failed pass
/// <see cref="ShaderPipelineLoadStatus.Failed"/>, and a source edited while the package was read
/// <see cref="ShaderPipelineLoadStatus.Retry"/>.</param>
/// <param name="Manifest">The package's manifest, or <see langword="null"/>.</param>
/// <param name="Pipeline">The compiled candidate, or <see langword="null"/>.</param>
/// <param name="Message">The summary or the reason.</param>
/// <param name="Code">The refusal's <see cref="ShaderClosureRefusedException.Code"/>, or <see langword="null"/> when
/// nothing was refused by name.</param>
public sealed record ShaderPackageResult(
    ShaderPipelineLoadStatus Status,
    ShaderPackageManifest? Manifest,
    CompiledShaderPipeline? Pipeline,
    string Message,
    string? Code = null
);
/// <summary>
/// Builds and loads <c>puck.shader.package.v1</c> packages. A build compiles a pipeline through the ordinary
/// <see cref="ShaderPipelineLoader"/> and <see cref="ShaderCompiler"/>, holds every binary's reflected frame block to its
/// interface's layout and compiles and reflects each interface's echo pass the same way, and writes the sources, each
/// pass's interface and generated declarations, and the binaries. A load reads the binaries and runs no tool, so a
/// package runs where no compiler exists.
/// <para>Loading refuses before it hands anything over: a malformed manifest, a missing or altered file or binary, a
/// closure that differs from the files listed or reaches outside the package, a limit exceeded, different backend
/// capabilities, or a pass whose interface or generated declarations differ from the ones the package's document
/// now yields. The files are read on every load, so nothing conceals a missing dependency.</para>
/// <para>A packager given a build's package store (<see cref="Store"/>) loads a pipeline document or one-off shader
/// from the store's package keyed by that source (<see cref="KeyOf(string, string)"/>) whenever one exists, so a
/// shipped source compiles nothing; see <see cref="LoadSource"/>.</para>
/// </summary>
public sealed partial class ShaderPackager {
    private readonly ShaderCompiler m_compiler;
    private readonly ShaderSourceLimits m_limits;
    private readonly ShaderPipelineLoader m_loader;
    private readonly bool m_reflectDxil;

    /// <summary>Initializes a new instance of the <see cref="ShaderPackager"/> class.</summary>
    /// <param name="compiler">The compiler a build compiles every pass with; a load runs none of its tools.</param>
    /// <param name="limits">The limits a package's whole closure must fit; <see langword="null"/> means
    /// <see cref="ShaderSourceLimits.Default"/>. Every compile also enforces the defaults on its own closure.</param>
    /// <param name="reflectDxil">Whether a build on Windows also holds each DXIL container's reflected frame block to
    /// its layout, through the <c>dxcompiler.dll</c> beside the compiler's <c>dxc</c>. Every build holds the SPIR-V
    /// modules to it.</param>
    /// <param name="store">The build's package store: the directory holding one package per shipped source, each in the
    /// directory <see cref="StorePathOf"/> names for its key; <see langword="null"/> when there is none.</param>
    /// <exception cref="ArgumentNullException"><paramref name="compiler"/> is <see langword="null"/>.</exception>
    public ShaderPackager(ShaderCompiler compiler, ShaderSourceLimits? limits = null, bool reflectDxil = true, string? store = null) {
        ArgumentNullException.ThrowIfNull(argument: compiler);

        m_compiler = compiler;
        m_limits = (limits ?? ShaderSourceLimits.Default);
        m_loader = new ShaderPipelineLoader(compiler: compiler);
        m_reflectDxil = reflectDxil;
        Store = ((store is null)
            ? null
            : Path.GetFullPath(path: store));
    }

    /// <summary>Returns the capabilities a plan requires.</summary>
    /// <param name="plan">The plan.</param>
    /// <returns>The capabilities.</returns>
    public static ShaderPackageCapabilities CapabilitiesOf(ShaderPipelinePlan plan) {
        ArgumentNullException.ThrowIfNull(argument: plan);

        return new ShaderPackageCapabilities(
            Buffers: plan.Storages.Any(predicate: static storage => (storage.Declaration.Kind == ShaderPipelineResourceKind.Buffer)),
            ImageFormats: plan.Storages
                .Where(predicate: static storage => ((storage.Declaration.Kind == ShaderPipelineResourceKind.Image) && (storage.Declaration.Format is not null)))
                .Select(selector: static storage => storage.Declaration.Format!)
                .Distinct(comparer: StringComparer.Ordinal)
                .Order(comparer: StringComparer.Ordinal)
                .ToArray(),
            ParameterBytes: plan.Passes.Select(selector: static pass => pass.Parameters.SizeBytes).DefaultIfEmpty().Max(),
            TargetFloor: new ShaderSetManifestTargetFloor(
                ShaderModel: ShaderCompiler.ShaderModel,
                Vulkan: ShaderCompiler.VulkanVersion
            ),
            WorkgroupInvocations: plan.Passes
                .Where(predicate: static pass => (pass.Declaration.Kind == ShaderPipelineDocumentPassKind.Compute))
                .Select(selector: static pass => checked(((pass.Declaration.GroupSizeX * pass.Declaration.GroupSizeY) * pass.Declaration.GroupSizeZ)))
                .DefaultIfEmpty()
                .Max()
        );
    }
    /// <summary>Reads and validates a manifest's bytes.</summary>
    /// <param name="utf8">The manifest's UTF-8 JSON.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ShaderClosureRefusedException">The bytes are not a well-formed <c>puck.shader.package.v1</c>
    /// manifest (<see cref="ShaderClosureRefusedException.PackageMalformed"/>).</exception>
    public static ShaderPackageManifest Read(ReadOnlySpan<byte> utf8) {
        ShaderPackageManifest? manifest;

        try {
            manifest = JsonSerializer.Deserialize(
                jsonTypeInfo: ShaderPackageJsonContext.Default.ShaderPackageManifest,
                utf8Json: utf8
            );
        } catch (JsonException exception) {
            throw Malformed(why: exception.Message);
        }

        if (manifest is null) {
            throw Malformed(why: "the manifest is null.");
        }

        Validate(manifest: manifest);

        return manifest;
    }
    /// <summary>Writes a manifest's canonical UTF-8 JSON, ending in one line feed.</summary>
    /// <param name="manifest">The manifest.</param>
    /// <returns>The bytes.</returns>
    public static byte[] Write(ShaderPackageManifest manifest) {
        ArgumentNullException.ThrowIfNull(argument: manifest);

        return [.. JsonSerializer.SerializeToUtf8Bytes(
            jsonTypeInfo: ShaderPackageJsonContext.Default.ShaderPackageManifest,
            value: manifest
        ), ((byte)'\n')];
    }
    /// <summary>Compiles a pipeline document or one-off shader and writes its package: the sources, and for each pass its
    /// interface, the declarations generated from it, and its binaries for both backends. Every binary's frame block, and
    /// that of each interface's echo pass, must reflect exactly as the interface lays it out.</summary>
    /// <param name="source">The pipeline document or one-off shader source.</param>
    /// <param name="output">The package directory to write. It is replaced only once the new package is complete, and
    /// only when it is absent, empty, or already a package.</param>
    /// <param name="root">The directory every file of the closure must lie within, and the one logical paths are
    /// relative to; <see langword="null"/> means the directory holding <paramref name="source"/>.</param>
    /// <param name="name">The name a one-off shader's pipeline and its one pass take, as an instance naming it would give
    /// them; <see langword="null"/> means the source's file name without its extension. A pipeline document names
    /// itself.</param>
    /// <param name="cancellationToken">The token that cancels the build.</param>
    /// <returns>The outcome; on success, the manifest written and the candidate compiled.</returns>
    public async Task<ShaderPackageResult> BuildAsync(string source, string output, string? root = null, string? name = null, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: source);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: output);

        return await GuardAsync(body: async () => {
            var documentPath = Path.GetFullPath(path: source);
            var rootPath = Path.GetFullPath(path: (root ?? Path.GetDirectoryName(path: documentPath)!));
            var outputPath = Path.GetFullPath(path: output);
            var plan = Plan(
                documentPath: documentPath,
                name: (name ?? Path.GetFileNameWithoutExtension(path: documentPath))
            );
            var survey = Survey(
                documentPath: documentPath,
                plan: plan,
                rootPath: rootPath
            );
            var tools = new List<ShaderPackageTool>(capacity: survey.Tools.Count);

            foreach (var tool in survey.Tools) {
                tools.Add(item: new ShaderPackageTool(
                    Name: tool,
                    Version: await m_compiler.ToolVersionAsync(
                        cancellationToken: cancellationToken,
                        tool: tool
                    ).ConfigureAwait(continueOnCapturedContext: false)
                ));
            }

            var loaded = m_loader.Load(
                cancellationToken: cancellationToken,
                name: plan.Definition.Name,
                path: documentPath
            );

            if (loaded.Pipeline is null) {
                return new ShaderPackageResult(
                    Manifest: null,
                    Message: loaded.Message,
                    Pipeline: null,
                    Status: loaded.Status
                );
            }

            if (!Matches(
                pipeline: loaded.Pipeline,
                survey: survey
            )) {
                return Changed();
            }

            await ReflectAsync(
                cancellationToken: cancellationToken,
                pipeline: loaded.Pipeline,
                survey: survey
            ).ConfigureAwait(continueOnCapturedContext: false);

            var binaries = new List<(string LogicalPath, byte[] Bytes)>();
            var passes = PassesOf(
                binaries: binaries,
                pipeline: loaded.Pipeline,
                rootPath: rootPath,
                survey: survey
            );
            var manifest = new ShaderPackageManifest(
                Capabilities: CapabilitiesOf(plan: loaded.Pipeline.Plan),
                Compiler: new ShaderPackageCompiler(
                    Tools: tools,
                    Version: passes.Compiler
                ),
                Document: LogicalPath(
                    path: documentPath,
                    rootPath: rootPath
                ),
                Files: survey.Files.Select(selector: file => new ShaderPackageFile(
                    Bytes: new FileInfo(fileName: file.Key).Length,
                    Path: LogicalPath(
                        path: file.Key,
                        rootPath: rootPath
                    ),
                    Pin: (ContentPin.Prefix + file.Value.Hash)
                )).OrderBy(
                    comparer: StringComparer.Ordinal,
                    keySelector: static file => file.Path
                ).ToArray(),
                Name: plan.Definition.Name,
                Passes: passes.Passes,
                Schema: ShaderPackageManifest.SchemaName
            );

            if (!WritePackage(
                binaries: binaries,
                manifest: manifest,
                outputPath: outputPath,
                rootPath: rootPath,
                survey: survey
            )) {
                return Changed();
            }

            return new ShaderPackageResult(
                Manifest: manifest,
                Message: $"packaged: {manifest.Files.Count} files, {manifest.Passes.Count} passes; {loaded.Message}",
                Pipeline: loaded.Pipeline,
                Status: ShaderPipelineLoadStatus.Compiled
            );
        }).ConfigureAwait(continueOnCapturedContext: false);
    }
    /// <summary>Determines whether a source path names a package: a package is named by its directory, and every other
    /// path names a pipeline document or a one-off shader.</summary>
    /// <param name="path">The source path.</param>
    /// <returns><see langword="true"/> when <paramref name="path"/> is a directory.</returns>
    public static bool IsPackage(string path) => Directory.Exists(path: path);
    /// <summary>Reads a package's manifest and verifies every file it lists, without planning or loading anything: the
    /// manifest must be well formed, and each listed file, interface, declaration and binary must exist with the length
    /// and pin it records.</summary>
    /// <param name="package">The package directory.</param>
    /// <param name="limits">The limits the manifest and the listed files must fit; <see langword="null"/> means
    /// <see cref="ShaderSourceLimits.Default"/>.</param>
    /// <returns>The manifest.</returns>
    /// <exception cref="ShaderClosureRefusedException"><paramref name="package"/> is not a directory holding a
    /// well-formed manifest (<see cref="ShaderClosureRefusedException.PackageMalformed"/>), a listed file is absent
    /// (<see cref="ShaderClosureRefusedException.PackageFileMissing"/>) or differs from its pin
    /// (<see cref="ShaderClosureRefusedException.PackageFilePin"/>), or a limit is exceeded.</exception>
    /// <exception cref="IOException">A file cannot be read.</exception>
    public static ShaderPackageManifest Open(string package, ShaderSourceLimits? limits = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);

        var bound = (limits ?? ShaderSourceLimits.Default);
        var rootPath = Path.GetFullPath(path: package);
        var manifestInfo = new FileInfo(fileName: Path.Combine(
            path1: rootPath,
            path2: ShaderPackageManifest.FileName
        ));

        if (
            !IsPackage(path: rootPath) ||
            !manifestInfo.Exists
        ) {
            throw Malformed(why: $"a package is named by its directory, and '{rootPath}' is not a directory holding {ShaderPackageManifest.FileName}.");
        }

        if (manifestInfo.Length > bound.MaxFileBytes) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.FileBytes,
                message: $"'{manifestInfo.FullName}' holds {manifestInfo.Length} bytes; one file may hold at most {bound.MaxFileBytes}."
            );
        }

        var manifest = Read(utf8: File.ReadAllBytes(path: manifestInfo.FullName));

        VerifyFiles(
            limits: bound,
            manifest: manifest,
            rootPath: rootPath
        );

        return manifest;
    }
    /// <summary>Loads what a pipeline instance's source names. A package directory loads through <see cref="LoadAsync"/>,
    /// which compiles nothing. A pipeline document or one-off shader loads from the <see cref="Store"/>'s package keyed by
    /// it (<see cref="KeyOf(string, string)"/>) when the store holds one, which compiles nothing either, and otherwise
    /// through the ordinary <see cref="ShaderPipelineLoader"/>, which compiles it. A source the store holds no package for
    /// and no compiler can compile here is <see cref="ShaderPipelineLoadStatus.Unsupported"/> with a message that starts
    /// with <c>[SHADERPKG_ABSENT]</c>. A package refusal, the store's included, is
    /// <see cref="ShaderPipelineLoadStatus.Failed"/> with a message that starts with its bracketed code, and nothing
    /// compiles in its place.</summary>
    /// <param name="name">The instance name, which names a one-off shader's pipeline. A package's pipeline is named by
    /// its manifest.</param>
    /// <param name="path">The source path.</param>
    /// <param name="cancellationToken">The token that cancels the load.</param>
    /// <returns>The outcome. A package directory's dependencies are its manifest and every file the manifest lists; a
    /// source's are the files of its closure, whether its store package or the compiler served it.</returns>
    public ShaderPipelineLoadResult LoadSource(string name, string path, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: path);

        var fullPath = Path.GetFullPath(path: path);

        if (!IsPackage(path: fullPath)) {
            return LoadSourceFile(
                cancellationToken: cancellationToken,
                name: name,
                path: fullPath
            );
        }

        var loaded = LoadAsync(
            cancellationToken: cancellationToken,
            package: fullPath
        ).GetAwaiter().GetResult();

        return new ShaderPipelineLoadResult(
            Dependencies: DependenciesOf(rootPath: fullPath),
            Message: loaded.Message,
            Pipeline: loaded.Pipeline,
            Status: loaded.Status
        );
    }
    /// <summary>Verifies a package and reads its precompiled binaries into a candidate. No tool runs: the package's own
    /// binaries are the candidate's bytecode.</summary>
    /// <param name="package">The package directory.</param>
    /// <param name="cancellationToken">The token that cancels the load.</param>
    /// <returns>The outcome; on success, the package's manifest and the candidate read from its binaries.</returns>
    public Task<ShaderPackageResult> LoadAsync(string package, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: package);

        return GuardAsync(body: () => {
            cancellationToken.ThrowIfCancellationRequested();

            var rootPath = Path.GetFullPath(path: package);
            var manifest = Open(
                limits: m_limits,
                package: rootPath
            );
            var documentPath = FullPath(
                logicalPath: manifest.Document,
                rootPath: rootPath
            );
            var plan = Plan(
                documentPath: documentPath,
                name: manifest.Name
            );
            var survey = Survey(
                documentPath: documentPath,
                plan: plan,
                rootPath: rootPath
            );
            var listed = manifest.Files.Select(selector: static file => file.Path).ToHashSet(comparer: StringComparer.Ordinal);
            var reached = survey.Files.Keys.Select(selector: path => LogicalPath(
                path: path,
                rootPath: rootPath
            )).ToHashSet(comparer: StringComparer.Ordinal);

            if (!listed.SetEquals(other: reached)) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.PackageClosure,
                    message: $"the document reaches [{string.Join(separator: ", ", values: reached.Except(second: listed).Order(comparer: StringComparer.Ordinal))}] beyond the listed files and not [{string.Join(separator: ", ", values: listed.Except(second: reached).Order(comparer: StringComparer.Ordinal))}]."
                );
            }

            if (!string.Equals(
                a: Json(capabilities: CapabilitiesOf(plan: plan)),
                b: Json(capabilities: manifest.Capabilities),
                comparisonType: StringComparison.Ordinal
            )) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.PackageCapabilities,
                    message: $"the plan requires {Json(capabilities: CapabilitiesOf(plan: plan))}, not the recorded {Json(capabilities: manifest.Capabilities)}."
                );
            }

            if (!manifest.Passes.Select(selector: static pass => pass.Name).SequenceEqual(second: plan.Passes.Select(selector: static pass => pass.Name))) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.PackageClosure,
                    message: $"the document plans passes [{string.Join(separator: ", ", values: plan.PassOrder)}]; the package records [{string.Join(separator: ", ", values: manifest.Passes.Select(selector: static pass => pass.Name))}]."
                );
            }

            var shaders = new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal);

            foreach (var planned in plan.Passes) {
                var recorded = manifest.Passes.Single(predicate: pass => string.Equals(
                    a: pass.Name,
                    b: planned.Name,
                    comparisonType: StringComparison.Ordinal
                ));
                var generated = survey.Generated[planned.Name];

                if (
                    !string.Equals(
                        a: recorded.Interface.Pin,
                        b: planned.Parameters.Interface.Hash.ToString(),
                        comparisonType: StringComparison.Ordinal
                    ) ||
                    !string.Equals(
                        a: recorded.Declarations.Pin,
                        b: (ContentPin.Prefix + ShaderSourceClosure.HashOf(text: generated.DeclarationsText)),
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    throw new ShaderClosureRefusedException(
                        code: ShaderClosureRefusedException.PackageInterface,
                        message: $"pass '{planned.Name}' reads interface {planned.Parameters.Interface.Hash} through the declarations this engine generates; the package was built for interface {recorded.Interface.Pin} and declarations {recorded.Declarations.Pin}."
                    );
                }

                shaders[planned.Name] = ReadBinaries(
                    planned: planned,
                    recorded: recorded,
                    rootPath: rootPath,
                    sourcePath: generated.SourcePath
                );
            }

            var pipeline = new CompiledShaderPipeline(
                plan: plan,
                shaders: shaders
            );

            return Task.FromResult(result: new ShaderPackageResult(
                Manifest: manifest,
                Message: $"loaded package '{manifest.Name}': {manifest.Files.Count} files, {manifest.Passes.Count} passes from their binaries; outputs={string.Join(separator: ",", values: plan.Outputs)}",
                Pipeline: pipeline,
                Status: ShaderPipelineLoadStatus.Compiled
            ));
        });
    }

    private static ShaderPackageResult Changed() => new(
        Manifest: null,
        Message: "Source changed while the package was read; retrying the complete package.",
        Pipeline: null,
        Status: ShaderPipelineLoadStatus.Retry
    );
    private static string FullPath(string logicalPath, string rootPath) => Path.GetFullPath(path: Path.Combine(
        path1: rootPath,
        path2: logicalPath
    ));
    private static async Task<ShaderPackageResult> GuardAsync(Func<Task<ShaderPackageResult>> body) {
        try {
            return await body().ConfigureAwait(continueOnCapturedContext: false);
        } catch (ShaderClosureRefusedException exception) {
            return new ShaderPackageResult(
                Code: exception.Code,
                Manifest: null,
                Message: exception.Message,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Failed
            );
        } catch (ShaderToolMissingException exception) {
            return new ShaderPackageResult(
                Manifest: null,
                Message: exception.Message,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Unsupported
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or ShaderPipelineCompilationException)) {
            return new ShaderPackageResult(
                Manifest: null,
                Message: exception.Message,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Failed
            );
        }
    }
    private static bool IsLogicalPath(string path) =>
        ((path.Length != 0) &&
        (path.IndexOfAny(anyOf: ['\\', ':']) < 0) &&
        path.Split('/').All(predicate: static segment => (segment is not ("" or "." or ".."))));
    private static string Json(ShaderPackageCapabilities capabilities) => JsonSerializer.Serialize(
        jsonTypeInfo: ShaderPackageJsonContext.Default.ShaderPackageCapabilities,
        value: capabilities
    ).ReplaceLineEndings(replacementText: string.Empty);
    private static string LogicalPath(string path, string rootPath) {
        if (!ShaderSourceClosure.IsWithin(
            directory: rootPath,
            path: path
        )) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.OutsideClosure,
                message: $"'{path}' lies outside the closure rooted at '{rootPath}'."
            );
        }

        var logical = Path.GetRelativePath(
            path: path,
            relativeTo: rootPath
        ).Replace(
            newChar: '/',
            oldChar: '\\'
        );

        if (string.Equals(
            a: logical,
            b: ShaderPackageManifest.FileName,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageClosure,
                message: $"'{path}' would be written over the package's own {ShaderPackageManifest.FileName}."
            );
        }

        return logical;
    }
    private static ShaderClosureRefusedException Malformed(string why) => new(
        code: ShaderClosureRefusedException.PackageMalformed,
        message: $"not a {ShaderPackageManifest.SchemaName} manifest: {why}"
    );
    // Every logical path a manifest names: its authored files, then each pass's interface, declarations and binaries.
    private static IEnumerable<string> PathsOf(ShaderPackageManifest manifest) =>
        manifest.Files.Select(selector: static file => file.Path)
            .Concat(second: manifest.Passes.SelectMany(selector: static pass => ((IEnumerable<string>)[pass.Interface.Path, pass.Declarations.Path]).Concat(second: pass.Variants.SelectMany(selector: static variant => variant.Binaries.Select(selector: static binary => binary.Path)))))
            .Distinct(comparer: StringComparer.Ordinal);
    private static IReadOnlyList<string> DependenciesOf(string rootPath) {
        var manifestPath = Path.Combine(
            path1: rootPath,
            path2: ShaderPackageManifest.FileName
        );

        try {
            return [manifestPath, .. PathsOf(manifest: Read(utf8: File.ReadAllBytes(path: manifestPath))).Select(selector: path => FullPath(
                logicalPath: path,
                rootPath: rootPath
            ))];
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or ShaderClosureRefusedException)) {
            return [manifestPath];
        }
    }
    private static void ValidateFile(ShaderPackageFile file, string what) {
        if (!IsLogicalPath(path: file.Path)) {
            throw Malformed(why: $"{what} '{file.Path}' is not a logical path: relative, with forward slashes and no '.' or '..' segment.");
        }

        if (!ContentPin.TryParse(
            pin: out _,
            text: file.Pin
        )) {
            throw Malformed(why: $"{what} '{file.Path}' has the pin '{file.Pin}'; expected '{ContentPin.Prefix}' followed by {ContentPin.HexLength} lowercase hexadecimal digits.");
        }

        if (file.Bytes < 0) {
            throw Malformed(why: $"{what} '{file.Path}' has a negative length.");
        }
    }
    private static void Validate(ShaderPackageManifest manifest) {
        if (!string.Equals(
            a: manifest.Schema,
            b: ShaderPackageManifest.SchemaName,
            comparisonType: StringComparison.Ordinal
        )) {
            throw Malformed(why: $"$schema is '{manifest.Schema}'.");
        }

        if (string.IsNullOrWhiteSpace(value: manifest.Name)) {
            throw Malformed(why: "name is empty.");
        }

        if (string.IsNullOrWhiteSpace(value: manifest.Compiler.Version)) {
            throw Malformed(why: "the compiler's version is empty.");
        }

        var tools = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var tool in manifest.Compiler.Tools) {
            if (
                string.IsNullOrWhiteSpace(value: tool.Name) ||
                string.IsNullOrWhiteSpace(value: tool.Version) ||
                !tools.Add(item: tool.Name)
            ) {
                throw Malformed(why: $"tool '{tool.Name}' is empty, has no version, or is listed twice.");
            }
        }

        if (manifest.Files.Count == 0) {
            throw Malformed(why: "files is empty.");
        }

        // Case-insensitively unique, so a package lands the same on every file system.
        var paths = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var file in manifest.Files) {
            ValidateFile(
                file: file,
                what: "file"
            );

            if (!paths.Add(item: file.Path)) {
                throw Malformed(why: $"'{file.Path}' is listed twice.");
            }

            if (string.Equals(
                a: file.Path,
                b: ShaderPackageManifest.FileName,
                comparisonType: StringComparison.OrdinalIgnoreCase
            )) {
                throw Malformed(why: $"a file cannot be named {ShaderPackageManifest.FileName}.");
            }
        }

        if (!paths.Contains(item: manifest.Document)) {
            throw Malformed(why: $"the document '{manifest.Document}' is not a listed file.");
        }

        if (manifest.Passes.Count == 0) {
            throw Malformed(why: "passes is empty.");
        }

        var passes = new HashSet<string>(comparer: StringComparer.Ordinal);
        var artifacts = new Dictionary<string, string>(comparer: StringComparer.OrdinalIgnoreCase);

        // Two passes compiling one source share its interface and declarations, so an artifact path may repeat only
        // with the same pin; a binary path never repeats.
        void Artifact(ShaderPackageFile file, string what, bool shared) {
            ValidateFile(
                file: file,
                what: what
            );

            if (
                paths.Contains(item: file.Path) ||
                (artifacts.TryGetValue(
                    key: file.Path,
                    value: out var pin
                ) && (!shared || !string.Equals(
                    a: pin,
                    b: file.Pin,
                    comparisonType: StringComparison.Ordinal
                )))
            ) {
                throw Malformed(why: $"{what} '{file.Path}' is listed twice.");
            }

            artifacts[file.Path] = file.Pin;
        }

        foreach (var pass in manifest.Passes) {
            if (
                string.IsNullOrWhiteSpace(value: pass.Name) ||
                !passes.Add(item: pass.Name) ||
                (pass.Stages.Count == 0) ||
                pass.Stages.Any(predicate: static stage => ((stage.Steps.Count == 0) || stage.Steps.Any(predicate: static step => string.IsNullOrWhiteSpace(value: step.Tool))))
            ) {
                throw Malformed(why: $"pass '{pass.Name}' is empty, is listed twice, or has a stage with no steps.");
            }

            Artifact(
                file: pass.Interface,
                shared: true,
                what: "interface"
            );
            Artifact(
                file: pass.Declarations,
                shared: true,
                what: "declarations"
            );

            if (!pass.Variants.Any(predicate: static variant => string.Equals(
                a: variant.Name,
                b: ShaderPackageVariant.DefaultName,
                comparisonType: StringComparison.Ordinal
            ))) {
                throw Malformed(why: $"pass '{pass.Name}' has no '{ShaderPackageVariant.DefaultName}' variant.");
            }

            foreach (var variant in pass.Variants) {
                foreach (var stage in pass.Stages) {
                    foreach (var target in ((string[])[ShaderPackageBinary.SpirvTarget, ShaderPackageBinary.DxilTarget])) {
                        if (variant.Binaries.Count(predicate: binary => ((binary.Stage == stage.Stage) && string.Equals(
                            a: binary.Target,
                            b: target,
                            comparisonType: StringComparison.Ordinal
                        ))) != 1) {
                            throw Malformed(why: $"pass '{pass.Name}' variant '{variant.Name}' does not carry exactly one {target} binary for its {stage.Stage} stage.");
                        }
                    }
                }

                if (variant.Binaries.Count != (pass.Stages.Count * 2)) {
                    throw Malformed(why: $"pass '{pass.Name}' variant '{variant.Name}' carries a binary for a stage the pass does not compile.");
                }

                foreach (var binary in variant.Binaries) {
                    Artifact(
                        file: new ShaderPackageFile(
                            Bytes: binary.Bytes,
                            Path: binary.Path,
                            Pin: binary.Pin
                        ),
                        shared: false,
                        what: "binary"
                    );
                }
            }
        }
    }
    // Holds every compiled binary's frame block to its pass's layout, then compiles each interface's echo pass and holds
    // its blocks too: the echo reads every member, so a declaration the generator placed wrongly shows here.
    private async Task ReflectAsync(CompiledShaderPipeline pipeline, PackageSurvey survey, CancellationToken cancellationToken) {
        var dxil = ((m_reflectDxil && OperatingSystem.IsWindows())
            ? DxilInterfaceReader.Load(toolchain: m_compiler.Toolchain)
            : null);

        try {
            void Check(string what, ShaderInterfaceLayout layout, CompiledShader shader) {
                foreach (var (stage, module) in shader.SpirvByStage) {
                    Refuse(
                        mismatch: layout.PushedBlockMismatch(reflected: SpirvInterfaceReader.Read(module: module.Span)),
                        what: $"{what} {stage} SPIR-V"
                    );
                }

                if (
                    (dxil is not null) &&
                    OperatingSystem.IsWindows()
                ) {
                    foreach (var (stage, container) in shader.DxilByStage) {
                        Refuse(
                            mismatch: layout.PushedBlockMismatch(reflected: dxil.Read(container: container.Span)),
                            what: $"{what} {stage} DXIL"
                        );
                    }
                }
            }

            var echoed = new HashSet<string>(comparer: StringComparer.Ordinal);

            foreach (var planned in pipeline.Plan.Passes) {
                var layout = planned.Parameters.Layout;
                var generated = survey.Generated[planned.Name];

                Check(
                    layout: layout,
                    shader: pipeline.Shaders[planned.Name],
                    what: $"pass '{planned.Name}'"
                );

                if (!echoed.Add(item: generated.DeclarationsPath)) {
                    continue;
                }

                var echo = await m_compiler.CompileAsync(
                    cancellationToken: cancellationToken,
                    descriptor: new ShaderCompilationRequest(
                        generatedIncludes: new Dictionary<string, string>(comparer: PuckPaths.Comparer) { [generated.DeclarationsPath] = generated.DeclarationsText },
                        name: $"{layout.Interface.Name}.echo",
                        stages: [new ShaderStageSource(
                            EntryPoint: "main",
                            Path: Path.Combine(
                                path1: Path.GetDirectoryName(path: generated.DeclarationsPath)!,
                                path2: (layout.Interface.Name + ".echo.hlsl")
                            ),
                            Source: ShaderInterfaceEcho.Generate(shaderInterface: layout.Interface),
                            Stage: ShaderStage.Compute
                        )]
                    )
                ).ConfigureAwait(continueOnCapturedContext: false);

                if (!echo.IsSuccess) {
                    throw new ShaderClosureRefusedException(
                        code: ShaderClosureRefusedException.PackageInterface,
                        message: $"the echo pass of interface '{layout.Interface.Name}' does not compile: {string.Join(separator: " | ", values: echo.Diagnostics.Where(predicate: static diagnostic => diagnostic.IsError).Select(selector: static diagnostic => diagnostic.Message))}"
                    );
                }

                Check(
                    layout: layout,
                    shader: echo,
                    what: $"the echo pass of interface '{layout.Interface.Name}'"
                );
            }
        } finally {
            if (OperatingSystem.IsWindows()) {
                dxil?.Dispose();
            }
        }
    }
    private static void Refuse(string? mismatch, string what) {
        if (mismatch is not null) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageInterface,
                message: $"{what}: {mismatch}"
            );
        }
    }
    private bool Matches(CompiledShaderPipeline pipeline, PackageSurvey survey) {
        foreach (var shader in pipeline.Shaders.Values) {
            var identity = (shader.Identity ?? throw new InvalidOperationException(message: $"Pass '{shader.Name}' was not compiled from source."));

            foreach (var file in identity.Sources.Concat(second: identity.Includes)) {
                // A loader-synthesized stage, such as a fullscreen pass's vertex stage, names no file of the closure, and
                // generated declarations are no file of it either.
                if (
                    survey.Files.TryGetValue(
                        key: file.Path,
                        value: out var surveyed
                    ) &&
                    !string.Equals(
                        a: surveyed.Hash,
                        b: file.ContentHash,
                        comparisonType: StringComparison.Ordinal
                    )
                ) {
                    return false;
                }
            }

            if (identity.Includes.Any(predicate: include => (!survey.Files.ContainsKey(key: include.Path) && !survey.Declarations.Contains(item: include.Path)))) {
                return false;
            }
        }

        return true;
    }
    // Each pass's manifest entry, and the binaries it lists, from the compiled candidate and the survey's generated
    // interface and declarations.
    private static (string Compiler, IReadOnlyList<ShaderPackagePass> Passes) PassesOf(CompiledShaderPipeline pipeline, PackageSurvey survey, string rootPath, List<(string LogicalPath, byte[] Bytes)> binaries) {
        var identities = pipeline.Plan.Passes.Select(selector: pass => (pass.Name, Identity: (pipeline.Shaders[pass.Name].Identity ?? throw new InvalidOperationException(message: $"Pass '{pass.Name}' was not compiled from source.")))).ToArray();
        var compiler = identities[0].Identity.Compiler;

        if (identities.Any(predicate: entry => (entry.Identity.Compiler != compiler))) {
            throw new InvalidOperationException(message: "The passes of one candidate were compiled under different compiler revisions.");
        }

        var passes = new List<ShaderPackagePass>(capacity: identities.Length);

        foreach (var (name, identity) in identities) {
            var generated = survey.Generated[name];
            var shader = pipeline.Shaders[name];
            var passBinaries = new List<ShaderPackageBinary>();

            foreach (var stage in identity.Stages) {
                foreach (var (target, bytes) in ((ReadOnlySpan<(string, ReadOnlyMemory<byte>)>)[(ShaderPackageBinary.SpirvTarget, shader.SpirvByStage[stage.Stage]), (ShaderPackageBinary.DxilTarget, shader.DxilByStage[stage.Stage])])) {
                    var logicalPath = BinaryPath(
                        pass: name,
                        stage: stage.Stage,
                        target: target,
                        variant: ShaderPackageVariant.DefaultName
                    );

                    binaries.Add(item: (logicalPath, bytes.ToArray()));
                    passBinaries.Add(item: new ShaderPackageBinary(
                        Bytes: bytes.Length,
                        Path: logicalPath,
                        Pin: (ContentPin.Prefix + ContentPin.Compute(content: bytes.Span).Hex),
                        Stage: stage.Stage,
                        Target: target
                    ));
                }
            }

            passes.Add(item: new ShaderPackagePass(
                Declarations: Artifact(
                    path: generated.DeclarationsPath,
                    rootPath: rootPath,
                    text: generated.DeclarationsText
                ),
                Interface: Artifact(
                    path: generated.InterfacePath,
                    rootPath: rootPath,
                    text: generated.InterfaceText
                ),
                Name: name,
                Stages: identity.Stages,
                Variants: [new ShaderPackageVariant(
                    Binaries: passBinaries,
                    Name: ShaderPackageVariant.DefaultName
                )]
            ));
        }

        return (compiler, passes);
    }
    private static ShaderPackageFile Artifact(string path, string text, string rootPath) => new(
        Bytes: Encoding.UTF8.GetByteCount(s: text),
        Path: LogicalPath(
            path: path,
            rootPath: rootPath
        ),
        Pin: (ContentPin.Prefix + ShaderSourceClosure.HashOf(text: text))
    );
    // Where a binary lives inside a package: one file per pass, variant, stage and target under binaries/.
    private static string BinaryPath(string pass, string variant, ShaderStage stage, string target) {
        var path = $"binaries/{pass}.{variant}.{stage switch { ShaderStage.Vertex => "vert", ShaderStage.Fragment => "frag", _ => "comp" }}.{((target == ShaderPackageBinary.SpirvTarget) ? "spv" : "dxil")}";

        if (!IsLogicalPath(path: path)) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageClosure,
                message: $"pass '{pass}' names no file a package can hold its binaries in."
            );
        }

        return path;
    }
    private ShaderPipelinePlan Plan(string documentPath, string name) {
        var info = new FileInfo(fileName: documentPath);

        if (
            info.Exists &&
            (info.Length > m_limits.MaxFileBytes)
        ) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.FileBytes,
                message: $"'{documentPath}' holds {info.Length} bytes; one file may hold at most {m_limits.MaxFileBytes}."
            );
        }

        return new ShaderPipelineCompiler().Compile(definition: ShaderPipelineLoader.ReadDefinition(
            name: name,
            path: documentPath
        ));
    }
    // Reads one pass's default-variant binaries, each verified against its pin by Open.
    private static CompiledShader ReadBinaries(ShaderPipelinePlannedPass planned, ShaderPackagePass recorded, string rootPath, string sourcePath) {
        var variant = recorded.Variants.Single(predicate: static variant => string.Equals(
            a: variant.Name,
            b: ShaderPackageVariant.DefaultName,
            comparisonType: StringComparison.Ordinal
        ));
        var spirv = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var dxil = new Dictionary<ShaderStage, ReadOnlyMemory<byte>>();
        var stages = ShaderPipelineLoader.StagesOf(
            pass: planned.Declaration,
            source: string.Empty,
            sourcePath: sourcePath
        );

        if (!stages.Select(selector: static stage => (stage.Stage, stage.EntryPoint)).SequenceEqual(second: recorded.Stages.Select(selector: static stage => (stage.Stage, stage.EntryPoint)))) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageClosure,
                message: $"pass '{planned.Name}' compiles [{string.Join(separator: ", ", values: stages.Select(selector: static stage => $"{stage.Stage} {stage.EntryPoint}"))}]; the package holds binaries for [{string.Join(separator: ", ", values: recorded.Stages.Select(selector: static stage => $"{stage.Stage} {stage.EntryPoint}"))}]."
            );
        }

        foreach (var binary in variant.Binaries) {
            var bytes = File.ReadAllBytes(path: FullPath(
                logicalPath: binary.Path,
                rootPath: rootPath
            ));

            ((binary.Target == ShaderPackageBinary.SpirvTarget) ? spirv : dxil)[binary.Stage] = bytes;
        }

        return new CompiledShader(
            planned.Name,
            sourcePath,
            recorded.Declarations.Pin,
            spirv,
            dxil,
            []
        );
    }
    // The closure a package holds: the document, every pass source, and every include those reach, each collected the
    // way the compiler collects a compile's closure, checked whole against the package's limits; and, apart from it, each
    // pass's interface and the declarations generated from it.
    private PackageSurvey Survey(string documentPath, ShaderPipelinePlan plan, string rootPath) {
        var files = new Dictionary<string, (string Hash, long Bytes)>(comparer: PuckPaths.Comparer);
        var generatedByPass = new Dictionary<string, GeneratedArtifacts>(comparer: StringComparer.Ordinal);
        var declarations = new HashSet<string>(comparer: PuckPaths.Comparer);
        var directory = Path.GetDirectoryName(path: documentPath)!;
        var includes = new HashSet<string>(comparer: PuckPaths.Comparer);

        void Add(string path, string hash, string text) {
            _ = LogicalPath(
                path: path,
                rootPath: rootPath
            );
            files.TryAdd(
                key: path,
                value: (hash, Encoding.UTF8.GetByteCount(s: text))
            );
        }

        var documentText = File.ReadAllText(path: documentPath);
        var steps = new List<IReadOnlyList<ShaderCompileStep>>();

        Add(
            hash: ShaderSourceClosure.HashOf(text: documentText),
            path: documentPath,
            text: documentText
        );

        foreach (var pass in plan.Passes) {
            var sourcePath = Path.GetFullPath(
                basePath: directory,
                path: pass.Declaration.Source
            );
            var info = new FileInfo(fileName: sourcePath);

            if (
                info.Exists &&
                (info.Length > m_limits.MaxFileBytes)
            ) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.FileBytes,
                    message: $"'{sourcePath}' holds {info.Length} bytes; one file may hold at most {m_limits.MaxFileBytes}."
                );
            }

            var generated = ShaderPipelineLoader.GeneratedIncludeOf(
                pass: pass,
                sourcePath: sourcePath
            );

            _ = LogicalPath(
                path: generated.Path,
                rootPath: rootPath
            );
            declarations.Add(item: generated.Path);
            generatedByPass[pass.Name] = new GeneratedArtifacts(
                DeclarationsPath: generated.Path,
                DeclarationsText: generated.Text,
                InterfacePath: Path.ChangeExtension(
                    extension: ".json",
                    path: generated.Path
                ),
                InterfaceText: pass.Parameters.Interface.ToJson(),
                SourcePath: sourcePath
            );

            var sourceText = File.ReadAllText(path: sourcePath);
            var closure = ShaderSourceClosure.Collect(
                generated: new Dictionary<string, string>(comparer: PuckPaths.Comparer) { [generated.Path] = generated.Text },
                limits: m_limits,
                root: rootPath,
                sources: [(sourcePath, sourceText)]
            );

            steps.AddRange(collection: ShaderPipelineLoader.StagesOf(
                pass: pass.Declaration,
                source: sourceText,
                sourcePath: sourcePath
            ).Select(selector: static stage => ShaderCompiler.StepsOf(
                entryPoint: stage.EntryPoint,
                stage: stage.Stage
            )));

            foreach (var root in closure.Sources) {
                Add(
                    hash: root.ContentHash,
                    path: root.Path,
                    text: File.ReadAllText(path: root.Path)
                );
            }

            foreach (var include in closure.Includes) {
                if (PuckPaths.Comparer.Equals(
                    x: include.Path,
                    y: generated.Path
                )) {
                    continue;
                }

                includes.Add(item: include.Path);
                Add(
                    hash: include.ContentHash,
                    path: include.Path,
                    text: closure.Contents[include.Path]
                );
            }
        }

        var expanded = files.Values.Sum(selector: static file => file.Bytes);

        if (expanded > m_limits.MaxExpandedBytes) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.ExpandedBytes,
                message: $"the package's files hold {expanded} bytes; a closure may hold at most {m_limits.MaxExpandedBytes}."
            );
        }

        if (includes.Count > m_limits.MaxDependencies) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.DependencyCount,
                message: $"the package's passes reach {includes.Count} includes; a closure may reach at most {m_limits.MaxDependencies}."
            );
        }

        var work = steps.Sum(selector: static stage => stage.Count);

        if (work > m_limits.MaxCompileSteps) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.CompileSteps,
                message: $"the passes need {work} native tool runs; a package may need at most {m_limits.MaxCompileSteps}."
            );
        }

        return new PackageSurvey(
            Declarations: declarations,
            Files: files.ToDictionary(
                comparer: PuckPaths.Comparer,
                elementSelector: static pair => pair.Value,
                keySelector: static pair => pair.Key
            ),
            Generated: generatedByPass,
            Tools: steps.SelectMany(selector: static stage => stage).Select(selector: static step => step.Tool).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal).ToArray()
        );
    }
    private static void VerifyFiles(ShaderPackageManifest manifest, string rootPath, ShaderSourceLimits limits) {
        var total = 0L;

        void Verify(string logicalPath, string recordedPin, long recordedBytes, bool text) {
            var info = new FileInfo(fileName: FullPath(
                logicalPath: logicalPath,
                rootPath: rootPath
            ));

            if (!info.Exists) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.PackageFileMissing,
                    message: $"'{logicalPath}' is listed, and no file exists at '{info.FullName}'."
                );
            }

            if (info.Length > limits.MaxFileBytes) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.FileBytes,
                    message: $"'{logicalPath}' holds {info.Length} bytes; one file may hold at most {limits.MaxFileBytes}."
                );
            }

            total += info.Length;

            if (total > limits.MaxExpandedBytes) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.ExpandedBytes,
                    message: $"the package's files hold more than {limits.MaxExpandedBytes} bytes once '{logicalPath}' is read."
                );
            }

            // A source's pin covers the UTF-8 text the compiler reads; a binary's covers its bytes.
            var pin = (ContentPin.Prefix + (text
                ? ShaderSourceClosure.HashOf(text: File.ReadAllText(path: info.FullName))
                : ContentPin.Compute(content: File.ReadAllBytes(path: info.FullName)).Hex));

            if (
                (info.Length != recordedBytes) ||
                !string.Equals(
                    a: pin,
                    b: recordedPin,
                    comparisonType: StringComparison.Ordinal
                )
            ) {
                throw new ShaderClosureRefusedException(
                    code: ShaderClosureRefusedException.PackageFilePin,
                    message: $"'{logicalPath}' holds {info.Length} bytes pinned {pin}; the package lists {recordedBytes} bytes pinned {recordedPin}."
                );
            }
        }

        foreach (var file in manifest.Files) {
            Verify(
                logicalPath: file.Path,
                recordedBytes: file.Bytes,
                recordedPin: file.Pin,
                text: true
            );
        }

        var seen = new HashSet<string>(comparer: StringComparer.Ordinal);

        foreach (var pass in manifest.Passes) {
            foreach (var artifact in ((ReadOnlySpan<ShaderPackageFile>)[pass.Interface, pass.Declarations])) {
                if (seen.Add(item: artifact.Path)) {
                    Verify(
                        logicalPath: artifact.Path,
                        recordedBytes: artifact.Bytes,
                        recordedPin: artifact.Pin,
                        text: true
                    );
                }
            }

            foreach (var binary in pass.Variants.SelectMany(selector: static variant => variant.Binaries)) {
                Verify(
                    logicalPath: binary.Path,
                    recordedBytes: binary.Bytes,
                    recordedPin: binary.Pin,
                    text: false
                );
            }
        }
    }
    // Stages the package beside the output and replaces the output only once it is complete, so a failure leaves the
    // last complete package in place.
    private static bool WritePackage(ShaderPackageManifest manifest, string outputPath, string rootPath, PackageSurvey survey, IReadOnlyList<(string LogicalPath, byte[] Bytes)> binaries) {
        if (File.Exists(path: outputPath)) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageOutput,
                message: $"'{outputPath}' is a file."
            );
        }

        if (
            Directory.Exists(path: outputPath) &&
            Directory.EnumerateFileSystemEntries(path: outputPath).Any() &&
            !File.Exists(path: Path.Combine(
                path1: outputPath,
                path2: ShaderPackageManifest.FileName
            ))
        ) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageOutput,
                message: $"'{outputPath}' holds files and no {ShaderPackageManifest.FileName}, so it is not a package to replace."
            );
        }

        var suffix = Guid.NewGuid().ToString(format: "N")[..12];
        var staging = $"{outputPath}.partial-{suffix}";
        var replaced = $"{outputPath}.replaced-{suffix}";
        var sources = survey.Files.Keys.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static path => path,
            keySelector: path => LogicalPath(
                path: path,
                rootPath: rootPath
            )
        );

        void Place(string logicalPath, byte[] bytes) {
            var destination = FullPath(
                logicalPath: logicalPath,
                rootPath: staging
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.WriteAllBytes(
                bytes: bytes,
                path: destination
            );
        }

        try {
            foreach (var file in manifest.Files) {
                var destination = FullPath(
                    logicalPath: file.Path,
                    rootPath: staging
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
                File.Copy(
                    destFileName: destination,
                    sourceFileName: sources[file.Path]
                );

                // The copy is the package's content, so a source edited since it was compiled fails here.
                if (!string.Equals(
                    a: (ContentPin.Prefix + ShaderSourceClosure.HashOf(text: File.ReadAllText(path: destination))),
                    b: file.Pin,
                    comparisonType: StringComparison.Ordinal
                )) {
                    return false;
                }
            }

            foreach (var generated in survey.Generated.Values.DistinctBy(keySelector: static generated => generated.DeclarationsPath)) {
                Place(
                    bytes: Encoding.UTF8.GetBytes(s: generated.DeclarationsText),
                    logicalPath: LogicalPath(
                        path: generated.DeclarationsPath,
                        rootPath: rootPath
                    )
                );
                Place(
                    bytes: Encoding.UTF8.GetBytes(s: generated.InterfaceText),
                    logicalPath: LogicalPath(
                        path: generated.InterfacePath,
                        rootPath: rootPath
                    )
                );
            }

            foreach (var (logicalPath, bytes) in binaries) {
                Place(
                    bytes: bytes,
                    logicalPath: logicalPath
                );
            }

            File.WriteAllBytes(
                bytes: Write(manifest: manifest),
                path: Path.Combine(
                    path1: staging,
                    path2: ShaderPackageManifest.FileName
                )
            );

            if (Directory.Exists(path: outputPath)) {
                Directory.Move(
                    destDirName: replaced,
                    sourceDirName: outputPath
                );
            }

            try {
                Directory.Move(
                    destDirName: outputPath,
                    sourceDirName: staging
                );
            } catch (IOException) when (Directory.Exists(path: replaced)) {
                Directory.Move(
                    destDirName: outputPath,
                    sourceDirName: replaced
                );
                throw;
            }
        } finally {
            if (Directory.Exists(path: staging)) {
                Directory.Delete(
                    path: staging,
                    recursive: true
                );
            }
        }

        if (Directory.Exists(path: replaced)) {
            Directory.Delete(
                path: replaced,
                recursive: true
            );
        }

        return true;
    }

    // One pass's generated artifacts: its declarations at the path its source includes them from, its interface's
    // canonical JSON beside them, and the source they resolve beside.
    private sealed record GeneratedArtifacts(string DeclarationsPath, string DeclarationsText, string InterfacePath, string InterfaceText, string SourcePath);
    private sealed record PackageSurvey(
        IReadOnlyDictionary<string, (string Hash, long Bytes)> Files,
        IReadOnlySet<string> Declarations,
        IReadOnlyDictionary<string, GeneratedArtifacts> Generated,
        IReadOnlyList<string> Tools
    );
}
