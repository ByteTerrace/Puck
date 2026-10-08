using Puck.Testing;
using Xunit;

namespace Puck.Cli.Laws.Tests;

/// <summary>The proof's persistent tree: its lease and its incremental reuse, over the shared fixtures of
/// <see cref="LawProofLaws"/>.</summary>
public sealed class LawProofTreeLawTests : LawProofLaws {
    // The clone's own build writes paths deep under the repository's directory, past Windows' 260-character limit when
    // the name is long; the name is a short key of the common git directory, the same for every spelling Windows folds.
    [Fact]
    public void ARepositorysProofTreeIsNamedByAShortKeyOfItsCommonGitDirectory() {
        var root = Path.Combine(path1: Path.GetTempPath(), path2: "law-trees");
        var directory = LawProofTree.DirectoryFor(root: root, source: Path.Combine(path1: Path.GetTempPath(), path2: "Repository", path3: ".git"));
        var name = Path.GetFileName(path: directory);

        Assert.Equal(expected: Path.GetFullPath(path: root), actual: Path.GetDirectoryName(path: Path.GetFullPath(path: directory)));
        // Sixteen hexadecimal digits: 64 bits of the key, with room left for the clone's deepest build path.
        Assert.Equal(expected: 16, actual: name.Length);
        Assert.All(collection: name, action: static digit => Assert.True(condition: (char.IsAsciiDigit(c: digit) || ((digit >= 'a') && (digit <= 'f')))));
        Assert.NotEqual(expected: directory, actual: LawProofTree.DirectoryFor(root: root, source: Path.Combine(path1: Path.GetTempPath(), path2: "Other", path3: ".git")));
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void AProofBuildUsesTheSharedShaderCacheAndWithheldShaderContentStillCompiles(bool withholdShader) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-shader-proof-");
        using var cache = new TemporaryDirectory(prefix: "puck-laws-shader-cache-");
        using var shaders = new ShaderBuildFixture(root: Path.Combine(path1: checkout.Root, path2: "src/Lib"));
        const string Source = "src/Lib/Assets/Shaders/a.comp.hlsl";

        checkout.Write(name: ".gitignore", text: "bin/\nobj/\n*.spv\n*.dxil\n*.hash\ncompiles.txt\nstarted/\n");
        checkout.Write(name: "src/Lib/Lib.csproj", text: "<Project />\n");
        checkout.Write(name: Source, text: "original shader");
        shaders.ShaderProject(body: """
            <ItemGroup><ComputeShaderSource Include="Assets/Shaders/*.comp.hlsl" /></ItemGroup>
            <Target Name="ResolveProjectReferences" />
            """);
        _ = checkout.Commit(message: "lib: shader source and build");
        checkout.Write(name: FixPath, text: "fixed");
        if (withholdShader) { checkout.Write(name: Source, text: "fixed shader"); }
        var fix = checkout.Commit(message: "lib: fix");
        string[] properties = [$"PuckShaderCacheDirectory={cache.RootPath}", $"DxcCommand={shaders.CompilerPath}"];

        shaders.RequireSuccess(run: shaders.Run(properties: properties, target: "Build"));
        Assert.Equal(expected: 1, actual: shaders.Compiles());
        shaders.Write(path: "bin/managed.dll", text: "never transfer managed output");
        shaders.Write(path: "obj/managed.cache", text: "never transfer managed intermediate");
        var originalBytes = File.ReadAllBytes(path: shaders.PathOf(path: "Assets/Shaders/a.comp.spv"));
        var builds = 0;
        var runner = new FakeRunner {
            BeforeBuild = (tree, _) => {
                using var proof = new ShaderBuildFixture(root: Path.Combine(path1: tree, path2: "src/Lib"));

                if (++builds == 1) {
                    Assert.False(condition: File.Exists(path: proof.PathOf(path: "Assets/Shaders/a.comp.spv")), userMessage: "The proof copied ignored shader output instead of obtaining it through its build.");
                }
                Assert.False(condition: File.Exists(path: proof.PathOf(path: "bin/managed.dll")));
                Assert.False(condition: File.Exists(path: proof.PathOf(path: "obj/managed.cache")));
                // The real target and generator run in the proof clone. The isolated shared directory replaces only
                // the per-user cache location; no bytecode or sidecar is copied between checkout and clone.
                var build = proof.Run(properties: properties, target: "Build");

                proof.RequireSuccess(run: build);
                Assert.Equal(expected: (withholdShader ? 1 : 0), actual: proof.Compiles());
                if (builds == 2) {
                    Assert.Equal(expected: originalBytes, actual: File.ReadAllBytes(path: proof.PathOf(path: "Assets/Shaders/a.comp.spv")));
                    if (withholdShader) {
                        Assert.Contains(expectedSubstring: "1 published from the cache, 0 compiled", actualString: build.Stdout);
                    }
                }
            },
        };

        var result = Prove(checkout: checkout, fix: new LawFix(Paths: (withholdShader ? [FixPath, Source] : [FixPath]), Revision: fix), runner: runner, scratch: scratch);

        Assert.True(condition: (result.ExitCode == CliExit.Success), userMessage: result.Error);
        Assert.Equal(actual: builds, expected: 2);
        Assert.Equal(expected: 1, actual: shaders.Compiles());
        Assert.Equal(expected: originalBytes, actual: File.ReadAllBytes(path: shaders.PathOf(path: "Assets/Shaders/a.comp.spv")));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ATrackedLinkIsRefusedEvenWhenGitChecksItOutAsAnOrdinaryFile() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        var fix = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner();
        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: fix), runner: runner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);
        Assert.Equal(expected: 0, actual: CliGit.Run(repository: runner.Builds[0], arguments: ["config", "core.symlinks", "false"]).ExitCode);
        _ = checkout.Git("config", "core.symlinks", "false");
        checkout.Write(name: "src/Lib/Link.cs", text: "Fix.cs");
        var blob = checkout.Git("hash-object", "-w", "src/Lib/Link.cs").Trim();

        _ = checkout.Git("update-index", "--add", "--cacheinfo", $"120000,{blob},src/Lib/Link.cs");
        _ = checkout.Git("-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "--message", "lib: tracked link");

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: fix), runner: runner, scratch: scratch);

        Assert.Equal(actual: second.ExitCode, expected: CliExit.Refused);
        Assert.Contains(actualString: second.Error, expectedSubstring: "link in the proven tree");
        Assert.Equal(expected: 2, actual: runner.Builds.Count);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(130)]
    [InlineData(-1)]
    [Theory]
    public void EveryOutcomeKeepsThePersistentTreeAndReleasesItsLease(int outcome) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        Exception? failure = outcome switch {
            130 => new OperationCanceledException(message: "cancelled build"),
            -1 => new InvalidOperationException(message: "runner failed"),
            _ => null,
        };
        var runner = new FakeRunner {
            BeforeBuild = (_, _) => { if (failure is not null) { throw failure; } },
            BuildResult = ((outcome == 2) ? new LawBuild(Succeeded: false, Errors: ["build failed"]) : null),
            Report = content => new LawRun(Tests: ["Holds"], Failures: (((outcome == 1) || (content == "fixed")) ? [] : [new LawFailure(Message: "broken", Test: "Holds")]), Error: null),
        };
        var exitCode = -1;
        var exception = Record.Exception(testCode: () => exitCode = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch).ExitCode);

        Assert.Same(actual: exception, expected: failure);
        Assert.Equal(actual: exitCode, expected: ((failure is null) ? outcome : -1));
        Assert.True(condition: Directory.Exists(path: runner.Builds[0]));
        using var lease = HoldTree(checkout: checkout);

        Assert.Equal(expected: lease.Tree, actual: runner.Builds[0]);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void AReusedCloneNeverRunsItsOwnCheckoutHook() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner();
        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);
        var tree = runner.Builds[0];
        var hooks = Path.Combine(path1: tree, path2: ".git/proof-hooks");

        _ = Directory.CreateDirectory(path: hooks);
        var hook = Path.Combine(path1: hooks, path2: "post-checkout");

        File.WriteAllText(contents: "#!/bin/sh\nprintf touched > \"$(git rev-parse --git-common-dir)/hook-ran\"\n", path: hook);
        if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(mode: UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, path: hook); }
        Assert.Equal(expected: 0, actual: CliGit.Run(repository: tree, arguments: ["config", "core.hooksPath", hooks]).ExitCode);

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (second.ExitCode == CliExit.Success), userMessage: second.Error);
        Assert.False(condition: File.Exists(path: Path.Combine(path1: tree, path2: ".git/hook-ran")));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void ASecondProofReusesAnUnregisteredCloneAndItsOwnOutputs() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner();

        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);
        var tree = runner.Builds[0];
        var output = Path.Combine(path1: tree, path2: "src/Lib/obj/proof-output");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: output)!);
        File.WriteAllText(contents: "produced by this clone", path: output);
        var unchanged = Path.Combine(path1: tree, path2: "src/Lib/Other.cs");
        var written = File.GetLastWriteTimeUtc(path: unchanged);

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (second.ExitCode == CliExit.Success), userMessage: second.Error);
        Assert.Equal(expected: 4, actual: runner.Builds.Count);
        Assert.All(collection: runner.Builds, action: built => Assert.Equal(actual: built, expected: tree));
        Assert.StartsWith(expectedStartString: LawTreesRoot(checkout: checkout), actualString: tree);
        Assert.Contains(actualString: second.Error, expectedSubstring: "reusing persistent proof tree");
        Assert.Equal(expected: "produced by this clone", actual: File.ReadAllText(path: output));
        Assert.Equal(expected: written, actual: File.GetLastWriteTimeUtc(path: unchanged));
        Assert.True(condition: Directory.Exists(path: Path.Combine(path1: tree, path2: ".git")));
        Assert.DoesNotContain(expectedSubstring: Puck.Abstractions.PuckPaths.Normalize(path: tree), actualString: checkout.Git("worktree", "list", "--porcelain"));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ASecondProofRefreshesChangedSourcesAndRemovesUntrackedStrays(bool commitChange) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        var fix = checkout.Commit(message: "lib: fix");
        var firstRunner = new FakeRunner();
        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: fix), runner: firstRunner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);
        var stray = Path.Combine(path1: firstRunner.Builds[0], path2: "src/Lib/Stale.cs");

        File.WriteAllText(contents: "stale source", path: stray);
        checkout.Write(name: "src/Lib/Other.cs", text: "new source");
        if (commitChange) { _ = checkout.Commit(message: "lib: another edit"); }
        var runner = new FakeRunner {
            BeforeBuild = (tree, _) => {
                Assert.Equal(expected: "new source", actual: File.ReadAllText(path: Path.Combine(path1: tree, path2: "src/Lib/Other.cs")));
                Assert.False(condition: File.Exists(path: stray));
            },
        };

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: fix), runner: runner, scratch: scratch);

        Assert.True(condition: (second.ExitCode == CliExit.Success), userMessage: second.Error);
        Assert.Equal(expected: firstRunner.Builds[0], actual: runner.Builds[0]);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: (commitChange ? string.Empty : " M src/Lib/Other.cs\n"));
    }
    [Fact]
    public void AConcurrentProofUsesAColdScratchWorktreeWithoutTakingTheLease() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        using var lease = HoldTree(checkout: checkout);
        var runner = new FakeRunner();

        var result = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (result.ExitCode == CliExit.Success), userMessage: result.Error);
        Assert.StartsWith(expectedStartString: scratch.RootPath, actualString: runner.Builds[0]);
        Assert.Contains(actualString: result.Error, expectedSubstring: "falling back to a fresh scratch worktree (cold)");
        Assert.Null(@object: LawProofTree.TryAcquire(root: LawTreesRoot(checkout: checkout), repository: checkout.Root, reason: out _));
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [InlineData("index")]
    [InlineData("origin")]
    [InlineData("missing")]
    [Theory]
    public void ABadPersistentTreeIsReclonedAndTheProofStillConcludes(string damage) {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner();
        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);
        var tree = runner.Builds[0];
        var marker = Path.Combine(path1: tree, path2: "obj/old-output");

        _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: marker)!);
        File.WriteAllText(contents: "discard this cache", path: marker);
        if (damage == "index") { File.WriteAllText(path: Path.Combine(path1: tree, path2: ".git/index"), contents: "corrupt index"); } else if (damage == "origin") { Assert.Equal(expected: 0, actual: CliGit.Run(repository: tree, arguments: ["config", "remote.origin.url", scratch.RootPath]).ExitCode); } else { CliTreeFiles.DeleteTree(path: tree); }

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (second.ExitCode == CliExit.Success), userMessage: second.Error);
        Assert.Contains(actualString: second.Error, expectedSubstring: "rebuilding persistent proof tree cold");
        Assert.DoesNotContain(actualString: second.Error, expectedSubstring: "falling back");
        Assert.False(condition: File.Exists(path: marker));
        Assert.Equal(expected: tree, actual: runner.Builds[2]);
        AssertNothingLeftBehind(checkout: checkout, scratch: scratch, status: string.Empty);
    }
    [Fact]
    public void EachSideReportsBuildWorkOnceForAllSelectedTests() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var runner = new FakeRunner {
            BuildResult = new LawBuild(Succeeded: true, Errors: [], Counts: new LawBuildCounts(Compiled: 2, Targets: 101, UpToDate: 7)),
            Report = content => new LawRun(Error: null, Tests: ["First", "Second"], Failures: ((content == "fixed") ? [] : [new LawFailure(Message: "broken", Test: "First")])),
        };

        var result = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (result.ExitCode == CliExit.Success), userMessage: result.Error);
        Assert.Equal(expected: 2, actual: runner.Builds.Count);
        Assert.Equal(expected: 2, actual: runner.Runs.Count);
        var counts = result.Error.Split(separator: '\n').Where(predicate: static line => line.StartsWith(comparisonType: StringComparison.Ordinal, value: "laws prove: built ")).Select(selector: static line => line.TrimEnd()).ToArray();

        Assert.Equal(actual: counts, expected: ["laws prove: built with the fix withheld: 2 project(s) compiled, 7 up to date, 101 target(s)", "laws prove: built with the fix restored: 2 project(s) compiled, 7 up to date, 101 target(s)"]);
    }
    [Fact]
    public void RestoredFilesAreNewerThanEvenFutureDatedOutputs() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        checkout.Write(name: FixPath, text: "fixed");
        _ = checkout.Commit(message: "lib: fix");
        var builds = 0;
        var future = DateTime.UtcNow.AddHours(value: 1);
        var runner = new FakeRunner {
            BeforeBuild = (tree, cancellationToken) => {
                var output = Path.Combine(path1: tree, path2: "bin/output");

                if (++builds == 1) {
                    _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: output)!);
                    File.WriteAllText(contents: "withheld binary", path: output);
                    File.SetLastWriteTimeUtc(lastWriteTimeUtc: future, path: output);
                } else {
                    Assert.True(condition: (File.GetLastWriteTimeUtc(path: Path.Combine(path1: tree, path2: FixPath)) > File.GetLastWriteTimeUtc(path: output)));
                }
            },
        };

        var result = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: "HEAD"), runner: runner, scratch: scratch);

        Assert.True(condition: (result.ExitCode == CliExit.Success), userMessage: result.Error);
        Assert.Equal(actual: builds, expected: 2);
    }
    [Fact]
    public void EveryWorktreeOfOneRepositorySharesOnePersistentTree() {
        using var checkout = Checkout(initial: "broken");
        using var other = new TemporaryDirectory(prefix: "puck-laws-worktree-law-");
        var otherWorktree = other.PathOf(name: "worktree");

        _ = checkout.Git("worktree", "add", "--detach", "--quiet", otherWorktree, "HEAD");

        try {
            string first;

            using (var lease = LawProofTree.TryAcquire(reason: out var reason, repository: checkout.Root, root: LawTreesRoot(checkout: checkout))) {
                Assert.True(condition: (lease is not null), userMessage: reason);
                first = lease.Tree;

                // The other worktree resolves the same lock, so it cannot lease while this one holds it.
                Assert.Null(@object: LawProofTree.TryAcquire(reason: out _, repository: otherWorktree, root: LawTreesRoot(checkout: checkout)));
            }

            using var second = LawProofTree.TryAcquire(reason: out var otherReason, repository: otherWorktree, root: LawTreesRoot(checkout: checkout));

            Assert.True(condition: (second is not null), userMessage: otherReason);
            Assert.Equal(actual: second.Tree, expected: first);
        } finally {
            _ = checkout.Git("worktree", "remove", "--force", otherWorktree);
        }
    }
    [Fact]
    public void AReusedCloneIsRecognizedWhenGitSpellsItsPathDifferently() {
        using var checkout = Checkout(initial: "broken");
        var head = checkout.Git("rev-parse", "HEAD").Trim();
        string marker;

        using (var first = LawProofTree.TryAcquire(reason: out var reason, repository: checkout.Root, root: LawTreesRoot(checkout: checkout))) {
            Assert.True(condition: (first is not null), userMessage: reason);
            Assert.True(condition: first.TryPrepare(git: static (repository, arguments) => CliGit.Run(arguments: arguments, repository: repository), head: head, reason: out reason), userMessage: reason);
            marker = Path.Combine(path1: first.Tree, path2: "obj/marker");
            _ = Directory.CreateDirectory(path: Path.GetDirectoryName(path: marker)!);
            File.WriteAllText(contents: "kept", path: marker);
        }

        // A packaged host redirects the per-user directory, so git names the clone's top level under another path.
        using var second = LawProofTree.TryAcquire(reason: out var secondReason, repository: checkout.Root, root: LawTreesRoot(checkout: checkout));

        Assert.True(condition: (second is not null), userMessage: secondReason);
        Assert.True(condition: second.TryPrepare(
            git: (repository, arguments) => (arguments.SequenceEqual(second: ["rev-parse", "--show-toplevel"])
                ? new Puck.Hosting.ChildProcessResult(ExitCode: 0, Stderr: string.Empty, Stdout: "Z:/Packages/Host/LocalCache/Local/Puck/law-trees/tree\n")
                : CliGit.Run(arguments: arguments, repository: repository)),
            head: head,
            reason: out secondReason
        ), userMessage: secondReason);
        Assert.True(condition: File.Exists(path: marker), userMessage: "the clone was rebuilt instead of reused");
    }
    [Fact]
    public void AProofLeavesTheCloneIndexAtItsHeadSoTheNextProofRewritesNothingUnchanged() {
        using var checkout = Checkout(initial: "broken");
        using var scratch = new TemporaryDirectory(prefix: "puck-laws-law-");

        // One fix commit changes two sources; the first proof withholds both through a three-way reverse.
        checkout.Write(name: FixPath, text: "fixed");
        checkout.Write(name: "src/Lib/Other.cs", text: "other, fixed\n");
        var fix = checkout.Commit(message: "lib: fix both");
        var firstRunner = new FakeRunner();
        var first = Prove(checkout: checkout, fix: new LawFix(Paths: [], Revision: fix), runner: firstRunner, scratch: scratch);

        Assert.True(condition: (first.ExitCode == CliExit.Success), userMessage: first.Error);

        // Mark the restored file, then prove again withholding only the fix file: the other file's content is
        // unchanged, so nothing may rewrite it (a rewrite gives it a fresh time, and everything built from it recompiles).
        var other = Path.Combine(path1: firstRunner.Builds[0], path2: "src/Lib/Other.cs");
        var marked = new DateTime(day: 1, hour: 0, kind: DateTimeKind.Utc, minute: 0, month: 1, second: 0, year: 2001);

        var content = File.ReadAllText(path: other);

        File.SetLastWriteTimeUtc(lastWriteTimeUtc: marked, path: other);

        var second = Prove(checkout: checkout, fix: new LawFix(Paths: [FixPath], Revision: fix), runner: new FakeRunner(), scratch: scratch);

        Assert.True(condition: (second.ExitCode == CliExit.Success), userMessage: second.Error);
        Assert.Equal(actual: File.ReadAllText(path: other), expected: content);
        Assert.Equal(actual: File.GetLastWriteTimeUtc(path: other), expected: marked);
    }
}
