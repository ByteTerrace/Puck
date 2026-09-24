using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;

namespace Puck.Cli.Canary;

internal static partial class CanaryCommand {
    // Prepares a leg's shader package before its first boot (CanaryPackage): the run's one package of the leg's source
    // (CanaryPackages) is copied to <run>/<output>, and an altered leg then appends a line to its own copy, so the World
    // reopens a relocated package whose source tree is gone. Returns null once the package is in place, or the leg's
    // result when it could not be prepared: unsupported when the verb reports a missing shader tool, and an
    // infrastructure failure otherwise. Every leg keeps the verb's output as <run>/package.log.
    private static CanaryLegRun? PreparePackage(CanaryLeg leg, CanaryPackage package, string runDirectory, CanaryBudget budget, TimeSpan timeout) {
        if (budget.Remaining < timeout) {
            return CanaryLegRun.BudgetExpired(
                budget: budget,
                leg: leg,
                runDirectory: runDirectory
            );
        }

        var built = budget.Packages.GetOrBuild(
            build: () => BuildPackage(
                budget: budget,
                sourcePath: package.SourcePath,
                timeout: timeout
            ),
            sourcePath: package.SourcePath
        );

        File.WriteAllText(
            contents: built.Log,
            encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            path: Path.Combine(
                path1: runDirectory,
                path2: "package.log"
            )
        );

        if (built.Directory is null) {
            var reason = $"shaders package {package.OutputName}: {built.Failure}";

            return (built.Unsupported
                ? (CanaryLegRun.InfrastructureFailure(
                    leg: leg,
                    reason: reason,
                    runDirectory: runDirectory
                ) with { InfrastructureError = null, Unsupported = reason })
                : CanaryLegRun.InfrastructureFailure(
                    leg: leg,
                    reason: reason,
                    runDirectory: runDirectory
                ));
        }

        var output = Path.Combine(
            path1: runDirectory,
            path2: package.OutputName
        );

        CopyDirectory(
            source: built.Directory,
            target: output
        );

        if (package.Alter is { } alter) {
            File.AppendAllText(
                contents: "\n// altered by the canary runner\n",
                path: Path.Combine(
                    path1: output,
                    path2: alter
                )
            );
        }

        return null;
    }
    // Packages one source for the whole run: the files of the directory holding it are copied into a run-level scratch
    // directory, that copy is packaged by this CLI's own `shaders package`, and the copy is deleted, so the package
    // every leg copies from outlives its source tree.
    private static CanaryBuiltPackage BuildPackage(string sourcePath, CanaryBudget budget, TimeSpan timeout) {
        var scratch = Directory.CreateTempSubdirectory(prefix: $"{ScratchPrefix}package-").FullName;
        var copy = Path.Combine(
            path1: scratch,
            path2: "package-source"
        );
        var built = Path.Combine(
            path1: scratch,
            path2: "package-build"
        );

        Directory.CreateDirectory(path: copy);
        foreach (var file in Directory.GetFiles(path: Path.GetDirectoryName(path: sourcePath)!)) {
            File.Copy(
                destFileName: Path.Combine(
                    path1: copy,
                    path2: Path.GetFileName(path: file)
                ),
                sourceFileName: file
            );
        }

        CliProcessResult process;

        try {
            budget.Tally.PackageSpawned();
            process = CliProcess.RunCaptured(
                arguments: [
                    typeof(CanaryCommand).Assembly.Location,
                    "shaders", "package",
                    Path.Combine(
                        path1: copy,
                        path2: Path.GetFileName(path: sourcePath)
                    ),
                    "--output", built,
                    "--json",
                ],
                cancellationToken: budget.Cancellation,
                fileName: "dotnet",
                input: string.Empty,
                timeout: timeout
            );
        } catch (Exception exception) when ((exception is InvalidOperationException or System.ComponentModel.Win32Exception)) {
            var reason = $"could not start: {exception.Message.ReplaceLineEndings(replacementText: " ")}";

            return new CanaryBuiltPackage(
                Directory: null,
                Failure: reason,
                Log: reason,
                Unsupported: false
            );
        }

        var log = (process.Stdout + process.Stderr);

        if (
            process.TimedOut ||
            (process.ExitCode != CliExit.Success)
        ) {
            var (status, message) = PackageOutcome(stdout: process.Stdout);

            return new CanaryBuiltPackage(
                Directory: null,
                Failure: $"exited {process.ExitCode}{(process.TimedOut ? " after timing out" : string.Empty)}: {(message ?? process.Stderr).ReplaceLineEndings(replacementText: " ").Trim()}",
                Log: log,
                Unsupported: (status == "Unsupported")
            );
        }

        Directory.Delete(
            path: copy,
            recursive: true
        );

        return new CanaryBuiltPackage(
            Directory: built,
            Failure: null,
            Log: log,
            Unsupported: false
        );
    }
    // The status and message of the one JSON object `shaders package --json` writes, or nulls when it wrote none.
    private static (string? Status, string? Message) PackageOutcome(string stdout) {
        try {
            using var document = JsonDocument.Parse(json: stdout);

            return (
                (document.RootElement.TryGetProperty(propertyName: "status", value: out var status) ? status.GetString() : null),
                (document.RootElement.TryGetProperty(propertyName: "message", value: out var message) ? message.GetString() : null)
            );
        } catch (JsonException) {
            return (null, null);
        }
    }

    // One package outcome: the built package's directory and the verb's output, or why it could not be built.
    private sealed record CanaryBuiltPackage(string? Directory, string Log, string? Failure, bool Unsupported);
    // The run's shader packages, one per source however many legs, backends, and manifests name it: the first leg to
    // ask builds it, and every other leg waits for that build and copies it. A package is the same bytes whichever leg
    // built it, so sharing it changes no observation; a leg that alters its package alters its own copy.
    private sealed class CanaryPackages {
        private readonly ConcurrentDictionary<string, Lazy<CanaryBuiltPackage>> m_packages = new(comparer: Puck.Abstractions.PuckPaths.Comparer);

        public CanaryBuiltPackage GetOrBuild(string sourcePath, Func<CanaryBuiltPackage> build) =>
            m_packages.GetOrAdd(
                key: Path.GetFullPath(path: sourcePath),
                value: new Lazy<CanaryBuiltPackage>(
                    mode: LazyThreadSafetyMode.ExecutionAndPublication,
                    valueFactory: build
                )
            ).Value;
    }
}
