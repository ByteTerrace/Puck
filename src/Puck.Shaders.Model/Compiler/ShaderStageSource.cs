namespace Puck.Shaders;

/// <summary>One HLSL source stage and the entry point the compiler builds it from.</summary>
/// <param name="Stage">The stage.</param>
/// <param name="Path">The source's full path, which resolves its includes and names it in diagnostics.</param>
/// <param name="Source">The source text.</param>
/// <param name="EntryPoint">The entry point.</param>
/// <param name="IncludeDirectories">The directories, in search order, an include that does not lie beside the file naming
/// it resolves through, as full paths: <see cref="ShaderIncludeDirectories.Of"/> for an engine source, or
/// <see langword="null"/> for none.</param>
public sealed record ShaderStageSource(
    ShaderStage Stage,
    string Path,
    string Source,
    string EntryPoint = "main",
    IReadOnlyList<string>? IncludeDirectories = null
);
