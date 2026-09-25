using System.Text.Json;
using Puck.Abstractions;

namespace Puck.Shaders;

/// <summary>How a source load ended.</summary>
public enum ShaderPipelineLoadStatus : byte {
    /// <summary>Every planned pass compiled; <see cref="ShaderPipelineLoadResult.Pipeline"/> holds the candidate.</summary>
    Compiled = 1,
    /// <summary>The document, a source, or a pass was refused; the message names why.</summary>
    Failed = 2,
    /// <summary>A source changed while the candidate compiled; loading the complete pipeline again is expected to
    /// succeed.</summary>
    Retry = 3,
    /// <summary>A required shader tool is absent from this environment, so no candidate can compile here.</summary>
    Unsupported = 4,
}
/// <summary>A complete source-load outcome. Failed loads retain dependencies for watched recovery.</summary>
/// <param name="Status">How the load ended. <see cref="Pipeline"/> is non-null exactly when this is
/// <see cref="ShaderPipelineLoadStatus.Compiled"/>.</param>
/// <param name="Pipeline">The compiled candidate, or <see langword="null"/>.</param>
/// <param name="Dependencies">Every file the load read, including those of a failed pass.</param>
/// <param name="Message">The summary or the refusal reason.</param>
public sealed record ShaderPipelineLoadResult(ShaderPipelineLoadStatus Status, CompiledShaderPipeline? Pipeline, IReadOnlyList<string> Dependencies, string Message);
/// <summary>Loads a graph document or a one-off shader into the same planned, compiled candidate.
/// No GPU objects are created here; callers may load candidates on a background worker.</summary>
public sealed class ShaderPipelineLoader {
    // A fullscreen triangle has no vertex buffer. Its UV convention is top-left, matching pipeline images.
    private const string FullscreenVertex = """
        struct VertexOutput { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        VertexOutput main(uint vertex : SV_VertexID) {
            VertexOutput result;
            result.uv = float2((vertex << 1) & 2, vertex & 2);
            result.position = float4(result.uv * float2(2, -2) + float2(-1, 1), 0, 1);
            return result;
        }
        """;
    // The same triangle read from the shared FullscreenTriangle vertex buffer's POSITION attribute. The UV is the exact
    // inverse of FullscreenVertex's position mapping, so a fragment stage sees one convention from either adapter.
    private const string FullscreenPositionVertex = """
        struct VertexOutput { float4 position : SV_Position; float2 uv : TEXCOORD0; };
        VertexOutput main(float2 position : POSITION) {
            VertexOutput result;
            result.uv = float2((position.x + 1) * 0.5, (1 - position.y) * 0.5);
            result.position = float4(position, 0, 1);
            return result;
        }
        """;

    private readonly ShaderCompiler m_compiler;

    /// <summary>Creates a source loader over the shared cross-backend compiler.</summary>
    public ShaderPipelineLoader(ShaderCompiler compiler) {
        ArgumentNullException.ThrowIfNull(compiler);
        m_compiler = compiler;
    }

    private static bool SourcesMatch(IReadOnlyDictionary<string, string> hashes) {
        try {
            foreach (var (path, expected) in hashes) {
                if (!string.Equals(
                    a: ShaderSourceClosure.HashOf(text: File.ReadAllText(path: path)),
                    b: expected,
                    comparisonType: StringComparison.Ordinal
                )) { return false; }
            }
            return true;
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>Loads and compiles every planned pass. A failure in any pass refuses the entire candidate.</summary>
    public ShaderPipelineLoadResult Load(string name, string path, CancellationToken cancellationToken = default) {
        path = Path.GetFullPath(path: path);
        var dependencies = new HashSet<string>(comparer: PuckPaths.Comparer) { path };
        var sourceTexts = new Dictionary<string, string>(comparer: PuckPaths.Comparer);
        var hashes = new Dictionary<string, string>(comparer: PuckPaths.Comparer);

        string Capture(string sourcePath) {
            dependencies.Add(item: sourcePath);
            if (sourceTexts.TryGetValue(
                key: sourcePath,
                value: out var captured
            )) { return captured; }
            var text = File.ReadAllText(path: sourcePath);

            sourceTexts.Add(
                key: sourcePath,
                value: text
            );
            hashes.Add(
                key: sourcePath,
                value: ShaderSourceClosure.HashOf(text: text)
            );
            return text;
        }
        ShaderPipelineLoadResult Changed() => new(
            Dependencies: dependencies.ToArray(),
            Message: "Source changed during compilation; retrying the complete pipeline.",
            Pipeline: null,
            Status: ShaderPipelineLoadStatus.Retry
        );
        try {
            cancellationToken.ThrowIfCancellationRequested();
            var definition = ParseDefinition(
                name: name,
                path: path,
                text: Capture(sourcePath: path)
            );
            var plan = RenderGraphCompiler.ShaderPasses.Compile(definition: definition).Pipeline;
            var shaders = new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal);
            var diagnostics = new List<string>();
            var directory = Path.GetDirectoryName(path: path)!;
            // Capture every root stage before invoking tools. A candidate cannot combine different revisions
            // of a file reused by several passes; includes are snapshotted by the source compiler. A graph over no package
            // plans shader passes alone, so every planned pass has its declaration.
            foreach (var pass in plan.Passes) {
                Capture(sourcePath: Path.GetFullPath(
                pass.Declaration!.Source,
                directory
            ));
            }
            foreach (var planned in plan.Passes) {
                cancellationToken.ThrowIfCancellationRequested();
                var pass = planned.Declaration!;
                var sourcePath = Path.GetFullPath(
                    pass.Source,
                    directory
                );

                dependencies.Add(item: sourcePath);
                var generated = GeneratedIncludeOf(
                    pass: planned,
                    sourcePath: sourcePath
                );
                var request = new ShaderCompilationRequest(
                    generatedIncludes: new Dictionary<string, string>(comparer: PuckPaths.Comparer) { [generated.Path] = generated.Text },
                    name: pass.Name,
                    stages: StagesOf(
                        pass: pass,
                        source: sourceTexts[sourcePath],
                        sourcePath: sourcePath
                    )
                );
                var shader = m_compiler.CompileAsync(
                    cancellationToken: cancellationToken,
                    descriptor: request
                ).GetAwaiter().GetResult();

                foreach (var dependency in shader.Dependencies) {
                    // The generated declarations are a function of the document, which is already watched.
                    if (PuckPaths.Comparer.Equals(
                        x: dependency.Path,
                        y: generated.Path
                    )) {
                        continue;
                    }

                    dependencies.Add(item: dependency.Path);
                    if (
                        hashes.TryGetValue(
                        key: dependency.Path,
                        value: out var before
                    ) &&
                        (before != dependency.ContentHash)
                    ) { return Changed(); }
                    hashes[dependency.Path] = dependency.ContentHash;
                }
                foreach (var diagnostic in shader.Diagnostics) {
                    diagnostics.Add(item: $"{(diagnostic.Path ?? sourcePath)}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}");
                }
                if (!shader.IsSuccess) {
                    if (!SourcesMatch(hashes: hashes)) { return Changed(); }
                    return new ShaderPipelineLoadResult(
                        Dependencies: dependencies.ToArray(),
                        Message: $"failed pass '{pass.Name}': {string.Join(
                            separator: " | ",
                            values: diagnostics
                        )}",
                        Pipeline: null,
                        Status: ShaderPipelineLoadStatus.Failed
                    );
                }
                shaders.Add(
                    key: pass.Name,
                    value: shader
                );
            }
            if (!SourcesMatch(hashes: hashes)) { return Changed(); }
            var candidate = new CompiledShaderPipeline(
                plan: plan,
                shaders: shaders
            );
            var message = $"compiled: {plan.Passes.Count} passes; outputs={string.Join(
                separator: ",",
                values: plan.Outputs
            )}";

            if (diagnostics.Count != 0) {
                message += $"; {string.Join(
                separator: " | ",
                values: diagnostics
            )}";
            }
            return new ShaderPipelineLoadResult(
                Dependencies: dependencies.ToArray(),
                Message: message,
                Pipeline: candidate,
                Status: ShaderPipelineLoadStatus.Compiled
            );
        } catch (ShaderToolMissingException exception) {
            return new ShaderPipelineLoadResult(
                Dependencies: dependencies.ToArray(),
                Message: exception.Message,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Unsupported
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or ShaderPipelineCompilationException)) {
            return new ShaderPipelineLoadResult(
                Dependencies: dependencies.ToArray(),
                Message: exception.Message,
                Pipeline: null,
                Status: ShaderPipelineLoadStatus.Failed
            );
        }
    }
    /// <summary>Reads a document using trim-safe metadata, or synthesizes a one-pass definition for a source file.</summary>
    /// <param name="name">The instance name, which names a one-off shader's pipeline and its one pass.</param>
    /// <param name="path">The path of the graph document or one-off shader.</param>
    /// <returns>The definition.</returns>
    public static RenderGraphDefinition ReadDefinition(string name, string path) {
        path = Path.GetFullPath(path: path);

        return ParseDefinition(
            name: name,
            path: path,
            text: File.ReadAllText(path: path)
        );
    }
    /// <summary>Parses the definition a source declares: a <c>.json</c> path as a <c>puck.render.graph.v1</c> document,
    /// an <c>.hlsl</c> path as a one-off shader forming the one-pass graph
    /// <see cref="RenderGraphDefinition.FromShaderSource"/> makes. It is the one rule every reader of a pipeline source
    /// applies.</summary>
    /// <param name="name">The instance name, which names a one-off shader's pipeline and its one pass.</param>
    /// <param name="path">The full path of the source.</param>
    /// <param name="text">The source's text, read by the caller so the definition and any hash of it describe one
    /// read.</param>
    /// <returns>The definition.</returns>
    /// <exception cref="JsonException">A graph document is malformed.</exception>
    /// <exception cref="InvalidDataException">A graph document is <c>null</c>, or <paramref name="path"/> names a
    /// package's manifest rather than its directory.</exception>
    /// <exception cref="ArgumentException">A one-off shader's extension names no supported stage.</exception>
    public static RenderGraphDefinition ParseDefinition(string name, string path, string text) {
        ArgumentNullException.ThrowIfNull(argument: text);

        if (string.Equals(
            a: Path.GetFileName(path: path),
            b: ShaderPackageManifest.FileName,
            comparisonType: StringComparison.OrdinalIgnoreCase
        )) {
            throw new InvalidDataException(message: $"'{path}' is a package manifest; a package is named by its directory.");
        }
        if (!Path.GetExtension(path: path).Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".json"
        )) {
            return RenderGraphDefinition.FromShaderSource(
                name: name,
                sourcePath: path
            );
        }

        return RenderGraphDefinition.Parse(json: text);
    }
    /// <summary>Returns the declarations a pass's source includes to read its frame block: the text
    /// <see cref="ShaderInterfaceHlsl"/> generates from the pass's interface, at the path
    /// <see cref="ShaderFrameInterface.IncludeFileName"/> names beside the source. It is the one statement of where a
    /// pass's generated declarations live; <see cref="Load"/> compiles against exactly this text and
    /// <see cref="ShaderPackager"/> carries it.</summary>
    /// <param name="pass">The planned pass.</param>
    /// <param name="sourcePath">The full path of the pass's source.</param>
    /// <returns>The include's full path and text.</returns>
    public static (string Path, string Text) GeneratedIncludeOf(ShaderPipelinePlannedPass pass, string sourcePath) {
        ArgumentNullException.ThrowIfNull(argument: pass);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: sourcePath);

        var shaderInterface = pass.Parameters.Interface;

        return (Path.GetFullPath(path: Path.Combine(
            path1: Path.GetDirectoryName(path: Path.GetFullPath(path: sourcePath))!,
            path2: ShaderFrameInterface.IncludeFileName(interfaceName: shaderInterface.Name)
        )), ShaderInterfaceHlsl.Generate(shaderInterface: shaderInterface));
    }
    /// <summary>Returns the stages a pass compiles, in compile order: a compute pass's one compute stage, a fullscreen
    /// pass's loader-supplied HLSL vertex stage followed by its fragment stage, or a geometry pass's vertex and fragment
    /// stages, both compiled from its source at their own entry points. It is the one statement of that rule;
    /// <see cref="Load"/> compiles exactly these stages and <see cref="ShaderPackager"/> records their tool steps.</summary>
    /// <param name="pass">The pass.</param>
    /// <param name="sourcePath">The full path of the pass's source.</param>
    /// <param name="source">The pass source's text.</param>
    /// <returns>The stages. A synthesized vertex stage's path is the pass source's path with a
    /// <c>.fullscreen.hlsl</c> or <c>.fullscreen-position.hlsl</c> suffix, a file that does not exist.</returns>
    public static IReadOnlyList<ShaderStageSource> StagesOf(ShaderPipelinePass pass, string sourcePath, string source) {
        ArgumentNullException.ThrowIfNull(argument: pass);

        if (pass.Kind == ShaderPipelineDocumentPassKind.Compute) {
            return [new ShaderStageSource(
                EntryPoint: pass.EntryPoint,
                Path: sourcePath,
                Source: source,
                Stage: ShaderStage.Compute
            )];
        }
        if (pass.Kind == ShaderPipelineDocumentPassKind.Geometry) {
            return [
                new ShaderStageSource(
                    EntryPoint: (pass.Geometry?.VertexEntryPoint ?? throw new InvalidDataException(message: $"Geometry pass '{pass.Name}' declares no geometry.")),
                    Path: sourcePath,
                    Source: source,
                    Stage: ShaderStage.Vertex
                ),
                new ShaderStageSource(
                    EntryPoint: pass.EntryPoint,
                    Path: sourcePath,
                    Source: source,
                    Stage: ShaderStage.Fragment
                ),
            ];
        }

        var position = (pass.Vertex == ShaderPipelineVertexInput.Position);

        return [
            new ShaderStageSource(
                EntryPoint: "main",
                Path: (sourcePath + (position
                    ? ".fullscreen-position.hlsl"
                    : ".fullscreen.hlsl")),
                Source: (position
                    ? FullscreenPositionVertex
                    : FullscreenVertex),
                Stage: ShaderStage.Vertex
            ),
            new ShaderStageSource(
                EntryPoint: pass.EntryPoint,
                Path: sourcePath,
                Source: source,
                Stage: ShaderStage.Fragment
            ),
        ];
    }
}
