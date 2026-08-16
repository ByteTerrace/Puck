namespace Puck.Cli.WorktreeBase;

/// <summary>
/// <c>puck worktree-base</c> — the mandatory first step in any git worktree: puts HEAD at a named base commit,
/// refusing rather than resetting when that would discard uncommitted work.
/// </summary>
internal static class WorktreeBaseCommand {
    private const string HelpText =
        """
        puck worktree-base <sha-or-ref> [options]   put a worktree's HEAD at a base commit

        Options:
          --path <worktree>   the worktree to act on (default: the current directory)
          -h, --help          this text

        Resolves HEAD and <sha-or-ref>^{commit} in the target worktree.

          HEAD already at the base       print "at base", exit 0
          clean tree, wrong base         git reset --hard <base>, print old -> new, exit 0
          dirty tree, wrong base         print what is dirty, REFUSE, exit 1, reset nothing
          git failure / not a git tree /
          unresolvable ref               exit 2

        "Dirty" means a tracked modification (git status --porcelain --untracked-files=no is
        nonempty); untracked files never block a reset. Always prints the worktree's toplevel
        path it acted on.

        Exit codes: 0 already at base or reset, 1 refused (dirty), 2 usage or git failure.
        """;

    public static int Run(string[] args) {
        var scanner = new ArgScanner().Flag(name: "h").Flag(name: "help").Value(name: "path");

        if (!scanner.Parse(args: args)) {
            Console.Error.WriteLine(value: $"worktree-base: {scanner.Error}");

            return 2;
        }

        if (scanner.Has(name: "h") || scanner.Has(name: "help")) {
            Console.Out.WriteLine(value: HelpText);

            return 0;
        }

        if (scanner.Positionals.Count != 1) {
            Console.Error.WriteLine(value: "worktree-base: expected exactly one <sha-or-ref> argument.");

            return 2;
        }

        var baseRef = scanner.Positionals[0];
        var rawPath = (scanner.Get(name: "path") ?? Directory.GetCurrentDirectory());
        var path = (Path.IsPathRooted(path: rawPath)
            ? rawPath
            : Path.GetFullPath(path: rawPath)
        );
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

            Console.Error.WriteLine(value: "worktree-base: REFUSING to reset a dirty tree.");

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
