namespace Puck.Shaders;

/// <summary>A compiler diagnostic mapped to the author's source file.</summary>
public readonly record struct ShaderDiagnostic(
    int Line,
    int Column,
    string Message,
    bool IsError,
    ShaderStage? Stage = null,
    string? Path = null)
{
    public ShaderDiagnostic(int line, string message, bool isError)
        : this(line, 0, message, isError) { }
}
