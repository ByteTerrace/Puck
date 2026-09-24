using Puck.Hosting;

namespace Puck.Shaders;

internal sealed class ShaderProcessRunner : IShaderProcessRunner {
    public Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) =>
        ChildProcess.RunAsync(
            arguments: arguments,
            cancellationToken: cancellationToken,
            fileName: fileName
        );
}
