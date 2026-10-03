using Puck.Cli.Host;

namespace Puck.Cli.Gate;

/// <summary>Runs the gate's steps as child processes: <c>dotnet build</c> over the solution, and each verb as
/// <c>dotnet &lt;copy&gt;/Puck.Cli.dll</c> in the checkout.</summary>
internal sealed class ProcessGateRunner(TimeProvider clock, CancellationToken cancellationToken) : IGateRunner {
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(minutes: 60);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromHours(hours: 3);

    private static GateStepResult Captured(CliProcessResult run, TimeSpan timeout) => new(
        ExitCode: (run.TimedOut ? CliExit.Refused : run.ExitCode),
        Output: (string.Join(separator: '\n', values: run.OutputLines.Select(selector: static line => line.Line)) + (run.TimedOut ? $"\n(timed out after {timeout})" : string.Empty))
    );

    /// <inheritdoc/>
    public GateStepResult Dotnet(string repositoryRoot, IReadOnlyList<string> arguments) => Captured(
        run: CliProcess.RunCaptured(
            arguments: arguments,
            fileName: "dotnet",
            input: string.Empty,
            clock: clock,
            cancellationToken: cancellationToken,
            timeout: ((arguments[0] == "build") ? BuildTimeout : StepTimeout),
            workingDirectory: repositoryRoot
        ),
        timeout: ((arguments[0] == "build") ? BuildTimeout : StepTimeout)
    );
    /// <inheritdoc/>
    public bool WaitForCapacity(string repositoryRoot, string step, bool device, bool heavySuite) {
        var probe = new HostProbe(checkoutRoot: repositoryRoot);

        return HostAdmission.Wait(step, device, heavySuite, () => probe.Sample(firstInterval: TimeSpan.FromSeconds(seconds: 1)), clock,
            delay => Task.Delay(cancellationToken: cancellationToken, delay: delay, timeProvider: clock).GetAwaiter().GetResult(), Console.Error, cancellationToken);
    }
    public string CopyCli(string repositoryRoot, string directory) {
        var output = Path.Combine(
            path1: repositoryRoot,
            path2: "src/Puck.Cli/bin",
            path3: CliOptions.DefaultConfiguration,
            path4: "net10.0"
        );

        foreach (var file in Directory.EnumerateFiles(path: output, searchOption: SearchOption.AllDirectories, searchPattern: "*")) {
            var destination = Path.Combine(
                path1: directory,
                path2: Path.GetRelativePath(path: file, relativeTo: output)
            );

            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(
                destFileName: destination,
                sourceFileName: file
            );
        }

        return Path.Combine(
            path1: directory,
            path2: "Puck.Cli.dll"
        );
    }
    /// <inheritdoc/>
    public GateStepResult Puck(string cli, string repositoryRoot, IReadOnlyList<string> arguments, Action<string>? progress = null) => Captured(
        run: CliProcess.RunCaptured(
            arguments: [cli, .. arguments],
            fileName: "dotnet",
            input: string.Empty,
            clock: clock,
            cancellationToken: cancellationToken,
            timeout: StepTimeout,
            workingDirectory: repositoryRoot,
            onOutput: ((progress is null) ? null : line => progress(obj: line.Line))
        ),
        timeout: StepTimeout
    );
}
