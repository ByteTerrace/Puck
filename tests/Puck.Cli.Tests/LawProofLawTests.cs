using Puck.Cli.Laws;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="LawProof"/> proves a law that fails without its fix and passes with it,
/// reports a law that passes with the fix withheld as unable to fail, refuses a build that fails, and does all of it in
/// a worktree of its own, leaving the caller's checkout, its worktree list and the scratch root as it found them. Each
/// law runs over a small git checkout and a runner that stands in for <c>dotnet build</c> and <c>dotnet test</c>: the
/// law passes only when the fix's file reads <c>fixed</c>, and the build fails on a file that reads
/// <c>unbuildable</c>.</summary>
public sealed class LawProofLawTests {
    private const string FixPath = "src/Lib/Fix.cs";
    private const string Law = "FixLawTests.Holds";
    private const string Project = "tests/Lib.Tests/Lib.Tests.csproj";

    private sealed class FakeRunner : ILawRunner {
        public List<string> Builds { get; } = [];
        public List<string> Runs { get; } = [];

        public LawBuild Build(string tree, string project) {
            Builds.Add(item: tree);

            return ((File.ReadAllText(path: Path.Combine(path1: tree, path2: FixPath)) == "unbuildable")
                ? new LawBuild(Errors: [$"{tree}/{FixPath}(1,1): error CS0000: unbuildable"], Succeeded: false)
                : new LawBuild(Errors: [], Succeeded: true));
        }
        public LawRun Run(string tree, string project, string law, string results) {
            Assert.Equal(actual: project, expected: Project);
            Runs.Add(item: tree);

            var content = File.ReadAllText(path: Path.Combine(path1: tree, path2: FixPath));

            return new LawRun(
                Error: null,
                Failures: ((content == "fixed")
                    ? []
                    : [new LawFailure(Message: $"Assert.Equal() Failure: {content}", Test: $"Lib.Tests.{law}")]),
                Total: 1
            );
        }
    }

    // A checkout whose law project declares FixLawTests and whose fix file starts as `initial`.
    private static GitScratchCheckout Checkout(string initial) {
        var checkout = new GitScratchCheckout();

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
        scratchRoot: scratch.RootPath
    ));
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
}
