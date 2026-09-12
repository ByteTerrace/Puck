namespace Puck.Shaders;

/// <summary>One source stage and its compiler entry-point settings.</summary>
public sealed record ShaderStageSource(
    ShaderStage Stage,
    string Path,
    string Source,
    ShaderSourceLanguage Language = ShaderSourceLanguage.Hlsl,
    string EntryPoint = "main",
    uint GroupSizeX = 8,
    uint GroupSizeY = 8,
    uint GroupSizeZ = 1)
{
    public ShaderStageSource(ShaderStage stage, string path, string source, string entryPoint)
        : this(stage, path, source, ShaderSourceLanguage.Hlsl, entryPoint) { }
}
