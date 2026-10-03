using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Puck.Hosting;

namespace Puck.Cli.Laws;

/// <summary>The change a law proof withholds: the first-parent change of <see cref="Revision"/>, or with no revision the
/// working tree's uncommitted change, narrowed to <see cref="Paths"/> when any are listed.</summary>
/// <param name="Revision">The commit whose change is the fix, or <see langword="null"/> for the uncommitted change.</param>
/// <param name="Paths">The repository-relative, forward-slashed paths to withhold; empty withholds every path the
/// commit changes outside <c>tests/</c>.</param>
internal sealed record LawFix(string? Revision, IReadOnlyList<string> Paths);
/// <summary>One build of the law's project: whether it succeeded, and its error lines when it did not.</summary>
internal sealed record LawBuild(bool Succeeded, IReadOnlyList<string> Errors);
/// <summary>One failed test: its name and the first line of its message.</summary>
internal sealed record LawFailure(string Test, string Message);
/// <summary>One run of the law: how many tests ran, which failed, and why the run reported nothing when it did
/// not.</summary>
internal sealed record LawRun(IReadOnlyList<string> Tests, IReadOnlyList<LawFailure> Failures, string? Error) {
    public int Total => Tests.Count;
}
/// <summary>Builds and runs a law in a tree. <see cref="DotnetLawRunner"/> is the real one; the laws over
/// <see cref="LawProof"/> substitute their own.</summary>
internal interface ILawRunner {
    /// <summary>Builds <paramref name="project"/> inside <paramref name="tree"/>.</summary>
    /// <param name="tree">The tree's root.</param>
    /// <param name="project">The project, relative to the tree.</param>
    /// <param name="cancellationToken">Cancels the build and waits for its process tree to exit.</param>
    /// <returns>The build's outcome.</returns>
    LawBuild Build(string tree, string project, CancellationToken cancellationToken);
    /// <summary>Runs the tests <paramref name="law"/> names in the already-built <paramref name="project"/>.</summary>
    /// <param name="tree">The tree's root.</param>
    /// <param name="project">The project, relative to the tree.</param>
    /// <param name="law">The law's name.</param>
    /// <param name="results">An empty directory the run may write its report into.</param>
    /// <param name="cancellationToken">Cancels the run and waits for its process tree to exit.</param>
    /// <returns>The run's outcome.</returns>
    LawRun Run(string tree, string project, string law, string results, CancellationToken cancellationToken);
}
/// <summary>
/// <c>puck laws prove</c>'s proof: that a law fails without its fix and passes with it. The proof runs in a detached
/// git worktree under a scratch directory, never in the caller's tree: the worktree starts at the caller's HEAD and
/// takes the caller's uncommitted and untracked files, then the fix is withheld (the commit's change reversed, or the
/// uncommitted change put back to HEAD), the law's project built and the law run, which must fail; then the fix is
/// restored, the project built again and the law run, which must pass. A build that fails in either phase refuses the
/// proof. The worktree and the scratch directory are removed whatever the outcome; a removal that fails is reported
/// with what it left behind and never changes the exit code.
/// </summary>
internal static partial class LawProof {
    private const string Verb = "laws prove";

    // Every git command that writes or deletes the proof tree enables long paths for that command alone: a built
    // tree's obj and bin paths pass Windows' 260-character limit, and git without them cannot delete those files.
    private static readonly string[] LongPaths = ["-c", "core.longpaths=true"];

    [GeneratedRegex(pattern: @"\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z")]
    private static partial Regex LawName();
    private static IReadOnlyList<string> Lines(string text) => [.. text.Split(separator: '\n')
        .Select(selector: static line => line.TrimEnd(trimChar: '\r'))
        .Where(predicate: static line => (line.Length > 0))];
    private static int Refuse(string what, string why) => CliExit.Refuse(
        verb: Verb,
        what: what,
        why: why
    );
    private static bool Git(string repository, out string stdout, out string error, params string[] arguments) {
        var result = CliGit.Run(
            arguments: arguments,
            repository: repository
        );

        stdout = result.Stdout;
        error = ((result.ExitCode == 0)
            ? string.Empty
            : $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr.Trim()}");

        return (result.ExitCode == 0);
    }
    // Tracked files that differ from HEAD, staged or not, and untracked files git does not ignore.
    private static bool TryReadDirty(string repositoryRoot, out IReadOnlyList<string> dirty, out string error) {
        dirty = [];

        if (!Git(repositoryRoot, out var changed, out error, "diff", "--name-only", "--no-renames", "HEAD")) {
            return false;
        }

        if (!Git(repositoryRoot, out var untracked, out error, "ls-files", "--others", "--exclude-standard")) {
            return false;
        }

        dirty = [.. Lines(text: changed).Concat(second: Lines(text: untracked)).Distinct(comparer: StringComparer.Ordinal).Order(comparer: StringComparer.Ordinal)];

        return true;
    }
    private static string? Read(string root, string path) {
        var full = Path.Combine(
            path1: root,
            path2: path
        );

        return (File.Exists(path: full)
            ? Convert.ToBase64String(inArray: File.ReadAllBytes(path: full))
            : null);
    }
    private static void Write(string root, string path, string? content) {
        var full = Path.Combine(
            path1: root,
            path2: path
        );

        if (content is null) {
            File.Delete(path: full);

            return;
        }

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: full)!);
        File.WriteAllBytes(
            bytes: Convert.FromBase64String(s: content),
            path: full
        );
    }
    // The nearest directory at or above the file's own, up to the tree, that holds a project file.
    private static string? OwningProject(string tree, string file) {
        for (var directory = Path.GetDirectoryName(path: file); ((directory is not null) && (directory.Length >= tree.Length)); directory = Path.GetDirectoryName(path: directory)) {
            var projects = Directory.GetFiles(
                path: directory,
                searchPattern: "*.csproj"
            );

            if (projects.Length == 1) {
                return projects[0];
            }
        }

        return null;
    }
    private static string Relative(string root, string path) => Path.GetRelativePath(
        path: path,
        relativeTo: root
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    // The project a law lives in: the one named, or the one test project whose sources declare a type named by one of
    // the law's segments, read from the rightmost segment leftward so `Class.Method` finds `Class`.
    private static bool TryResolveProject(string tree, string law, string? named, out string project, out string error) {
        project = string.Empty;
        error = string.Empty;

        if (named is not null) {
            var full = Path.GetFullPath(path: Path.Combine(
                path1: tree,
                path2: named
            ));

            if (Path.IsPathRooted(path: named) || !full.StartsWith(value: (tree + Path.DirectorySeparatorChar), comparisonType: Puck.Abstractions.PuckPaths.Comparison)) {
                error = $"--project {named} is outside the proven tree.";
                return false;
            }
            var candidates = (named.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".csproj")
                ? (File.Exists(path: full) ? [full] : [])
                : (Directory.Exists(path: full)
                    ? Directory.GetFiles(path: full, searchPattern: "*.csproj")
                    : []));

            if (candidates.Length != 1) {
                error = $"--project {named} names no single project file in the proven tree.";

                return false;
            }

            project = Relative(path: candidates[0], root: tree);

            return true;
        }

        var tests = Path.Combine(
            path1: tree,
            path2: "tests"
        );
        var sources = (Directory.Exists(path: tests)
            ? Directory.EnumerateFiles(path: tests, searchOption: SearchOption.AllDirectories, searchPattern: "*.cs").Order(comparer: StringComparer.Ordinal).Select(selector: path => (Path: path, Text: File.ReadAllText(path: path))).ToArray()
            : []);
        var segments = law.Split(separator: '.');

        for (var index = (segments.Length - 1); (index >= 0); index--) {
            var declaration = new Regex(pattern: $@"\b(class|record|struct)\s+{Regex.Escape(str: segments[index])}\b");
            var owners = sources.Where(predicate: source => declaration.IsMatch(input: source.Text))
                .Select(selector: source => OwningProject(file: source.Path, tree: tree))
                .OfType<string>()
                .Distinct(comparer: StringComparer.Ordinal)
                .Select(selector: path => Relative(path: path, root: tree))
                .Order(comparer: StringComparer.Ordinal)
                .ToArray();

            if (owners.Length == 1) {
                project = owners[0];

                return true;
            }

            if (owners.Length > 1) {
                error = $"'{segments[index]}' is declared in more than one test project ({string.Join(separator: ", ", values: owners)}); name one with --project.";

                return false;
            }
        }

        error = "no source under tests/ declares a type any segment of the law names; name its project with --project.";

        return false;
    }
    // Removes this proof's worktree registration and its scratch directory: null when both are gone, otherwise why
    // cleanup stopped. Never throws, so a cleanup failure cannot replace the proof's verdict.
    private static string? TryRemoveWorktree(string repositoryRoot, string tree, string scratch, Func<string, string[], ChildProcessResult> git) {
        var operation = "inspect proof worktree registration";

        try {
            operation = $"inspect proof worktree registration for {CliPaths.ToDisplay(fullPath: tree)}";
            var registered = git(arg1: repositoryRoot, arg2: [.. LongPaths, "worktree", "list", "--porcelain", "-z"]);

            if (registered.ExitCode != 0) {
                return $"cannot {operation}: {registered.Stderr.Trim()}";
            }
            if (registered.Stdout.Split(separator: '\0').Any(predicate: entry => string.Equals(a: entry, b: $"worktree {Puck.Abstractions.PuckPaths.Normalize(path: tree)}", comparisonType: Puck.Abstractions.PuckPaths.Comparison))) {
                operation = $"remove proof worktree and its registration for {CliPaths.ToDisplay(fullPath: tree)}";
                var removed = git(arg1: repositoryRoot, arg2: [.. LongPaths, "worktree", "remove", "--force", "--force", tree]);

                if (removed.ExitCode != 0) {
                    return $"cannot {operation}: {removed.Stderr.Trim()}";
                }
            }

            operation = $"remove proof scratch directory {CliPaths.ToDisplay(fullPath: scratch)}";
            try {
                Directory.Delete(path: scratch, recursive: true);
            } catch (DirectoryNotFoundException) {
                // The scratch directory is already gone; its registration was handled above.
            }

            return null;
        } catch (Exception exception) {
            return $"cannot {operation}: {exception.Message.ReplaceLineEndings(replacementText: " ")}";
        }
    }
    // Refuse links before copying or building: writes through a checked-out link escape the isolated tree.
    private static string? LinkedPath(string tree) {
        var pending = new Stack<string>();

        pending.Push(item: tree);
        while (pending.TryPop(result: out var directory)) {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path: directory)) {
                var attributes = File.GetAttributes(path: entry);

                if ((attributes & FileAttributes.ReparsePoint) != 0) {
                    return Relative(path: entry, root: tree);
                }
                if ((attributes & FileAttributes.Directory) != 0) {
                    pending.Push(item: entry);
                }
            }
        }
        return null;
    }
    private static void Describe(StringBuilder evidence, string phase, LawRun run) {
        if (run.Failures.Count == 0) {
            _ = evidence.Append(value: $"{phase}: {run.Total} of {run.Total} passed\n");

            return;
        }

        _ = evidence.Append(value: $"{phase}: {run.Failures.Count} of {run.Total} failed\n");

        foreach (var failure in run.Failures) {
            _ = evidence.Append(value: $"  {failure.Test}: {failure.Message}\n");
        }
    }
    private static bool TryPhase(ILawRunner runner, string tree, string project, string law, string results, string phase, CancellationToken cancellationToken, out LawRun run, out int refusal) {
        run = new LawRun(Error: null, Failures: [], Tests: []);
        Console.Error.WriteLine(value: $"laws prove: building {project} {phase}.");

        var build = runner.Build(
            cancellationToken: cancellationToken,
            project: project,
            tree: tree
        );

        if (!build.Succeeded) {
            refusal = Refuse(what: project, why: $"did not build {phase}, so the law cannot be judged:");

            foreach (var line in build.Errors) {
                Console.Error.WriteLine(value: $"  {line.Replace(comparisonType: StringComparison.OrdinalIgnoreCase, newValue: string.Empty, oldValue: tree)}");
            }

            return false;
        }

        Console.Error.WriteLine(value: $"laws prove: running {law} {phase}.");
        _ = Directory.CreateDirectory(path: results);
        run = runner.Run(
            cancellationToken: cancellationToken,
            law: law,
            project: project,
            results: results,
            tree: tree
        );

        if (run.Error is { } error) {
            refusal = Refuse(what: law, why: $"the run {phase} cannot be judged: {error}");

            return false;
        }

        if (run.Total == 0) {
            refusal = Refuse(what: law, why: $"selects no test in {project} {phase}.");

            return false;
        }

        refusal = CliExit.Success;

        return true;
    }

    /// <summary>Reads a Visual Studio test results (TRX) report into a run: every executed result, and the first line of
    /// each failure's message, ordered by test name. An explicit test the run did not opt into is reported
    /// <c>NotRunnable</c> and was never selected, so it is left out; any other result that did not pass or fail makes the
    /// run unfit to judge.</summary>
    /// <param name="report">The report's XML text.</param>
    /// <returns>The run.</returns>
    public static LawRun ReadReport(string report) {
        var document = XDocument.Parse(text: report);
        XNamespace schema = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        var results = document.Descendants(name: (schema + "UnitTestResult"))
            .Where(predicate: static result => (((string?)result.Attribute(name: "outcome")) != "NotRunnable"))
            .ToArray();
        var incomplete = results.FirstOrDefault(predicate: static result => (((string?)result.Attribute(name: "outcome")) is not ("Passed" or "Failed")));
        var infrastructureError = (document.Descendants(name: (schema + "RunInfo")).Any(predicate: static info => (((string?)info.Attribute(name: "outcome")) is "Error" or "Aborted" or "Timeout")) ||
            document.Descendants(name: (schema + "ResultSummary")).Any(predicate: static summary => (((string?)summary.Attribute(name: "outcome")) is "Error" or "Aborted" or "Timeout")) ||
            document.Descendants(name: (schema + "Counters")).Any(predicate: static counters => (new[] { "error", "timeout", "aborted" }.Any(predicate: name => (((int?)counters.Attribute(name: name)) is > 0)))));
        var failures = results.Where(predicate: static result => (((string?)result.Attribute(name: "outcome")) == "Failed"))
            .Select(selector: result => new LawFailure(
                Message: (Lines(text: (((string?)result.Descendants(name: (schema + "Message")).FirstOrDefault()) ?? string.Empty)).FirstOrDefault()?.Trim() ?? "no message"),
                Test: (((string?)result.Attribute(name: "testName")) ?? "?")
            ))
            .OrderBy(comparer: StringComparer.Ordinal, keySelector: static failure => failure.Test)
            .ToArray();

        return new LawRun(
            Error: (infrastructureError ? "the TRX report records an infrastructure failure." : ((incomplete is null) ? null : $"{((string?)incomplete.Attribute(name: "testName"))}: outcome {(((string?)incomplete.Attribute(name: "outcome")) ?? "missing")}; every selected test must execute.")),
            Failures: failures,
            Tests: [.. results.Select(selector: static result => (((string?)result.Attribute(name: "testName")) ?? "?")).Order(comparer: StringComparer.Ordinal)]
        );
    }
    /// <summary>Proves <paramref name="law"/> against <paramref name="fix"/> and prints the evidence on standard
    /// output.</summary>
    /// <param name="repositoryRoot">The caller's checkout; it is read, never written.</param>
    /// <param name="law">The law: a test name of dotted identifiers, matched as a fully qualified name prefix or
    /// part.</param>
    /// <param name="project">The law's project relative to the repository root, or <see langword="null"/> to find the test
    /// project that declares it.</param>
    /// <param name="fix">The change to withhold.</param>
    /// <param name="runner">Builds and runs the law.</param>
    /// <param name="scratchRoot">The directory the proof's scratch directory is created under: the temporary root for a
    /// real run.</param>
    /// <param name="cancellationToken">Cancels the proof; cleanup runs without the cancelled token.</param>
    /// <param name="cleanupGit">Runs cleanup's git commands; the real git process when omitted.</param>
    /// <param name="reportCleanupFailure">Writes the cleanup warning; standard error when omitted. A failed write
    /// cannot replace the proof's outcome.</param>
    /// <returns><see cref="CliExit.Success"/> when the law fails without the fix and passes with it,
    /// <see cref="CliExit.Failed"/> when it passes without the fix (it cannot fail) or fails with it, and
    /// <see cref="CliExit.Refused"/> for a build that failed, a law that selects no test, or a fix that cannot be
    /// withheld.</returns>
    public static int Prove(string repositoryRoot, string law, string? project, LawFix fix, ILawRunner runner, string scratchRoot, CancellationToken cancellationToken = default, Func<string, string[], ChildProcessResult>? cleanupGit = null, Action<string>? reportCleanupFailure = null) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!LawName().IsMatch(input: law)) {
            return Refuse(what: law, why: "a law is a test name of dotted identifiers, such as Class or Class.Method.");
        }

        if (
            (fix.Revision is null) &&
            (fix.Paths.Count == 0)
        ) {
            return Refuse(what: law, why: "name the fix to withhold with --fix <revision>, --file-list <json>, or both.");
        }

        if (!CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var head, revision: "HEAD")) {
            return Refuse(what: repositoryRoot, why: "has no HEAD commit.");
        }

        string[] withheld;
        string described;
        string? parent = null;
        string? commit = null;

        if (fix.Revision is { } revision) {
            if (!CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var resolved, revision: revision)) {
                return Refuse(what: revision, why: "does not resolve to a commit.");
            }

            commit = resolved;

            if (!CliGit.IsAncestor(candidate: commit, descendant: head, repository: repositoryRoot)) {
                return Refuse(what: revision, why: $"is not in the history of HEAD ({head[..12]}), so the tree being proven does not carry its change.");
            }

            if (!CliGit.TryResolveCommit(repository: repositoryRoot, resolved: out var first, revision: $"{commit}^1")) {
                return Refuse(what: revision, why: "is a root commit, so it has no change to withhold.");
            }

            parent = first;

            if (!Git(repositoryRoot, out var names, out var diffError, "diff", "--name-only", "--no-renames", parent, commit)) {
                return Refuse(what: revision, why: diffError);
            }

            var changed = Lines(text: names);

            if (fix.Paths.Count > 0) {
                if (fix.Paths.FirstOrDefault(predicate: path => !changed.Contains(value: path, comparer: StringComparer.Ordinal)) is { } untouched) {
                    return Refuse(what: untouched, why: $"is not a path {revision} changes, so there is nothing of it to withhold.");
                }

                withheld = [.. fix.Paths];
            } else {
                withheld = [.. changed.Where(predicate: static path => !path.StartsWith(comparisonType: StringComparison.Ordinal, value: "tests/"))];

                if (withheld.Length == 0) {
                    return Refuse(what: revision, why: "changes nothing outside tests/; name the paths to withhold with --file-list.");
                }
            }

            _ = Git(repositoryRoot, out var subject, out _, "log", "-1", "--format=%s", commit);
            described = $"{commit[..12]} {subject.Trim()}";
        } else {
            if (!TryReadDirty(dirty: out var dirty, error: out var dirtyError, repositoryRoot: repositoryRoot)) {
                return Refuse(what: repositoryRoot, why: dirtyError);
            }

            if (fix.Paths.FirstOrDefault(predicate: path => !dirty.Contains(value: path, comparer: StringComparer.Ordinal)) is { } clean) {
                return Refuse(what: clean, why: "has no uncommitted change to withhold.");
            }

            withheld = [.. fix.Paths];
            described = $"the uncommitted change over {head[..12]}";
        }

        withheld = [.. withheld.Order(comparer: StringComparer.Ordinal)];

        var scratch = Directory.CreateDirectory(path: Path.Combine(
            path1: scratchRoot,
            path2: $"puck-laws-{Path.GetFileNameWithoutExtension(path: Path.GetRandomFileName())}"
        )).FullName;
        var tree = Path.Combine(
            path1: scratch,
            path2: "tree"
        );
        var hooks = Path.Combine(path1: scratch, path2: "hooks");

        bool ProofGit(string repository, out string stdout, out string error, params string[] arguments) {
            var result = CliGit.RunAsync(arguments: [.. LongPaths, "-c", $"core.hooksPath={hooks}", .. arguments], cancellationToken: cancellationToken, repository: repository).GetAwaiter().GetResult();

            stdout = result.Stdout;
            error = ((result.ExitCode == 0) ? string.Empty : $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr.Trim()}");
            return (result.ExitCode == 0);
        }

        try {
            _ = Directory.CreateDirectory(path: hooks);
            Console.Error.WriteLine(value: $"laws prove: copying {head[..12]} and the working tree's changes into {CliPaths.ToDisplay(fullPath: tree)}.");

            if (!ProofGit(repositoryRoot, out _, out var addError, "worktree", "add", "--detach", "--quiet", tree, head)) {
                return Refuse(what: tree, why: addError);
            }
            if (LinkedPath(tree: tree) is { } linked) {
                return Refuse(what: linked, why: "is a link in the proven tree; copying or building through it would escape isolation.");
            }

            if (!TryReadDirty(dirty: out var mirrored, error: out var mirrorError, repositoryRoot: repositoryRoot)) {
                return Refuse(what: repositoryRoot, why: mirrorError);
            }

            foreach (var path in mirrored) {
                cancellationToken.ThrowIfCancellationRequested();
                Write(
                    content: Read(path: path, root: repositoryRoot),
                    path: path,
                    root: tree
                );
            }

            // The worktree's own index takes the mirrored state, so a three-way reverse sees no unstaged change.
            if (!ProofGit(tree, out _, out var stageError, "add", "--all")) {
                return Refuse(what: tree, why: stageError);
            }

            if (!TryResolveProject(error: out var projectError, law: law, named: project, project: out var lawProject, tree: tree)) {
                return Refuse(what: law, why: projectError);
            }

            var original = withheld.ToDictionary(
                comparer: StringComparer.Ordinal,
                elementSelector: path => Read(path: path, root: tree),
                keySelector: static path => path
            );

            if (commit is not null) {
                var patch = Path.Combine(
                    path1: scratch,
                    path2: "fix.patch"
                );

                if (!ProofGit(repositoryRoot, out var diff, out var patchError, ["diff", "--binary", "--no-renames", parent!, commit, "--", .. withheld])) {
                    return Refuse(what: commit[..12], why: patchError);
                }

                File.WriteAllText(
                    contents: diff,
                    encoding: new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    path: patch
                );

                if (!ProofGit(tree, out _, out var applyError, "apply", "--reverse", "--3way", patch)) {
                    return Refuse(what: commit[..12], why: $"its change does not reverse cleanly over HEAD: {applyError}");
                }
            } else {
                foreach (var path in withheld) {
                    if (ProofGit(tree, out _, out _, "cat-file", "-e", $"HEAD:{path}")) {
                        if (!ProofGit(tree, out _, out var checkoutError, "checkout", "HEAD", "--", path)) {
                            return Refuse(what: path, why: checkoutError);
                        }
                    } else {
                        Write(content: null, path: path, root: tree);
                    }
                }
            }

            if (withheld.All(predicate: path => string.Equals(a: Read(path: path, root: tree), b: original[path], comparisonType: StringComparison.Ordinal))) {
                return Refuse(what: law, why: "withholding the fix changed no file, so nothing would be proven.");
            }

            if (LinkedPath(tree: tree) is { } withheldLink) {
                return Refuse(what: withheldLink, why: "withholding created a link in the proven tree.");
            }
            if (!TryPhase(cancellationToken: cancellationToken, law: law, phase: "with the fix withheld", project: lawProject, refusal: out var refusal, results: Path.Combine(path1: scratch, path2: "withheld"), run: out var without, runner: runner, tree: tree)) {
                return refusal;
            }

            var evidence = new StringBuilder();

            _ = evidence.Append(value: $"Law: {law} ({lawProject})\n");
            _ = evidence.Append(value: $"Withheld: {described}\n");

            foreach (var path in withheld) {
                _ = evidence.Append(value: $"  {path}\n");
            }

            Describe(evidence: evidence, phase: "Without the fix", run: without);

            if (without.Failures.Count == 0) {
                Console.Out.Write(value: evidence.ToString());
                _ = Refuse(what: law, why: "cannot fail: it passes with the fix withheld.");

                return CliExit.Failed;
            }

            foreach (var (path, content) in original) {
                Write(content: content, path: path, root: tree);
            }

            if (!TryPhase(cancellationToken: cancellationToken, law: law, phase: "with the fix restored", project: lawProject, refusal: out refusal, results: Path.Combine(path1: scratch, path2: "restored"), run: out var with, runner: runner, tree: tree)) {
                return refusal;
            }
            if (!without.Tests.SequenceEqual(second: with.Tests, comparer: StringComparer.Ordinal)) {
                return Refuse(what: law, why: "the withheld and restored runs executed different tests, so the failed law was not proven to pass.");
            }

            Describe(evidence: evidence, phase: "With the fix", run: with);
            Console.Out.Write(value: evidence.ToString());

            if (with.Failures.Count > 0) {
                _ = Refuse(what: law, why: "fails with the fix in place, so the fix does not make it pass.");

                return CliExit.Failed;
            }

            cancellationToken.ThrowIfCancellationRequested();
            return CliExit.Success;
        } finally {
            if (TryRemoveWorktree(git: (cleanupGit ?? CliGit.Run), repositoryRoot: repositoryRoot, scratch: scratch, tree: tree) is { } failure) {
                try {
                    (reportCleanupFailure ?? Console.Error.WriteLine).Invoke(obj: $"laws prove: {failure}; scratch directory: {CliPaths.ToDisplay(fullPath: scratch)}; cleanup may have left files or its registration behind and the exit code still reports the proof.");
                } catch (Exception) {
                    // A closed error stream cannot receive the warning or replace the proof's outcome.
                }
            }
        }
    }
}
