using Puck.Hosting;

namespace Puck.Shaders;

// Each tool's standard input is closed rather than inherited: a build host speaks to its compiler over that stream, and
// no compile step reads it.
internal sealed class ShaderProcessRunner : IShaderProcessRunner {
    public Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        ChildProcess.RunAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            fileName: fileName,
            input: string.Empty
        );
}
