using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Runs shader tools for the compiler through <see cref="ChildProcess.RunAsync"/> or a supplied runner.</summary>
public interface IShaderProcessRunner {
    /// <summary>Runs one tool invocation with the compiler's cancellation token.</summary>
    Task<ChildProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken);
}
