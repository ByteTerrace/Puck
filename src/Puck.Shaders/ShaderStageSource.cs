namespace Puck.Shaders;

/// <summary>One HLSL source stage and the entry point the compiler builds it from.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Path">The source's full path, which resolves its includes and names it in diagnostics.</param>
/// <param name="Source">The source text.</param>
/// <param name="EntryPoint">The entry point.</param>
public sealed record ShaderStageSource(
    ShaderStage Stage,
    string Path,
    string Source,
    string EntryPoint = "main"
);
