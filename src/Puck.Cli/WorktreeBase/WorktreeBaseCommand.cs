using System.CommandLine;

namespace Puck.Cli.WorktreeBase;

/// <summary>
/// <c>puck worktree-base</c> — the mandatory first step in any git worktree: puts HEAD at a named base commit,
/// refusing rather than resetting when that would discard uncommitted work.
/// </summary>
internal static class WorktreeBaseCommand {
    public static Command Create() {
        var baseArgument = new Argument<string>(name: "base") { Description = "The commit, tag, or ref to put HEAD at, resolved as <base>^{commit} in the target worktree." };
        var pathOption = new Option<string>(name: "--path") { DefaultValueFactory = _ => Directory.GetCurrentDirectory(), Description = "The worktree to act on." };
        var command = new Command(description: """
            Put a worktree's HEAD at a base commit, refusing to reset a dirty tree.

              HEAD already at the base       print "at base", exit 0
              clean tree, wrong base         git reset --hard <base>, print old -> new, exit 0
              dirty tree, wrong base         print what is dirty, refuse, exit 1, reset nothing
              git failure / not a git tree /
              unresolvable ref               exit 2

            "Dirty" means a tracked modification (git status --porcelain --untracked-files=no is nonempty);
            untracked files never block a reset. Always prints the worktree's toplevel path it acted on.
            """, name: "worktree-base") { baseArgument, pathOption };

        command.SetAction(action: parseResult => Run(baseRef: parseResult.GetRequiredValue(argument: baseArgument), rawPath: parseResult.GetRequiredValue(option: pathOption)));
        return command;
    }

    private static int Run(string baseRef, string rawPath) {
        var path = (Path.IsPathRooted(path: rawPath) ? rawPath : Path.GetFullPath(path: rawPath));
        var toplevel = WorktreeBaseGit.Run(path: path, arguments: ["rev-parse", "--show-toplevel"]);

        if (toplevel.ExitCode != 0) {
            Console.Error.WriteLine(value: $"worktree-base: '{path}' is not a git worktree: {toplevel.Stderr}");

            return 2;
        }

        var worktree = toplevel.Stdout;
        var head = WorktreeBaseGit.Run(path: worktree, arguments: ["rev-parse", "HEAD"]);

        if ((head.ExitCode != 0) || (head.Stdout.Length == 0)) {
            Console.Error.WriteLine(value: $"worktree-base: {worktree}: could not resolve HEAD: {head.Stderr}");

            return 2;
        }

        var resolvedBase = WorktreeBaseGit.Run(path: worktree, arguments: ["rev-parse", "--verify", "--quiet", $"{baseRef}^{{commit}}"]);

        if ((resolvedBase.ExitCode != 0) || (resolvedBase.Stdout.Length == 0)) {
            Console.Error.WriteLine(value: $"worktree-base: {worktree}: '{baseRef}' does not resolve to a commit.");

            return 2;
        }

        var oldHead = head.Stdout;
        var newBase = resolvedBase.Stdout;

        if (string.Equals(a: oldHead, b: newBase, comparisonType: StringComparison.Ordinal)) {
            Console.Out.WriteLine(value: $"worktree-base: {worktree} at base {newBase[..12]}.");

            return 0;
        }

        var status = WorktreeBaseGit.Run(path: worktree, arguments: ["status", "--porcelain", "--untracked-files=no"]);

        if (status.ExitCode != 0) {
            Console.Error.WriteLine(value: $"worktree-base: {worktree}: could not read tree status: {status.Stderr}");

            return 2;
        }

        if (status.Stdout.Length != 0) {
            Console.Error.WriteLine(value: $"worktree-base: {worktree} is at {oldHead[..12]}, not base {newBase[..12]}, and the tracked tree is dirty:");

            foreach (var line in status.Stdout.Split(separator: '\n')) {
                Console.Error.WriteLine(value: $"  {line.TrimEnd(trimChar: '\r')}");
            }

            Console.Error.WriteLine(value: "worktree-base: refusing to reset a dirty tree.");

            return 1;
        }

        var reset = WorktreeBaseGit.Run(path: worktree, arguments: ["reset", "--hard", newBase]);

        if (reset.ExitCode != 0) {
            Console.Error.WriteLine(value: $"worktree-base: {worktree}: git reset --hard {newBase} failed: {reset.Stderr}");

            return 2;
        }

        Console.Out.WriteLine(value: $"worktree-base: {worktree} was at {oldHead[..12]}, reset to {newBase[..12]}.");

        return 0;
    }
}
