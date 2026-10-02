using System.CommandLine;

namespace Puck.Cli.Laws;

/// <summary><c>puck laws</c> — the verbs over the repository's laws (its xUnit tests): <c>prove</c> shows that a law
/// fails without its fix and passes with it.</summary>
internal static class LawsCommand {
    private const string ProveVerb = "laws prove";

    private static int Prove(string law, string? fix, string? fileList, string? project, CancellationToken cancellationToken) {
        if (!CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot)) {
            return CliExit.Refused;
        }

        string[] paths = [];

        if (fileList is not null) {
            var listed = CliOptions.ReadFileList(
                baseDirectory: Environment.CurrentDirectory,
                manifest: fileList
            );
            var outside = listed.FirstOrDefault(predicate: path => Path.GetRelativePath(path: path, relativeTo: repositoryRoot).StartsWith(comparisonType: StringComparison.Ordinal, value: ".."));

            if (outside is not null) {
                return CliExit.Refuse(verb: ProveVerb, what: CliPaths.ToDisplay(fullPath: outside), why: "is outside the repository.");
            }

            paths = [.. listed.Select(selector: path => CliPaths.ToDisplay(fullPath: path, relativeTo: repositoryRoot))];
        }

        return LawProof.Prove(
            cancellationToken: cancellationToken,
            fix: new LawFix(Paths: paths, Revision: fix),
            law: law,
            project: ((project is null)
                ? null
                : CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: project), relativeTo: repositoryRoot)),
            repositoryRoot: repositoryRoot,
            runner: new DotnetLawRunner(),
            scratchRoot: Path.GetTempPath()
        );
    }
    private static Command CreateProve() {
        var lawArgument = new Argument<string>(name: "law") { Description = "The law: a test name of dotted identifiers (Class or Class.Method), selected as FullyQualifiedName~<law>." };
        var fixOption = new Option<string>(name: "--fix") { Description = "The commit whose change is the fix; its first-parent change is reversed, and it must be in HEAD's history." };
        var fileListOption = CliOptions.FileList(description: "The paths to withhold: with --fix, the subset of the commit's change to reverse; without it, the paths whose uncommitted change is the fix.");
        var projectOption = new Option<string>(name: "--project") { Description = "The law's test project or its directory (default: the test project whose sources declare the law's class)." };
        var command = new Command(
            description: "Prove that a law fails without its fix and passes with it.",
            name: "prove"
        ) { lawArgument, fixOption, fileListOption, projectOption };

        command.Detail(detail: """
              The proof never touches the working tree. It adds a detached git worktree of HEAD under a
              scratch directory, copies the working tree's uncommitted and untracked files into it, and
              withholds the fix there:
                --fix <revision>          reverse the commit's first-parent change (three-way, over
                                          HEAD) on every path it changes outside tests/, or on the
                                          --file-list paths when given
                --file-list <json>        put each listed path back to HEAD, removing one HEAD lacks
              It then builds the law's project in Release and runs the law, which must fail; restores the
              fix, builds and runs again, and the law must pass. The worktree and scratch directory are
              removed on success, refusal, exception and cancellation. Cleanup removes only this
              proof's registration and reports any removal failure. Caller Git hooks are disabled;
              links in the proven tree and projects outside it are refused.
              Every selected test must execute, and both legs must execute the same tests. A skipped
              test, an aborted process or an inconsistent report refuses the proof.

              Standard output carries the evidence block for a commit body: the law and its project,
              what was withheld, each failure's first message line without the fix, and the pass with it.

              Exit codes: 0 proven; 1 the law cannot fail (it passes with the fix withheld) or fails with
              the fix in place; 2 a build failed in either phase, the law selects no test, or the fix
              cannot be withheld; 130 cancelled after cleanup.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Prove(
            cancellationToken: cancellationToken,
            fileList: parseResult.GetValue(option: fileListOption),
            fix: parseResult.GetValue(option: fixOption),
            law: parseResult.GetRequiredValue(argument: lawArgument),
            project: parseResult.GetValue(option: projectOption)
        ), cancellationToken: cancellationToken));

        return command;
    }

    public static Command Create() => new(
        description: "Prove the repository's laws against the fixes they hold.",
        name: "laws"
    ) {
        CreateProve(),
    };
}
