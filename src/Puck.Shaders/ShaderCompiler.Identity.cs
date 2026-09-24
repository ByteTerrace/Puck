using System.Globalization;
using System.Text;
using Puck.Assets;
using Puck.Hosting;

namespace Puck.Shaders;

public sealed partial class ShaderCompiler {
    /// <summary>The Direct3D shader model every stage's DXC profile targets.</summary>
    public const string ShaderModel = "6.6";
    /// <summary>The Vulkan API version every SPIR-V module targets.</summary>
    public const string VulkanVersion = "1.3";
    /// <summary>The DirectX Shader Compiler, which produces SPIR-V and DXIL for every stage.</summary>
    public const string DxcTool = "dxc";

    /// <summary>Returns the DXC target profile a stage compiles under, such as <c>cs_6_6</c>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The profile.</returns>
    public static string ProfileOf(ShaderStage stage) {
        var prefix = stage switch {
            ShaderStage.Vertex => "vs",
            ShaderStage.Fragment => "ps",
            _ => "cs",
        };

        return $"{prefix}_{ShaderModel.Replace(
            newChar: '_',
            oldChar: '.'
        )}";
    }
    /// <summary>Returns the native tool runs that compile one stage to SPIR-V and DXIL, in order. They are the one
    /// statement of the compiler's options: the compiler runs exactly these, its cache key hashes them, and a package
    /// manifest records them.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="entryPoint">The entry point the author declared.</param>
    /// <returns>The SPIR-V step, then the DXIL step.</returns>
    public static IReadOnlyList<ShaderCompileStep> StepsOf(ShaderStage stage, string entryPoint) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: entryPoint);

        var profile = ProfileOf(stage: stage);

        return [
            new ShaderCompileStep(
                Options: ["-spirv", $"-fspv-target-env=vulkan{VulkanVersion}", "-fspv-entrypoint-name=main", "-T", profile, "-E", entryPoint],
                Tool: DxcTool
            ),
            new ShaderCompileStep(
                Options: ["-T", profile, "-E", entryPoint],
                Tool: DxcTool
            ),
        ];
    }
    /// <summary>Returns the identity a request compiles under once its closure is collected.</summary>
    /// <param name="request">The request.</param>
    /// <param name="closure">The closure of the request's stage sources.</param>
    /// <returns>The identity.</returns>
    public static ShaderCompileIdentity Identify(ShaderCompilationRequest request, ShaderSourceClosure closure) {
        ArgumentNullException.ThrowIfNull(argument: request);
        ArgumentNullException.ThrowIfNull(argument: closure);

        return new ShaderCompileIdentity(
            Compiler: CompilerVersion,
            Includes: closure.Includes,
            Sources: closure.Sources,
            Stages: request.Stages.Select(selector: static stage => new ShaderCompileStage(
                EntryPoint: stage.EntryPoint,
                Profile: ProfileOf(stage: stage.Stage),
                Stage: stage.Stage,
                Steps: StepsOf(
                    entryPoint: stage.EntryPoint,
                    stage: stage.Stage
                )
            )).ToArray()
        );
    }
    /// <summary>Reads the version a native tool reports: the first line <c>--version</c> prints.</summary>
    /// <param name="tool">The tool's name, <see cref="DxcTool"/>.</param>
    /// <param name="cancellationToken">The token that cancels the tool run.</param>
    /// <returns>The version line.</returns>
    /// <exception cref="ShaderToolMissingException">The tool cannot be resolved.</exception>
    /// <exception cref="ShaderClosureRefusedException">The tool ran but reported no version.</exception>
    public async Task<string> ToolVersionAsync(string tool, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: tool);

        var result = await RunToolAsync(
            args: ["--version"],
            cancellationToken: cancellationToken,
            name: tool
        ).ConfigureAwait(continueOnCapturedContext: false);
        var line = ((result.Stdout + "\n") + result.Stderr).Split('\n').Select(selector: static text => text.Trim()).FirstOrDefault(predicate: static text => (text.Length != 0));

        if (
            (result.ExitCode != 0) ||
            (line is null)
        ) {
            throw new ShaderClosureRefusedException(
                code: ShaderClosureRefusedException.PackageCompiler,
                message: $"'{tool}' reported no version (exit code {result.ExitCode.ToString(provider: CultureInfo.InvariantCulture)})."
            );
        }

        return line;
    }

    private string CreateCacheKey(ShaderCompilationRequest descriptor, ShaderCompileIdentity identity) {
        var builder = new StringBuilder(value: identity.Compiler).Append(value: '|').Append(value: SerializeDescriptor(descriptor: descriptor)).Append(value: '|').Append(value: m_toolchain.Identity);

        foreach (var stage in identity.Stages) {
            _ = builder.Append(value: '|').Append(value: stage.Stage).Append(value: '|').Append(value: stage.Profile).Append(value: '|').Append(value: stage.EntryPoint);

            foreach (var step in stage.Steps) {
                _ = builder.Append(value: '|').Append(value: step.Tool);

                foreach (var option in step.Options) {
                    _ = builder.Append(value: '|').Append(value: option);
                }
            }
        }

        foreach (var include in identity.Includes.OrderBy(
            keySelector: static include => include.Path,
            comparer: StringComparer.OrdinalIgnoreCase
        )) {
            _ = builder.Append(value: '|').Append(value: include.Path).Append(value: '|').Append(value: include.ContentHash);
        }

        return ContentPin.Compute(content: Encoding.UTF8.GetBytes(s: builder.ToString())).Hex;
    }
    // Runs one step: its options, then the include directory, the output and the input. Every compile step's tool runs
    // here, so a run is counted here.
    private async Task<ChildProcessResult> RunStepAsync(ShaderCompileStep step, string input, string output, string? includeDirectory, CancellationToken cancellationToken) {
        var args = new List<string>(collection: step.Options);

        if (!string.IsNullOrWhiteSpace(value: includeDirectory)) { args.Add(item: ("-I" + includeDirectory)); }

        args.AddRange(collection: ["-Fo", output, input]);

        var result = await RunToolAsync(
            args: args,
            cancellationToken: cancellationToken,
            name: step.Tool
        ).ConfigureAwait(continueOnCapturedContext: false);

        Work.Count(kind: RunsOf(tool: step.Tool));

        return result;
    }
}
