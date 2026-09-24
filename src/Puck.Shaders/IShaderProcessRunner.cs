using Puck.Hosting;

namespace Puck.Shaders;

// The compiler's seam over ChildProcess.RunAsync, so tests can stand in for the shader tools.
internal interface IShaderProcessRunner {
    Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
