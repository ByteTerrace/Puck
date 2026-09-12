namespace Puck.Shaders;

internal interface IShaderProcessRunner
{
    Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}

internal readonly record struct ShaderProcessResult(int ExitCode, string Stdout, string Stderr);
