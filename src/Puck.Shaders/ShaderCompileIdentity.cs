using System.Text.Json.Serialization;

namespace Puck.Shaders;

/// <summary>One native tool run of a compile: the tool and the options that select what it produces. The compiler adds
/// only paths to these options when it runs the tool: the include directory, the output, and the input.</summary>
/// <param name="Tool">The tool's name, <c>dxc</c>.</param>
/// <param name="Options">The options, in the order the tool receives them.</param>
public sealed record ShaderCompileStep(string Tool, IReadOnlyList<string> Options);
/// <summary>How one HLSL stage compiles: its entry point, profile, and the native tool runs that turn it into SPIR-V and
/// DXIL, in order.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="EntryPoint">The entry point the author declared.</param>
/// <param name="Profile">The DXC target profile, such as <c>cs_6_6</c>.</param>
/// <param name="Steps">The tool runs: SPIR-V, then DXIL.</param>
public sealed record ShaderCompileStage(
    ShaderStage Stage,
    string EntryPoint,
    string Profile,
    IReadOnlyList<ShaderCompileStep> Steps
);
/// <summary>
/// What one compile is, apart from the machine that runs it: the compiler revision, how each stage compiles, and every
/// file it reads with its content hash. <see cref="ShaderCompiler"/> hashes this record, together with the request and
/// the local toolchain's identity, into the compile's cache key, and every <see cref="CompiledShader"/> carries the
/// record it was compiled under. A package manifest records the same stages and revision with logical paths, so the
/// cache key and the manifest have one source.
/// </summary>
/// <param name="Compiler">The compiler's revision, which changes whenever the way it compiles does.</param>
/// <param name="Stages">How each stage compiles, in the request's order.</param>
/// <param name="Sources">Each stage's source path and content hash, in the order of <paramref name="Stages"/>.</param>
/// <param name="Includes">Every include the stage sources reach, by full path, in discovery order.</param>
public sealed record ShaderCompileIdentity(
    string Compiler,
    IReadOnlyList<ShaderCompileStage> Stages,
    IReadOnlyList<ShaderSourceDependency> Sources,
    IReadOnlyList<ShaderSourceDependency> Includes
) {
    /// <summary>Gets the number of native tool runs the compile needs.</summary>
    [JsonIgnore]
    public int StepCount => Stages.Sum(selector: static stage => stage.Steps.Count);
}
