using System.CommandLine;
using Puck.Cli.Gate;

namespace Puck.Cli.Locks;

/// <summary><c>puck locks</c>: re-records the solution's lock files after a reference change, takes newer packages with
/// <c>--update</c>, and with <c>--check</c> reports every project whose lock file drifted, writing nothing.
/// <see cref="LockFiles"/> holds both halves.</summary>
public static class LocksCommand {
    private const string Verb = "locks";

    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(minutes: 15);

    private static GateStepResult Restore(string repositoryRoot, IReadOnlyList<string> arguments, CancellationToken cancellationToken) {
        var run = CliProcess.RunCaptured(
            arguments: arguments,
            cancellationToken: cancellationToken,
            fileName: "dotnet",
            input: string.Empty,
            timeout: RestoreTimeout,
            workingDirectory: repositoryRoot
        );

        return new GateStepResult(
            ExitCode: (run.TimedOut ? CliExit.Refused : run.ExitCode),
            Output: (string.Join(separator: '\n', values: run.OutputLines.Select(selector: static line => line.Line)) + (run.TimedOut ? $"\n(timed out after {RestoreTimeout})" : string.Empty))
        );
    }
    private static int Run(bool check, bool update, CancellationToken cancellationToken) {
        if (check && update) {
            return CliExit.Refuse(verb: Verb, what: "--update", why: "re-evaluates every floating version, which a check never does.");
        }
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }
        if (check) {
            var verdict = LockFiles.Check(repositoryRoot: repositoryRoot, restore: arguments => Restore(arguments: arguments, cancellationToken: cancellationToken, repositoryRoot: repositoryRoot));

            foreach (var line in verdict.Report) { Console.Out.WriteLine(value: $"{Verb}: {line}"); }
            if (verdict.ExitCode == CliExit.Success) { Console.Out.WriteLine(value: $"{Verb}: every lock file matches its project."); }
            return verdict.ExitCode;
        }
        var result = LockFiles.Record(changed: out var changed, repositoryRoot: repositoryRoot, restore: arguments => Restore(arguments: arguments, cancellationToken: cancellationToken, repositoryRoot: repositoryRoot), update: update);

        if (result.ExitCode != 0) {
            Console.Error.WriteLine(value: result.Output.TrimEnd());
            Console.Error.WriteLine(value: $"{Verb}: the restore failed (exit {result.ExitCode}); no lock file is known to be current.");
            return CliExit.Failed;
        }
        foreach (var path in changed) { Console.Out.WriteLine(value: $"{Verb}: recorded {path}"); }
        if (changed.Count == 0) { Console.Out.WriteLine(value: $"{Verb}: every lock file already matched its project."); }
        return CliExit.Success;
    }

    /// <summary>Creates <c>puck locks</c>.</summary>
    /// <returns>The command.</returns>
    public static Command Create() {
        var check = CliOptions.Check(description: $"Run a locked restore of {LockFiles.Solution} and report each project whose lock file no longer matches it, by name; write nothing.");
        var update = new Option<bool>(name: "--update") { Description = "Re-evaluate every floating package version and record the newest each range admits." };
        var command = new Command(
            description: "Re-record the solution's packages.lock.json files after a reference change, or check them.",
            name: Verb
        ) { check, update };

        command.Detail(detail: $"""
            Every restore in the tree is locked: Directory.Build.props sets RestoreLockedMode, as CI's
            `dotnet restore {LockFiles.Solution} --locked-mode` does. A build after a package or project reference
            change therefore fails with NU1004, naming the project, until its lock file is re-recorded here.

            puck locks            restore unlocked; NuGet rewrites only the lock files that no longer match
                                  their projects, and keeps every other pinned version. Names each lock file
                                  it wrote.
            puck locks --update   also re-evaluate every floating version ([10.*, ) and the like), taking the
                                  newest packages each range admits: the explicit dependency update.
            puck locks --check    run `dotnet {string.Join(separator: ' ', values: LockFiles.CheckArguments)}` and report each
                                  project whose lock file drifted, and each project that has none. Writes no
                                  lock file: a lock file the restore creates for a project without one is
                                  removed again. `puck gate` runs this before it builds anything.

            Review and commit the rewritten lock files with the reference change that moved them.

            Exit codes: 0 every lock file matches (or was recorded); 1 drift found or the restore failed;
            2 refused (--check with --update, or no repository root).
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.FromResult(result: Run(
            cancellationToken: cancellationToken,
            check: parseResult.GetValue(option: check),
            update: parseResult.GetValue(option: update)
        )));
        return command;
    }
}
