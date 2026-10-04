using Puck.Cli.Laws;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="LawProof"/> proves a law that fails without its fix and passes with it,
/// reports a law that passes with the fix withheld as unable to fail, refuses a build that fails, and does all of it in
/// a clone of its own, leaving the caller's checkout, its worktree list and the scratch root as it found them. Each
/// law runs over a small git checkout and a runner that stands in for <c>dotnet build</c> and <c>dotnet test</c>: the
/// law passes only when the fix's file reads <c>fixed</c>, and the build fails on a file that reads
/// <c>unbuildable</c>.</summary>
public sealed partial class LawProofLawTests {
    private const string FixPath = "src/Lib/Fix.cs";
    private const string Law = "FixLawTests.Holds";
    private const string Project = "tests/Lib.Tests/Lib.Tests.csproj";

    private sealed class FakeRunner : ILawRunner {
        public Action<string, CancellationToken>? BeforeBuild { get; init; }
        public LawBuild? BuildResult { get; init; }
        public List<string> Builds { get; } = [];
        public Func<string, LawRun>? Report { get; init; }
        public List<string> Runs { get; } = [];

        public LawBuild Build(string tree, string project, string logDirectory, CancellationToken cancellationToken) {
            Assert.Equal(actual: project, expected: Project);
            Builds.Add(item: tree);
            BeforeBuild?.Invoke(arg1: tree, arg2: cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (BuildResult is not null) { return BuildResult; }

            return ((File.ReadAllText(path: Path.Combine(path1: tree, path2: FixPath)) == "unbuildable")
                ? new LawBuild(Errors: [$"{tree}/{FixPath}(1,1): error CS0000: unbuildable"], Succeeded: false)
                : new LawBuild(Errors: [], Succeeded: true));
        }
        public LawRun Run(string tree, string project, string law, string results, CancellationToken cancellationToken) {
            Assert.Equal(actual: project, expected: Project);
            Runs.Add(item: tree);

            var content = File.ReadAllText(path: Path.Combine(path1: tree, path2: FixPath));

            if (Report is not null) { return Report(arg: content); }

            return new LawRun(
                Error: null,
                Failures: ((content == "fixed")
                    ? []
                    : [new LawFailure(Message: $"Assert.Equal() Failure: {content}", Test: $"Lib.Tests.{law}")]),
                Tests: [$"Lib.Tests.{law}"]
            );
        }
    }

    // A checkout whose law project declares FixLawTests and whose fix file starts as `initial`.
    private static GitScratchCheckout Checkout(string initial) {
        var checkout = new GitScratchCheckout();

        checkout.Write(name: ".gitignore", text: "bin/\nobj/\n");
        checkout.Write(name: Project, text: "<Project />\n");
        checkout.Write(name: "tests/Lib.Tests/FixLawTests.cs", text: "public sealed class FixLawTests { }\n");
        checkout.Write(name: FixPath, text: initial);
        checkout.Write(name: "src/Lib/Other.cs", text: "other\n");
        _ = checkout.Commit(message: "initial");

        return checkout;
    }
    private static (int ExitCode, string Output, string Error) Prove(GitScratchCheckout checkout, LawFix fix, FakeRunner runner, TemporaryDirectory scratch) => ConsoleCapture.RunSplit(run: () => LawProof.Prove(
        fix: fix,
        law: Law,
        project: null,
        repositoryRoot: checkout.Root,
        runner: runner,
        scratchRoot: scratch.RootPath,
        lawTreesRoot: LawTreesRoot(checkout: checkout)
    ));
    private static string LawTreesRoot(GitScratchCheckout checkout) => Path.Combine(path1: Path.GetDirectoryName(path: checkout.Root)!, path2: "law-trees");
    private static LawProofTree HoldTree(GitScratchCheckout checkout) => (LawProofTree.TryAcquire(root: LawTreesRoot(checkout: checkout), repository: checkout.Root, reason: out _) ?? throw new InvalidOperationException(message: "test could not hold the proof-tree lock"));
    // The proof leaves the caller's checkout clean, its worktree list holding only itself, and the scratch root empty.
    private static void AssertNothingLeftBehind(GitScratchCheckout checkout, TemporaryDirectory scratch, string status) {
        Assert.Equal(actual: checkout.Git("status", "--porcelain"), expected: status);
        Assert.Single(collection: checkout.Git("worktree", "list", "--porcelain").Split(separator: '\n'), predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree "));
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: scratch.RootPath));
    }

    [Fact]
    public void ALawThatCanFailIsProvenWithItsEvidence() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        checkout.Write(name: "tests/Lib.Tests/FixLawTests.cs", text: "public sealed class FixLawTests { void Holds() { } }\n");

        var fix = checkout.Commit(message: "lib: fix the thing");
        var runner = new FakeRunner();

        var (exitCode, output, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.Equal(
            actual: output.ReplaceLineEndings(replacementText: "\n"),
            expected: $"""
                Law: {Law} ({Project})
                Withheld: {fix[..12]} lib: fix the thing
                  {FixPath}
                Without the fix: 1 of 1 failed
                  Lib.Tests.{Law}: Assert.Equal() Failure: broken
                With the fix: 1 of 1 passed

                """
        );
        Assert.Equal(actual: runner.Builds.Count, expected: 2);
        Assert.DoesNotContain(collection: runner.Builds, filter: tree => tree.StartsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: checkout.Root));
        Assert.Equal(actual: checkout.Read(name: FixPath), expected: "fixed");
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ALawThatPassesWithTheFixWithheldCannotFail() {
        using var checkout = Checkout(initial: "fixed");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: "src/Lib/Other.cs", text: "changed\n");
        _ = checkout.Commit(message: "lib: an unrelated change");

        var runner = new FakeRunner();

        var (exitCode, output, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.Equal(actual: exitCode, expected: CliExit.Failed);
        Assert.Contains(actualString: output, expectedSubstring: "Without the fix: 1 of 1 passed");
        Assert.Contains(actualString: error, expectedSubstring: "cannot fail: it passes with the fix withheld");
        Assert.Single(collection: runner.Runs);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ABuildThatFailsWithTheFixWithheldIsRefused() {
        using var checkout = Checkout(initial: "unbuildable");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");

        var runner = new FakeRunner();

        var (exitCode, output, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Empty(collection: output);
        Assert.Contains(actualString: error, expectedSubstring: "did not build with the fix withheld");
        Assert.Contains(actualString: error, expectedSubstring: $"  /{FixPath}(1,1): error CS0000: unbuildable");
        Assert.Empty(collection: runner.Runs);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void AnUncommittedFixIsWithheldInTheCopyAndKeptInTheWorkingTree() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        checkout.Write(name: "tests/Lib.Tests/NewLawTests.cs", text: "public sealed class NewLawTests { }\n");

        var runner = new FakeRunner();

        var (exitCode, output, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [FixPath], Revision: null), runner: runner, scratch: scratch);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.Contains(actualString: output, expectedSubstring: "Without the fix: 1 of 1 failed");
        Assert.Contains(actualString: output, expectedSubstring: "With the fix: 1 of 1 passed");
        Assert.Equal(actual: checkout.Read(name: FixPath), expected: "fixed");
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: $" M {FixPath}\n?? tests/Lib.Tests/NewLawTests.cs\n");
    }
    [Fact]
    public void AFixOutsideHeadsHistoryIsRefusedBeforeAnythingIsCopied() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        _ = checkout.Git("switch", "--quiet", "--create", "elsewhere");
        checkout.Write(name: FixPath, text: "fixed");
        var elsewhere = checkout.Commit(message: "lib: fix elsewhere");

        _ = checkout.Git("switch", "--quiet", "main");

        var runner = new FakeRunner();

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: elsewhere), runner: runner, scratch: scratch);

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "is not in the history of HEAD");
        Assert.Empty(collection: runner.Builds);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AProjectOutsideTheProvenTreeIsRefusedBeforeBuilding(bool absolute) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner { BuildResult = new LawBuild(Errors: ["external project reached the build runner"], Succeeded: false) };
        var outside = (absolute ? Path.Combine(path1: checkout.Root, path2: Project) : "../../Outside.csproj");

        File.WriteAllText(path: Path.Combine(path1: scratch.RootPath, path2: "Outside.csproj"), contents: "<Project />");

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => LawProof.Prove(
            repositoryRoot: checkout.Root, law: Law, project: outside, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratchRoot: scratch.RootPath, lawTreesRoot: LawTreesRoot(checkout: checkout)));

        Assert.Empty(collection: runner.Builds);
        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "outside the proven tree");
        File.Delete(path: Path.Combine(path1: scratch.RootPath, path2: "Outside.csproj"));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ProofGitCommandsNeverInvokeTheCallersHooks() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        // Git runs this hook after both worktree creation and checkout of an uncommitted fix.
        checkout.Write(name: ".git/proof-hooks/post-checkout", text: "#!/bin/sh\nprintf touched > \"$(git rev-parse --git-common-dir)/proof-hook-ran\"\n");
        if (!OperatingSystem.IsWindows()) {
            File.SetUnixFileMode(path: Path.Combine(path1: checkout.Root, path2: ".git/proof-hooks/post-checkout"), mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        _ = checkout.Git("config", "core.hooksPath", Path.Combine(path1: checkout.Root, path2: ".git/proof-hooks"));
        checkout.Write(name: FixPath, text: "fixed");
        var runner = new FakeRunner();

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [FixPath], Revision: null), runner: runner, scratch: scratch);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.False(condition: File.Exists(path: Path.Combine(path1: checkout.Root, path2: ".git/proof-hook-ran")));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: $" M {FixPath}\n");
    }
    [Fact]
    public void CancellationReachesTheRunnerAndRemovesTheWorktree() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        using var cancellation = new CancellationTokenSource();

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            BeforeBuild = (_, token) => {
                Assert.Equal(expected: cancellation.Token, actual: token);
                cancellation.Cancel();
            },
        };

        Assert.Throws<OperationCanceledException>(testCode: () => ConsoleCapture.RunSplit(run: () => LawProof.Prove(
            repositoryRoot: checkout.Root, law: Law, project: null, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratchRoot: scratch.RootPath, lawTreesRoot: LawTreesRoot(checkout: checkout), cancellationToken: cancellation.Token)));

        Assert.Empty(collection: runner.Runs);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
        Assert.IsAssignableFrom<System.CommandLine.Invocation.AsynchronousCommandLineAction>(@object: LawsCommand.Create().Subcommands.Single().Action);
    }
    [Fact]
    public void AnExceptionRemovesEvenALockedProofWorktree() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            BeforeBuild = (tree, _) => {
                checkout.Git("worktree", "lock", tree);
                throw new InvalidOperationException(message: "runner failed");
            },
        };

        var exception = Assert.Throws<InvalidOperationException>(testCode: () => Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch));

        Assert.Equal(expected: "runner failed", actual: exception.Message);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [InlineData("NotExecuted")]
    [InlineData("Skipped")]
    [InlineData("Error")]
    [InlineData("Timeout")]
    [InlineData("Inconclusive")]
    [Theory]
    public void ASelectedTestThatDoesNotExecuteCannotProveALaw(string outcome) {
        var run = LawProof.ReadReport(report: $"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="{outcome}" />
              <UnitTestResult testName="Other" outcome="Passed" /></Results>
            </TestRun>
            """);

        Assert.NotNull(@object: run.Error);
        Assert.Contains(expectedSubstring: "Holds", actualString: run.Error);
    }
    // A failed result's output lines (Output/TextMessages) precede its ErrorInfo, and each is a Message element too.
    [Fact]
    public void AFailuresMessageIsItsErrorInfosNotItsOutputLines() {
        var run = LawProof.ReadReport(report: """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="Failed">
                <Output>
                  <TextMessages><Message>run directory kept: D:/temp/run</Message></TextMessages>
                  <ErrorInfo><Message>Assert.Equal() Failure: the assertion the law was written for
            second line</Message></ErrorInfo>
                </Output>
              </UnitTestResult></Results>
            </TestRun>
            """);

        Assert.Null(@object: run.Error);
        Assert.Equal(
            actual: Assert.Single(collection: run.Failures).Message,
            expected: "Assert.Equal() Failure: the assertion the law was written for"
        );
    }
    [Fact]
    public void AnExplicitTestTheRunDidNotOptIntoIsNotSelected() {
        var run = LawProof.ReadReport(report: """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Deep" outcome="NotRunnable" />
              <UnitTestResult testName="Holds" outcome="Passed" /></Results>
            </TestRun>
            """);

        Assert.Null(@object: run.Error);
        Assert.Equal(actual: Assert.Single(collection: run.Tests), expected: "Holds");
    }
    [InlineData(0, true, "Passed")]
    [InlineData(1, false, "Failed")]
    [InlineData(2, false, "Passed")]
    [InlineData(0, false, "Failed")]
    [Theory]
    public void AnAbortedOrInconsistentProcessCannotProveALaw(int exitCode, bool timedOut, string outcome) {
        var process = new CliProcessResult(ExitCode: exitCode, OutputLines: [], Stderr: "", Stdout: "", TimedOut: timedOut);
        var run = DotnetLawRunner.ReadRun(report: $"""
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="{outcome}" /></Results>
            </TestRun>
            """, run: process);

        Assert.NotNull(@object: run.Error);
    }
    [Fact]
    public void AFailedTestMustRunAgainWithTheFixRestored() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            Report = content => ((content == "fixed")
            ? new LawRun(Error: null, Failures: [], Tests: ["Other"])
            : new LawRun(Tests: ["Holds", "Other"], Failures: [new LawFailure(Message: "broken", Test: "Holds")], Error: null)),
        };

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.Equal(actual: exitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: error, expectedSubstring: "executed different tests");
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void AHostCrashWithReportedFailuresIsNotARedLeg() {
        var process = new CliProcessResult(ExitCode: CliTestRun.TestsFailed, OutputLines: [], Stderr: "", Stdout: "", TimedOut: false);
        var run = DotnetLawRunner.ReadRun(report: """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="Failed" /></Results>
              <ResultSummary outcome="Failed"><RunInfos><RunInfo outcome="Error">
                <Text>The test host crashed.</Text>
              </RunInfo></RunInfos></ResultSummary>
            </TestRun>
            """, run: process);

        Assert.NotNull(@object: run.Error);
    }
    [Fact]
    public void AFailedTestIsAVerdictNotAnInfrastructureFault() {
        var process = new CliProcessResult(ExitCode: CliTestRun.TestsFailed, OutputLines: [], Stderr: "", Stdout: "", TimedOut: false);
        var run = DotnetLawRunner.ReadRun(report: """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="Failed"><Output><ErrorInfo><Message>broken</Message></ErrorInfo></Output></UnitTestResult></Results>
              <ResultSummary outcome="Failed"><Counters total="1" executed="1" passed="0" failed="1" error="0" timeout="0" aborted="0" /></ResultSummary>
            </TestRun>
            """, run: process);

        Assert.Null(@object: run.Error);
        Assert.Equal(actual: Assert.Single(collection: run.Failures).Message, expected: "broken");
    }
    [Fact]
    public void AnAbortedCounterCannotProveALaw() {
        var process = new CliProcessResult(ExitCode: CliTestRun.TestsFailed, OutputLines: [], Stderr: "", Stdout: "", TimedOut: false);
        var run = DotnetLawRunner.ReadRun(report: """
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Results><UnitTestResult testName="Holds" outcome="Failed" /></Results>
              <ResultSummary outcome="Failed"><Counters total="2" executed="1" passed="0" failed="1" error="0" timeout="0" aborted="1" /></ResultSummary>
            </TestRun>
            """, run: process);

        Assert.NotNull(@object: run.Error);
    }
    [Fact]
    public void AProofRefusesLinksBeforeMirroringOrBuilding() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        var link = Path.Combine(path1: checkout.Root, path2: "src/Lib/Link.cs");

        _ = checkout.Git("config", "core.symlinks", "true");
        try {
            File.CreateSymbolicLink(path: link, pathToTarget: Path.Combine(path1: checkout.Root, path2: FixPath));
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException or PlatformNotSupportedException)) {
            Assert.Skip(reason: $"symbolic links are unavailable: {exception.Message}");
            return;
        }
        try {
            _ = checkout.Commit(message: "lib: link");
            checkout.Write(name: FixPath, text: "fixed");
            _ = checkout.Commit(message: "lib: fix");
            var runner = new FakeRunner();

            var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

            Assert.Empty(collection: runner.Builds);
            Assert.Equal(actual: exitCode, expected: CliExit.Refused);
            Assert.Contains(actualString: error, expectedSubstring: "link in the proven tree");
            Assert.Equal(expected: "fixed", actual: checkout.Read(name: FixPath));
            AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
        } finally {
            File.Delete(path: link);
        }
    }
    [Fact]
    public void CleanupNeverPrunesAnotherWorktreesRegistration() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        using var other = new TemporaryDirectory(prefix: "puck-laws-other-law-");
        var otherTree = other.PathOf(name: "tree");

        _ = checkout.Git("worktree", "add", "--detach", "--quiet", otherTree, "HEAD");
        Directory.Delete(path: otherTree, recursive: true);
        _ = checkout.Git("config", "gc.worktreePruneExpire", "now");
        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");

        // Read last before the proof, so the registration it must keep is shown to exist when the proof starts.
        var before = checkout.Git("worktree", "list", "--porcelain");

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: new FakeRunner(), scratch: scratch);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        var registrations = checkout.Git("worktree", "list", "--porcelain");
        var otherEntry = $"worktree {Puck.Abstractions.PuckPaths.Normalize(path: otherTree)}";

        Assert.Contains(actualString: before, expectedSubstring: otherEntry);
        Assert.Contains(actualString: registrations, expectedSubstring: otherEntry);
        Assert.Equal(expected: 2, actual: registrations.Split(separator: '\n').Count(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "worktree ")));
        Assert.Empty(collection: Directory.EnumerateFileSystemEntries(path: scratch.RootPath));
    }
    [Fact]
    public void CleanupRemovesABuiltTreeWhosePathsPassTheWindowsLimit() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        string? deepest = null;

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");

        // A stand-in for the build's obj and bin output: a file whose full path passes 260 characters.
        var runner = new FakeRunner {
            BeforeBuild = (tree, cancellationToken) => {
                var directory = Path.Combine(path1: tree, path2: "obj");

                while (directory.Length < 300) {
                    directory = Path.Combine(path1: directory, path2: "Release-net10.0-intermediate");
                }

                _ = Directory.CreateDirectory(path: directory);
                deepest = Path.Combine(path1: directory, path2: "Lib.Tests.AssemblyInfoInputs.cache");
                File.WriteAllText(contents: "built", path: deepest);
            },
        };

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (deepest?.Length > 260), userMessage: deepest);
        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.DoesNotContain(actualString: error, expectedSubstring: "exit code still reports the proof");
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ACleanupThatFailsAfterAProvenLawKeepsTheVerdictAndNamesWhatItLeft() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        var builds = 0;
        FileStream? held = null;

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");

        // The restored phase leaves a file in the proof tree open without sharing, as a lingering build process would,
        // so a platform that refuses to delete an open file cannot clean the tree up.
        var runner = new FakeRunner {
            BeforeBuild = (tree, _) => {
                if (++builds == 2) {
                    held = new FileStream(access: FileAccess.ReadWrite, mode: FileMode.Create, path: Path.Combine(path1: tree, path2: "held.lock"), share: FileShare.None);
                }
            },
        };

        int exitCode;
        string error;
        bool leftBehind;

        try {
            (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);
            leftBehind = Directory.EnumerateFileSystemEntries(path: scratch.RootPath).Any();
        } finally {
            held?.Dispose();
        }

        Assert.NotNull(@object: held);
        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.Equal(expected: OperatingSystem.IsWindows(), actual: leftBehind);

        if (leftBehind) {
            Assert.Contains(actualString: error, expectedSubstring: "exit code still reports the proof");
            Assert.Contains(actualString: error, expectedSubstring: "cannot remove proof worktree and its registration");
            Assert.Contains(actualString: error, expectedSubstring: CliPaths.ToDisplay(fullPath: Directory.EnumerateDirectories(path: scratch.RootPath).Single()));
        } else {
            Assert.DoesNotContain(actualString: error, expectedSubstring: "exit code still reports the proof");
        }
    }
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(130, 0)]
    [InlineData(-1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    [InlineData(130, 1)]
    [InlineData(-1, 1)]
    [InlineData(0, 2)]
    [InlineData(1, 2)]
    [InlineData(2, 2)]
    [InlineData(130, 2)]
    [InlineData(-1, 2)]
    [Theory]
    public void CleanupFailuresPreserveVerdictsAndExceptions(int outcome, int failureStage) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        using var cancellation = new CancellationTokenSource();
        Exception? proofException = outcome switch {
            130 => new OperationCanceledException(token: cancellation.Token),
            -1 => new InvalidOperationException(message: "runner failed"),
            _ => null,
        };
        Exception cleanupException = outcome switch {
            1 => new InvalidOperationException(message: "git did not start"),
            2 => new ArgumentException(message: "git launch arguments failed"),
            _ => new System.ComponentModel.Win32Exception(message: "git could not be launched"),
        };
        var cleanupCalls = 0;
        string? warning = null;
        Exception? observed = null;

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            BeforeBuild = (_, _) => {
                if (outcome == 130) { cancellation.Cancel(); }
                if (proofException is not null) { throw proofException; }
            },
            BuildResult = ((outcome == 2) ? new LawBuild(Errors: ["build failed"], Succeeded: false) : null),
            Report = content => new LawRun(
                Error: null,
                Failures: (((outcome == 1) || (content == "fixed")) ? [] : [new LawFailure(Message: "broken", Test: "Holds")]),
                Tests: ["Holds"]),
        };

        var (exitCode, _, error) = ConsoleCapture.RunSplit(run: () => {
            try {
                return LawProof.Prove(
                    cancellationToken: cancellation.Token,
                    cleanupGit: (repository, arguments) => {
                        ++cleanupCalls;
                        if (failureStage == 2) {
                            return new Puck.Hosting.ChildProcessResult(ExitCode: 1, Stderr: "cannot inspect registration", Stdout: "");
                        }
                        if ((failureStage == 0) || (cleanupCalls == 2)) { throw cleanupException; }
                        return CliGit.Run(arguments: arguments, repository: repository);
                    },
                    fix: new LawFix(Paths: [], Revision: "HEAD"),
                    law: Law,
                    project: null,
                    reportCleanupFailure: message => {
                        warning = message;
                        if (failureStage == 2) { throw new IOException(message: "stderr reader closed"); }
                        Console.Error.WriteLine(value: message);
                    },
                    repositoryRoot: checkout.Root,
                    runner: runner,
                    scratchRoot: scratch.RootPath,
                    lawTreesRoot: LawTreesRoot(checkout: checkout));
            } catch (Exception exception) {
                observed = exception;
                return -1;
            }
        });

        Assert.Same(actual: observed, expected: proofException);
        Assert.Equal(actual: exitCode, expected: ((proofException is null) ? outcome : -1));
        Assert.Equal(actual: cleanupCalls, expected: ((failureStage == 1) ? 2 : 1));
        Assert.NotNull(@object: warning);
        var tree = runner.Builds[0];

        Assert.Contains(actualString: warning, expectedSubstring: CliPaths.ToDisplay(fullPath: tree));
        Assert.Contains(actualString: warning, expectedSubstring: $"scratch directory: {CliPaths.ToDisplay(fullPath: Path.GetDirectoryName(path: tree)!)};");
        Assert.Contains(actualString: warning, expectedSubstring: ((failureStage == 1) ? "cannot remove proof worktree and its registration" : "cannot inspect proof worktree registration"));
        Assert.True(condition: Directory.Exists(path: tree));
        Assert.Contains(actualString: checkout.Git("worktree", "list", "--porcelain"), expectedSubstring: $"worktree {Puck.Abstractions.PuckPaths.Normalize(path: tree)}");
        if (failureStage != 2) {
            Assert.Contains(actualString: warning, expectedSubstring: cleanupException.Message);
            Assert.Contains(actualString: error, expectedSubstring: warning);
        }
    }
    [Fact]
    public void CleanupOfAnAlreadyRemovedScratchDirectoryStillRemovesItsRegistration() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");
        using var lease = HoldTree(checkout: checkout);
        string? proofScratch = null;

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            BeforeBuild = (tree, _) => proofScratch = Path.GetDirectoryName(path: tree),
            Report = content => {
                if (content == "fixed") {
                    Directory.Delete(path: proofScratch!, recursive: true);
                }
                return new LawRun(
                    Error: null,
                    Failures: ((content == "fixed") ? [] : [new LawFailure(Message: "broken", Test: "Holds")]),
                    Tests: ["Holds"]);
            },
        };

        var (exitCode, _, error) = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (exitCode == CliExit.Success), userMessage: error);
        Assert.DoesNotContain(actualString: error, expectedSubstring: "cannot remove proof scratch directory");
        Assert.DoesNotContain(actualString: error, expectedSubstring: "exit code still reports the proof");
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
}
