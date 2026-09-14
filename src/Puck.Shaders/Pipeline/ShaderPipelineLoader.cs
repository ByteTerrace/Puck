using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>A complete source-load outcome. Failed loads retain dependencies for watched recovery.</summary>
public sealed record ShaderPipelineLoadResult(CompiledShaderPipeline? Pipeline, IReadOnlyList<string> Dependencies, string Message, bool RetryRecommended = false);
/// <summary>Loads a pipeline document or a one-off shader into the same planned, compiled candidate.
/// No GPU objects are created here; callers may load candidates on a background worker.</summary>
public sealed class ShaderPipelineLoader {
    private static StringComparer PathComparer => (OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal
    );

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

    private readonly ShaderCompiler m_compiler;
    private readonly ShaderPipelineCompiler m_planner = new();

    /// <summary>Creates a source loader over the shared cross-backend compiler.</summary>
    public ShaderPipelineLoader(ShaderCompiler compiler) {
        ArgumentNullException.ThrowIfNull(compiler);
        m_compiler = compiler;
    }

    private static GpuComputeBindingKind DescriptorKind(ShaderPipelineResource resource, bool output) =>
        (output
            ? ((resource.Kind == ShaderPipelineResourceKind.Image)
                ? GpuComputeBindingKind.StorageImage
                : GpuComputeBindingKind.StorageBufferReadWrite)
            : ((resource.Kind == ShaderPipelineResourceKind.Image)
                ? GpuComputeBindingKind.SampledImage
                : GpuComputeBindingKind.StorageBufferRead
        ));
    private static string SourceHash(string text) => Convert.ToHexStringLower(inArray: SHA256.HashData(source: Encoding.UTF8.GetBytes(s: text)));
    private static bool SourcesMatch(IReadOnlyDictionary<string, string> hashes) {
        try {
            foreach (var (path, expected) in hashes) {
                if (!string.Equals(
                    a: SourceHash(text: File.ReadAllText(path: path)),
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
        var dependencies = new HashSet<string>(comparer: PathComparer) { path };
        var sourceTexts = new Dictionary<string, string>(comparer: PathComparer);
        var hashes = new Dictionary<string, string>(comparer: PathComparer);

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
                value: SourceHash(text: text)
            );
            return text;
        }
        ShaderPipelineLoadResult Changed() => new(
            null,
            dependencies.ToArray(),
            "Source changed during compilation; retrying the complete pipeline.",
            RetryRecommended: true
        );
        try {
            cancellationToken.ThrowIfCancellationRequested();
            var rootText = Capture(sourcePath: path);
            var definition = (Path.GetExtension(path: path).Equals(
                comparisonType: StringComparison.OrdinalIgnoreCase,
                value: ".json"
            )
                ? (JsonSerializer.Deserialize(
                    rootText,
                    ShaderPipelineJsonContext.Default.ShaderPipelineDefinition
                )
                    ?? throw new InvalidDataException(message: $"Pipeline '{path}' is null."))
                : ShaderPipelineDefinition.FromShaderSource(
                    name,
                    path
                )
            );
            var plan = m_planner.Compile(definition);
            var shaders = new Dictionary<string, CompiledShader>(comparer: StringComparer.Ordinal);
            var diagnostics = new List<string>();
            var directory = Path.GetDirectoryName(path: path)!;
            var resources = plan.Resources.ToDictionary(
                static resource => resource.Name,
                StringComparer.Ordinal
            );
            // Capture every root stage before invoking tools. A candidate cannot combine different revisions
            // of a file reused by several passes; includes are snapshotted by the source compiler.
            foreach (var pass in plan.Passes) {
                Capture(sourcePath: Path.GetFullPath(
                pass.Declaration.Source,
                directory
            ));
            }
            foreach (var planned in plan.Passes) {
                cancellationToken.ThrowIfCancellationRequested();
                var pass = planned.Declaration;
                var sourcePath = Path.GetFullPath(
                    pass.Source,
                    directory
                );

                dependencies.Add(item: sourcePath);
                var text = sourceTexts[sourcePath];
                var channels = new Dictionary<string, uint>(comparer: StringComparer.Ordinal);
                var descriptorBindings = new List<ShaderDescriptorBinding>(capacity: (pass.InputReferences.Count + pass.OutputReferences.Count));

                for (var index = 0; (index < pass.InputReferences.Count); index++) {
                    var input = pass.InputReferences[index];

                    channels.Add(
                        key: $"iChannel{index}",
                        value: input.Binding!.Value
                    );
                    descriptorBindings.Add(item: new ShaderDescriptorBinding(
                        input.Binding.Value,
                        DescriptorKind(
                            resources[input.Name].Declaration,
                            output: false
                        )
                    ));
                }
                if (pass.Kind == ShaderPipelinePassKind.Compute) {
                    foreach (var output in pass.OutputReferences) {
                        descriptorBindings.Add(item: new ShaderDescriptorBinding(
                            output.Binding!.Value,
                            DescriptorKind(
                                resources[output.Name].Declaration,
                                output: true
                            )
                        ));
                    }
                }
                IReadOnlyList<ShaderStageSource> stages;

                if (pass.Kind == ShaderPipelinePassKind.Compute) {
                    stages = [new ShaderStageSource(
                            ShaderStage.Compute,
                            sourcePath,
                            text,
                            pass.Language,
                            pass.EntryPoint,
                            pass.GroupSizeX,
                            pass.GroupSizeY,
                            pass.GroupSizeZ
                        )];
                } else {
                    stages = [new ShaderStageSource(
                            ShaderStage.Vertex,
                            (sourcePath + ".fullscreen.hlsl"),
                            FullscreenVertex,
                            ShaderSourceLanguage.Hlsl,
                            "main"
                        ),
                        new ShaderStageSource(
                            ShaderStage.Fragment,
                            sourcePath,
                            text,
                            pass.Language,
                            pass.EntryPoint
                        )];
                }
                var outputFormat = GpuPixelFormat.R8G8B8A8Unorm;

                if (pass.OutputReferences.Count > 0) {
                    var outputResource = resources[pass.OutputReferences[0].Name].Declaration;

                    if (
                        (outputResource.Kind == ShaderPipelineResourceKind.Image) &&
                        !Enum.TryParse(
                        outputResource.Format,
                        ignoreCase: true,
                        out outputFormat
                    )
                    ) {
                        throw new InvalidDataException(message: $"Pass '{pass.Name}' has unknown output format '{outputResource.Format}'.");
                    }
                }
                var request = new ShaderCompilationRequest(
                    pass.Name,
                    stages,
                    channels,
                    outputFormat,
                    pass.Config,
                    descriptorBindings
                );
                var shader = m_compiler.CompileAsync(
                    cancellationToken: cancellationToken,
                    descriptor: request
                ).GetAwaiter().GetResult();

                foreach (var dependency in shader.Dependencies) {
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
                        null,
                        dependencies.ToArray(),
                        $"failed pass '{pass.Name}': {string.Join(
                            separator: " | ",
                            values: diagnostics
                        )}"
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
                values: plan.Outputs.Select(selector: output => output.Name)
            )}";

            if (diagnostics.Count != 0) {
                message += $"; {string.Join(
                separator: " | ",
                values: diagnostics
            )}";
            }
            return new ShaderPipelineLoadResult(
                candidate,
                dependencies.ToArray(),
                message
            );
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or ShaderPipelineCompilationException or ShaderToolMissingException)) {
            return new ShaderPipelineLoadResult(
                null,
                dependencies.ToArray(),
                exception.Message
            );
        }
    }
    /// <summary>Reads a document using trim-safe metadata, or synthesizes a one-pass definition for a source file.</summary>
    public ShaderPipelineDefinition ReadDefinition(string name, string path) {
        path = Path.GetFullPath(path: path);
        if (Path.GetExtension(path: path).Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: ".json"
        )) {
            return (JsonSerializer.Deserialize(
                File.ReadAllText(path: path),
                ShaderPipelineJsonContext.Default.ShaderPipelineDefinition
            )
                ?? throw new InvalidDataException(message: $"Pipeline '{path}' is null."));
        }
        return ShaderPipelineDefinition.FromShaderSource(
            name,
            path
        );
    }
}
