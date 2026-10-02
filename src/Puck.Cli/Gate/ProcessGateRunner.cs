namespace Puck.Cli.Gate;

/// <summary>Runs the gate's steps as child processes: <c>dotnet build</c> over the solution, and each verb as
/// <c>dotnet &lt;copy&gt;/Puck.Cli.dll</c> in the checkout.</summary>
internal sealed class ProcessGateRunner : IGateRunner {
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(minutes: 60);
    private static readonly TimeSpan StepTimeout = TimeSpan.FromHours(hours: 3);

    private static GateStepResult Captured(CliProcessResult run, TimeSpan timeout) => new(
        ExitCode: (run.TimedOut ? CliExit.Refused : run.ExitCode),
        Output: (string.Join(separator: '\n', values: run.OutputLines.Select(selector: static line => line.Line)) + (run.TimedOut ? $"\n(timed out after {timeout})" : string.Empty))
    );

    /// <inheritdoc/>
    public GateStepResult Build(string repositoryRoot) => Captured(
        run: CliProcess.RunCaptured(
            arguments: ["build", "Puck.slnx", "-c", CliOptions.DefaultConfiguration, "-v", "q", "-nologo"],
            fileName: "dotnet",
            input: string.Empty,
            timeout: BuildTimeout,
            workingDirectory: repositoryRoot
        ),
        timeout: BuildTimeout
    );
    /// <inheritdoc/>
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
    public GateStepResult Puck(string cli, string repositoryRoot, IReadOnlyList<string> arguments) => Captured(
        run: CliProcess.RunCaptured(
            arguments: [cli, .. arguments],
            fileName: "dotnet",
            input: string.Empty,
            timeout: StepTimeout,
            workingDirectory: repositoryRoot
        ),
        timeout: StepTimeout
    );
}
