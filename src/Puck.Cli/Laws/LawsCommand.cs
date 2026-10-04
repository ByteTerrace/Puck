using System.CommandLine;

namespace Puck.Cli.Laws;

/// <summary><c>puck laws</c> — the verbs over the repository's laws (its xUnit tests): <c>prove</c> shows that a law
/// fails without its fix and passes with it.</summary>
internal static class LawsCommand {
    private const string ProveVerb = "laws prove";

    private static int Prove(string law, string[] alsoLaws, string? fix, string? fileList, string? project, CancellationToken cancellationToken) {
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
            alsoLaws: alsoLaws,
            cancellationToken: cancellationToken,
            fix: new LawFix(Paths: paths, Revision: fix),
            law: law,
            project: ((project is null)
                ? null
                : CliPaths.ToDisplay(fullPath: Path.GetFullPath(path: project), relativeTo: repositoryRoot)),
            repositoryRoot: repositoryRoot,
            runner: new DotnetLawRunner(),
            lawTreesRoot: LawProofTree.DefaultRoot,
            scratchRoot: Path.GetTempPath()
        );
    }
    private static Command CreateProve() {
        var lawArgument = new Argument<string>(name: "law") { Description = "The law: a test name of dotted identifiers (Class or Class.Method), matched anywhere in each test's fully qualified method name." };
        var alsoLawOption = new Option<string[]>(name: "--also-law") { DefaultValueFactory = static _ => [], Description = "Another exact dotted selector in the same project (repeatable). Every selector must fail withheld and pass restored; each side builds once." };
        var fixOption = new Option<string>(name: "--fix") { Description = "The commit whose change is the fix; its first-parent change is reversed, and it must be in HEAD's history." };
        var fileListOption = CliOptions.FileList(description: "The paths to withhold: with --fix, the subset of the commit's change to reverse; without it, the paths whose uncommitted change is the fix.");
        var projectOption = new Option<string>(name: "--project") { Description = "The law's test project or its directory (default: the test project whose sources declare the law's class)." };
        var command = new Command(
            description: "Prove that a law fails without its fix and passes with it.",
            name: "prove"
        ) { lawArgument, alsoLawOption, fixOption, fileListOption, projectOption };

        command.Detail(detail: """
              The proof never touches the working tree. It keeps a shared-object git clone under the
              per-user Puck/law-trees directory, keyed by the repository's common git directory, so
              every worktree of one repository shares it. This clone
              is never registered as a worktree. It checks out HEAD, removes unignored strays, retains
              its own obj/bin outputs, copies the caller's uncommitted and untracked files, and
              withholds the fix there:
                --fix <revision>          reverse the commit's first-parent change (three-way, over
                                          HEAD) on every path it changes outside tests/, or on the
                                          --file-list paths when given
                --file-list <json>        put each listed path back to HEAD, removing one HEAD lacks
              It then builds the law's project in Release and runs the law, which must fail; restores the
              fix, builds and runs again, and the law must pass. Each side builds only that project's
              dependency closure, once for all selected tests. An exclusive lock leases the clone;
              a concurrent proof uses a fresh scratch worktree (cold). A missing, corrupt or wrongly
              sourced clone is recreated cold; an unavailable cache also uses the scratch fallback.
              Cleanup keeps the clone on every outcome, removes per-proof scratch and any fallback
              worktree and its own registration, and never changes the proof's exit code. Git commands
              enable long paths. Cleanup failures name their operation and scratch on standard error.
              Caller Git hooks are disabled; links in the proven tree and projects outside it are refused.
              Every selected test must execute, and both legs must execute the same tests. A skipped
              test, an aborted process or an inconsistent report refuses the proof.
              Repeat --also-law for independent selectors in the same project. Each side builds once,
              then runs each selector separately, retaining its own report and verdict. Every selector
              must fail withheld; overlapping selections and different projects are refused.

              Standard output carries the evidence block for a commit body: the law and its project,
              what was withheld, each failure's first message line without the fix, and the pass with it.
              Standard error reports each side's build work as:
                laws prove: built with the fix withheld: <n> project(s) compiled, <m> up to date, <t> target(s)
              The restored side uses "with the fix restored". Counts come from MSBuild events;
              up-to-date CoreCompile targets are not counted as compilations or executed targets.

              Exit codes: 0 proven; 1 the law cannot fail (it passes with the fix withheld) or fails with
              the fix in place; 2 a build failed in either phase, the law selects no test, or the fix
              cannot be withheld; 130 cancelled after cleanup.
            """);
        command.SetAction(action: (parseResult, cancellationToken) => Task.Run(function: () => Prove(
            alsoLaws: parseResult.GetValue(option: alsoLawOption) ?? [],
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
