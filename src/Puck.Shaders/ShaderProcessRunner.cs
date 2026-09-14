using System.Diagnostics;
using System.Text;

namespace Puck.Shaders;

internal sealed class ShaderProcessRunner : IShaderProcessRunner {
    public async Task<ShaderProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
        var startInfo = new ProcessStartInfo {
            CreateNoWindow = true,
            FileName = fileName,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            StandardErrorEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            UseShellExecute = false,
        };

        foreach (var argument in arguments) { startInfo.ArgumentList.Add(item: argument); }

        using var process = (Process.Start(startInfo: startInfo) ?? throw new InvalidOperationException(message: $"Failed to start {fileName}."));

        try {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken: cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken: cancellationToken);

            await process.WaitForExitAsync(cancellationToken: cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return new ShaderProcessResult(
                process.ExitCode,
                await stdoutTask.ConfigureAwait(continueOnCapturedContext: false),
                await stderrTask.ConfigureAwait(continueOnCapturedContext: false)
            );
        } catch (OperationCanceledException) {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); } } catch (InvalidOperationException) { }
            throw;
        }
    }
}
