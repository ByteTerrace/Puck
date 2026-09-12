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
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly ShaderCompiler m_compiler;
    private readonly ShaderPipelineCompiler m_planner = new();
    /// <summary>Creates a source loader over the shared cross-backend compiler.</summary>
    public ShaderPipelineLoader(ShaderCompiler compiler) {
        ArgumentNullException.ThrowIfNull(compiler);
        m_compiler = compiler;
    }
    /// <summary>Reads a document using trim-safe metadata, or synthesizes a one-pass definition for a source file.</summary>
    public ShaderPipelineDefinition ReadDefinition(string name, string path) {
        path = Path.GetFullPath(path);
        if (Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)) {
            return JsonSerializer.Deserialize(File.ReadAllText(path), ShaderPipelineJsonContext.Default.ShaderPipelineDefinition)
                ?? throw new InvalidDataException($"Pipeline '{path}' is null.");
        }
        return ShaderPipelineDefinition.FromShaderSource(name, path);
    }
    /// <summary>Loads and compiles every planned pass. A failure in any pass refuses the entire candidate.</summary>
    public ShaderPipelineLoadResult Load(string name, string path, CancellationToken cancellationToken = default) {
        path = Path.GetFullPath(path);
        var dependencies = new HashSet<string>(PathComparer) { path };
        var sourceTexts = new Dictionary<string, string>(PathComparer);
        var hashes = new Dictionary<string, string>(PathComparer);
        string Capture(string sourcePath) {
            dependencies.Add(sourcePath);
            if (sourceTexts.TryGetValue(sourcePath, out var captured)) { return captured; }
            var text = File.ReadAllText(sourcePath);
            sourceTexts.Add(sourcePath, text);
            hashes.Add(sourcePath, SourceHash(text));
            return text;
        }
        ShaderPipelineLoadResult Changed() => new(null, dependencies.ToArray(), "Source changed during compilation; retrying the complete pipeline.", RetryRecommended: true);
        try {
            cancellationToken.ThrowIfCancellationRequested();
            var rootText = Capture(path);
            var definition = Path.GetExtension(path).Equals(".json", StringComparison.OrdinalIgnoreCase)
                ? JsonSerializer.Deserialize(rootText, ShaderPipelineJsonContext.Default.ShaderPipelineDefinition)
                    ?? throw new InvalidDataException($"Pipeline '{path}' is null.")
                : ShaderPipelineDefinition.FromShaderSource(name, path);
            var plan = m_planner.Compile(definition);
            var shaders = new Dictionary<string, CompiledShader>(StringComparer.Ordinal);
            var diagnostics = new List<string>();
            var directory = Path.GetDirectoryName(path)!;
            var resources = plan.Resources.ToDictionary(static resource => resource.Name, StringComparer.Ordinal);
            // Capture every root stage before invoking tools. A candidate cannot combine different revisions
            // of a file reused by several passes; includes are snapshotted by the source compiler.
            foreach (var pass in plan.Passes) { Capture(Path.GetFullPath(pass.Declaration.Source, directory)); }
            foreach (var planned in plan.Passes) {
                cancellationToken.ThrowIfCancellationRequested();
                var pass = planned.Declaration;
                var sourcePath = Path.GetFullPath(pass.Source, directory);
                dependencies.Add(sourcePath);
                var text = sourceTexts[sourcePath];
                var channels = new Dictionary<string, uint>(StringComparer.Ordinal);
                var descriptorBindings = new List<ShaderDescriptorBinding>(pass.InputReferences.Count + pass.OutputReferences.Count);
                for (var index = 0; index < pass.InputReferences.Count; index++) {
                    var input = pass.InputReferences[index];
                    channels.Add($"iChannel{index}", input.Binding!.Value);
                    descriptorBindings.Add(new ShaderDescriptorBinding(input.Binding.Value, DescriptorKind(resources[input.Name].Declaration, output: false)));
                }
                if (pass.Kind == ShaderPipelinePassKind.Compute) {
                    foreach (var output in pass.OutputReferences) {
                        descriptorBindings.Add(new ShaderDescriptorBinding(output.Binding!.Value, DescriptorKind(resources[output.Name].Declaration, output: true)));
                    }
                }
                IReadOnlyList<ShaderStageSource> stages;
                if (pass.Kind == ShaderPipelinePassKind.Compute) {
                    stages = [new ShaderStageSource(ShaderStage.Compute, sourcePath, text, pass.Language, pass.EntryPoint, pass.GroupSizeX, pass.GroupSizeY, pass.GroupSizeZ)];
                } else {
                    stages = [new ShaderStageSource(ShaderStage.Vertex, sourcePath + ".fullscreen.hlsl", FullscreenVertex, ShaderSourceLanguage.Hlsl, "main"),
                        new ShaderStageSource(ShaderStage.Fragment, sourcePath, text, pass.Language, pass.EntryPoint)];
                }
                var outputFormat = GpuPixelFormat.R8G8B8A8Unorm;
                if (pass.OutputReferences.Count > 0) {
                    var outputResource = resources[pass.OutputReferences[0].Name].Declaration;
                    if (outputResource.Kind == ShaderPipelineResourceKind.Image &&
                        !Enum.TryParse(outputResource.Format, ignoreCase: true, out outputFormat)) {
                        throw new InvalidDataException($"Pass '{pass.Name}' has unknown output format '{outputResource.Format}'.");
                    }
                }
                var request = new ShaderCompilationRequest(pass.Name, stages, channels, outputFormat, pass.Config, descriptorBindings);
                var shader = m_compiler.CompileAsync(request, cancellationToken).GetAwaiter().GetResult();
                foreach (var dependency in shader.Dependencies) {
                    dependencies.Add(dependency.Path);
                    if (hashes.TryGetValue(dependency.Path, out var before) && before != dependency.ContentHash) { return Changed(); }
                    hashes[dependency.Path] = dependency.ContentHash;
                }
                foreach (var diagnostic in shader.Diagnostics) {
                    diagnostics.Add($"{diagnostic.Path ?? sourcePath}:{diagnostic.Line}:{diagnostic.Column}: {diagnostic.Message}");
                }
                if (!shader.IsSuccess) {
                    if (!SourcesMatch(hashes)) { return Changed(); }
                    return new ShaderPipelineLoadResult(null, dependencies.ToArray(), $"failed pass '{pass.Name}': {string.Join(" | ", diagnostics)}");
                }
                shaders.Add(pass.Name, shader);
            }
            if (!SourcesMatch(hashes)) { return Changed(); }
            var candidate = new CompiledShaderPipeline(plan, shaders);
            var message = $"compiled: {plan.Passes.Count} passes; outputs={string.Join(",", plan.Outputs.Select(output => output.Name))}";
            if (diagnostics.Count != 0) { message += $"; {string.Join(" | ", diagnostics)}"; }
            return new ShaderPipelineLoadResult(candidate, dependencies.ToArray(), message);
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException or ShaderPipelineCompilationException or ShaderToolMissingException) {
            return new ShaderPipelineLoadResult(null, dependencies.ToArray(), exception.Message);
        }
    }

    private static GpuComputeBindingKind DescriptorKind(ShaderPipelineResource resource, bool output) =>
        output
            ? (resource.Kind == ShaderPipelineResourceKind.Image ? GpuComputeBindingKind.StorageImage : GpuComputeBindingKind.StorageBufferReadWrite)
            : (resource.Kind == ShaderPipelineResourceKind.Image ? GpuComputeBindingKind.SampledImage : GpuComputeBindingKind.StorageBufferRead);
    private static string SourceHash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static bool SourcesMatch(IReadOnlyDictionary<string, string> hashes) {
        try {
            foreach (var (path, expected) in hashes) {
                if (!string.Equals(SourceHash(File.ReadAllText(path)), expected, StringComparison.Ordinal)) { return false; }
            }
            return true;
        } catch (IOException) {
            return false;
        } catch (UnauthorizedAccessException) {
            return false;
        }
    }
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
}
