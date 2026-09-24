using System.CommandLine;

namespace Puck.Cli.WorktreeBase;

/// <summary>
/// <c>puck worktree-base</c> — the mandatory first step in any git worktree: puts HEAD at a named base commit,
/// refusing rather than resetting when that would discard uncommitted work.
/// </summary>
internal static class WorktreeBaseCommand {
    private const string Verb = "worktree-base";

    // One git call against the worktree, both streams trimmed.
    private static (int ExitCode, string Stdout, string Stderr) Git(string worktree, params string[] arguments) {
        var result = CliGit.Run(
            arguments: arguments,
            repository: worktree
        );

        return (result.ExitCode, result.Stdout.Trim(), result.Stderr.Trim());
    }
    private static int Run(string baseRef, string rawPath) {
        var path = Path.GetFullPath(path: rawPath);
        var toplevel = Git(path, "rev-parse", "--show-toplevel");

        if (toplevel.ExitCode != 0) {
            return CliExit.Refuse(
                verb: Verb,
                what: CliPaths.ToDisplay(fullPath: path),
                why: $"not a git worktree: {toplevel.Stderr}"
            );
        }

        var worktree = Path.GetFullPath(path: toplevel.Stdout);
        var shown = CliPaths.ToDisplay(fullPath: worktree);
        var head = Git(worktree, "rev-parse", "HEAD");

        if (
            (head.ExitCode != 0) ||
            (head.Stdout.Length == 0)
        ) {
            return CliExit.Refuse(
                verb: Verb,
                what: shown,
                why: $"could not resolve HEAD: {head.Stderr}"
            );
        }

        if (!CliGit.TryResolveCommit(
            repository: worktree,
            resolved: out var newBase,
            revision: baseRef
        )) {
            return CliExit.Refuse(
                verb: Verb,
                what: baseRef,
                why: $"does not resolve to a commit in {shown}."
            );
        }

        var oldHead = head.Stdout;

        if (string.Equals(
            a: oldHead,
            b: newBase,
            comparisonType: StringComparison.Ordinal
        )) {
            Console.Out.WriteLine(value: $"worktree-base: {shown} at base {newBase[..12]}.");

            return CliExit.Success;
        }

        var status = Git(worktree, "status", "--porcelain", "--untracked-files=no");

        if (status.ExitCode != 0) {
            return CliExit.Refuse(
                verb: Verb,
                what: shown,
                why: $"could not read tree status: {status.Stderr}"
            );
        }

        if (status.Stdout.Length != 0) {
            _ = CliExit.Refuse(
                verb: Verb,
                what: shown,
                why: $"refusing to reset a dirty tree: HEAD is at {oldHead[..12]}, not base {newBase[..12]}, and these tracked files are modified:"
            );

            foreach (var line in status.Stdout.Split(separator: '\n')) {
                Console.Error.WriteLine(value: $"  {line.TrimEnd(trimChar: '\r')}");
            }

            return CliExit.Refused;
        }

        var reset = Git(worktree, "reset", "--hard", newBase);

        if (reset.ExitCode != 0) {
            return CliExit.Refuse(
                verb: Verb,
                what: shown,
                why: $"git reset --hard {newBase} failed: {reset.Stderr}"
            );
        }

        Console.Out.WriteLine(value: $"worktree-base: {shown} was at {oldHead[..12]}, reset to {newBase[..12]}.");

        return CliExit.Success;
    }

    public static Command Create() {
        var baseArgument = new Argument<string>(name: "base") { Description = "The commit, tag, or ref to put HEAD at, resolved as <base>^{commit} in the target worktree." };
        var pathOption = new Option<string>(name: "--path") { Description = "The worktree to act on, resolved against the working directory (default: the working directory)." };
        var command = new Command(
            description: "Put a worktree's HEAD at a base commit, refusing to reset a dirty tree.",
            name: Verb
        ) { baseArgument, pathOption };

        command.Detail(detail: """
              HEAD already at the base       print "at base", exit 0
              clean tree, wrong base         git reset --hard <base>, print old -> new, exit 0
              dirty tree, wrong base         print what is dirty, refuse, exit 2, reset nothing
              git failure / not a git tree /
              unresolvable ref               exit 2

            "Dirty" means a tracked modification (git status --porcelain --untracked-files=no is
            nonempty); untracked files never block a reset. Always prints the worktree's toplevel
            path it acted on, relative to the working directory.
            """);
        command.SetAction(action: parseResult => Run(
            baseRef: parseResult.GetRequiredValue(argument: baseArgument),
            rawPath: (parseResult.GetValue(option: pathOption) ?? ".")
        ));

        return command;
    }
}
