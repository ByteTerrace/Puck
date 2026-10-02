using System.Globalization;
using System.Text;
using Puck.Abstractions.Presentation;
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
    /// <summary>The repository-relative path of the build's shader recipe: the MSBuild properties
    /// <c>build/Shaders.targets</c> compiles every stage source with, one a stage and target
    /// (<see cref="BuildRecipePropertyOf"/>), generated from <see cref="StepsOf"/> by <c>puck shaders generate</c>
    /// (<see cref="GenerateBuildRecipe"/>), so the build and this compiler run one recipe.</summary>
    public const string BuildRecipePath = "build/ShaderRecipe.targets";

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
    /// <param name="tier">The quality tier the stage compiles for, which both steps define as
    /// <see cref="QualityTiers.Define"/>, or <see langword="null"/> for the variant no tier names, which defines
    /// nothing.</param>
    /// <returns>The SPIR-V step, then the DXIL step.</returns>
    public static IReadOnlyList<ShaderCompileStep> StepsOf(ShaderStage stage, string entryPoint, QualityTier? tier = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: entryPoint);

        var profile = ProfileOf(stage: stage);
        string[] defines = ((tier is { } named)
            ? ["-D", $"{QualityTiers.Define}={QualityTiers.DefineValue(tier: named).ToString(provider: CultureInfo.InvariantCulture)}"]
            : []);

        // Both targets pin the capability floor, explicit -O3 and 16-bit types. SPIR-V renames the entry point to main;
        // the vk:: attributes are SPIR-V's alone, so DXIL suppresses the warning that it ignores them.
        return [
            new ShaderCompileStep(
                Options: ["-spirv", $"-fspv-target-env=vulkan{VulkanVersion}", "-fspv-entrypoint-name=main", "-enable-16bit-types", "-O3", "-T", profile, "-E", entryPoint, .. defines],
                Tool: DxcTool
            ),
            new ShaderCompileStep(
                Options: ["-Wno-ignored-attributes", "-enable-16bit-types", "-O3", "-T", profile, "-E", entryPoint, .. defines],
                Tool: DxcTool
            ),
        ];
    }
    /// <summary>Returns the entry point a stage source the build compiles declares: <c>VSMain</c>, <c>PSMain</c> or
    /// <c>CSMain</c>.</summary>
    /// <param name="stage">The stage.</param>
    /// <returns>The entry point.</returns>
    public static string BuildEntryPointOf(ShaderStage stage) => stage switch {
        ShaderStage.Vertex => "VSMain",
        ShaderStage.Fragment => "PSMain",
        _ => "CSMain",
    };
    /// <summary>Returns the MSBuild property of the build's shader recipe (<see cref="BuildRecipePath"/>) that holds one
    /// stage's options for one target, such as <c>PuckDxcComputeSpirv</c>.</summary>
    /// <param name="stage">The stage.</param>
    /// <param name="spirv">Whether the target is SPIR-V rather than DXIL.</param>
    /// <returns>The property name.</returns>
    public static string BuildRecipePropertyOf(ShaderStage stage, bool spirv) =>
        $"PuckDxc{stage}{(spirv ? "Spirv" : "Dxil")}";
    /// <summary>Generates the build's shader recipe (<see cref="BuildRecipePath"/>): for every stage, the options of
    /// <see cref="StepsOf"/> for its <see cref="BuildEntryPointOf">build entry point</see> and no tier, SPIR-V then
    /// DXIL, each as the property <see cref="BuildRecipePropertyOf"/> names.</summary>
    /// <returns>The file's text, LF line endings.</returns>
    public static string GenerateBuildRecipe() {
        var text = new StringBuilder();

        text.Append(value: "<Project>\n");
        text.Append(value: "    <!-- Generated by `puck shaders generate` from Puck.Shaders.ShaderCompiler.StepsOf, the DXC recipe the runtime\n");
        text.Append(value: "         shader compiler runs too; never edit it. build/Shaders.targets compiles every stage source with these\n");
        text.Append(value: "         options, followed by -Fo <bytecode> <source>. -->\n");
        text.Append(value: "    <PropertyGroup>\n");

        foreach (var stage in Enum.GetValues<ShaderStage>()) {
            var steps = StepsOf(
                entryPoint: BuildEntryPointOf(stage: stage),
                stage: stage
            );

            foreach (var (step, spirv) in ((ReadOnlySpan<(ShaderCompileStep, bool)>)[(steps[0], true), (steps[1], false)])) {
                var property = BuildRecipePropertyOf(
                    spirv: spirv,
                    stage: stage
                );

                text.Append(provider: CultureInfo.InvariantCulture, handler: $"        <{property}>{string.Join(separator: ' ', values: step.Options)}</{property}>\n");
            }
        }

        text.Append(value: "    </PropertyGroup>\n");
        text.Append(value: "</Project>\n");

        return text.ToString();
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
            Stages: request.Stages.Select(selector: stage => new ShaderCompileStage(
                EntryPoint: stage.EntryPoint,
                Profile: ProfileOf(stage: stage.Stage),
                Stage: stage.Stage,
                Steps: StepsOf(
                    entryPoint: stage.EntryPoint,
                    stage: stage.Stage,
                    tier: request.Tier
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
    private async Task<ChildProcessResult> RunStepAsync(ShaderCompileStep step, string input, string output, IReadOnlyList<string> includeDirectories, CancellationToken cancellationToken) {
        var args = new List<string>(collection: step.Options);

        foreach (var includeDirectory in includeDirectories) { args.Add(item: ("-I" + includeDirectory)); }

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
