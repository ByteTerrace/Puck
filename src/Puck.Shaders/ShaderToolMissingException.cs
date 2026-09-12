namespace Puck.Shaders;

/// <summary>Thrown when a required shader compiler executable cannot be resolved.</summary>
public sealed class ShaderToolMissingException : Exception
{
    public ShaderToolMissingException(string tool, string? directory)
        : base($"Shader toolchain tool '{tool}' was not found in {(directory is null ? "the search path" : $"'{directory}'")}.")
    {
        Tool = tool;
        Directory = directory;
    }

    public string Tool { get; }
    public string? Directory { get; }
}
