using System.Diagnostics;
using Puck.Testing;
using Puck.Cli.Canary;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <see cref="WorldArtifactBuild"/> builds the World once per source state and every
/// later resolution of that state takes the kept build. Each law runs over a small git checkout of its own, shaped like
/// the real one (a World project referencing a library, root build properties, a documentation directory outside the
/// closure), and a builder that counts its calls in place of <c>dotnet build</c>, so the laws count builds rather than
/// time them.</summary>
public sealed class WorldArtifactBuildLawTests {
    private const string LibrarySource = "src/Puck.Library/Library.cs";

    private sealed class Checkout : IDisposable {
        private readonly TemporaryDirectory m_directory = new();

        public Checkout() {
            Write(
                name: "Directory.Build.props",
                text: "<Project />\n"
            );
            Write(
                name: WorldArtifactClosure.WorldProject,
                text: """
                <Project Sdk="Microsoft.NET.Sdk">
                  <ItemGroup>
                    <ProjectReference Include="..\Puck.Library\Puck.Library.csproj" />
                  </ItemGroup>
                </Project>

                """
            );
            Write(
                name: "src/Puck.World/Program.cs",
                text: "return 0;\n"
            );
            Write(
                name: "src/Puck.Library/Puck.Library.csproj",
                text: "<Project Sdk=\"Microsoft.NET.Sdk\" />\n"
            );
            Write(
                name: LibrarySource,
                text: "namespace Library;\n"
            );
            Write(
                name: "src/Puck.Unrelated/Puck.Unrelated.csproj",
                text: "<Project Sdk=\"Microsoft.NET.Sdk\" />\n"
            );
            Write(
                name: "docs/guide.md",
                text: "# Guide\n"
            );
            GitScratchCheckout.Initialize(repository: m_directory.RootPath);
            Git("add", "--all");
            Commit(message: "initial");
        }

        public string LogDirectory => m_directory.PathOf(name: "run");
        public string Root => m_directory.RootPath;
        public WorldArtifactStore Store => new(root: m_directory.PathOf(name: "store"));

        public void Commit(string message) =>
            Git("-c", "user.name=law", "-c", "user.email=law@example.invalid", "-c", "commit.gpgsign=false", "commit", "--quiet", "--all", "--message", message);
        public void Dispose() {
            // Git writes its object files read-only, which a recursive delete refuses on Windows.
            foreach (var file in Directory.EnumerateFiles(
                path: Root,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                File.SetAttributes(
                    fileAttributes: FileAttributes.Normal,
                    path: file
                );
            }

            m_directory.Dispose();
        }
        public void Git(params string[] arguments) {
            var result = CliGit.Run(
                arguments: [.. arguments],
                repository: Root
            );

            Assert.True(
                condition: (result.ExitCode == 0),
                userMessage: $"git {string.Join(separator: ' ', values: arguments)} exited {result.ExitCode}: {result.Stderr}"
            );
        }
        public string Key() {
            var (roots, _) = WorldArtifactClosure.Walk(repositoryRoot: Root);

            Assert.True(
                condition: WorldArtifactKey.TryCompute(
                buildArguments: ["build"],
                key: out var key,
                reason: out var reason,
                repositoryRoot: Root,
                roots: roots
            ),
                userMessage: reason
            );

            return key;
        }
        public void Write(string name, string text) =>
            m_directory.WriteText(
                name: name,
                text: text
            );
    }
    private sealed class CountingBuilder(Action? entered = null) {
        private int m_builds;

        public int Builds => Volatile.Read(location: ref m_builds);

        public CliProcessResult Build(IReadOnlyList<string> arguments, TimeSpan timeout) {
            var outputDirectory = arguments[^1];

            _ = Interlocked.Increment(location: ref m_builds);
            entered?.Invoke();
            File.WriteAllText(
                contents: Guid.NewGuid().ToString(format: "N"),
                path: Path.Combine(
                    path1: outputDirectory,
                    path2: WorldArtifactBuild.ArtifactName
                )
            );
            return new CliProcessResult(ExitCode: 0, OutputLines: [], Stderr: string.Empty, Stdout: "Build succeeded.", TimedOut: false);
        }
    }

    private static WorldArtifact Resolve(Checkout checkout, WorldArtifactStore store, CountingBuilder builder, Action? waiting = null) {
        Assert.True(
            condition: WorldArtifactBuild.TryResolve(
            artifact: out var artifact,
            builder: builder.Build,
            error: out var error,
            logDirectory: checkout.LogDirectory,
            repositoryRoot: checkout.Root,
            store: store,
            timeout: TestLiveness.Bound,
            verb: "law",
            waiting: waiting
        ),
            userMessage: error
        );

        return artifact;
    }

    [Fact]
    public void TwoResolutionsOfAnUnchangedTreeProduceOneBuild() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var builder = new CountingBuilder();

        using var first = Resolve(
            builder: builder,
            checkout: checkout,
            store: store
        );
        using var second = Resolve(
            builder: builder,
            checkout: checkout,
            store: store
        );

        Assert.Equal(
            actual: builder.Builds,
            expected: 1
        );
        Assert.False(condition: first.Reused);
        Assert.True(condition: second.Reused);
        Assert.Equal(
            actual: second.Path,
            expected: first.Path
        );
        Assert.Equal(
            actual: File.ReadAllText(path: second.Path),
            expected: File.ReadAllText(path: first.Path)
        );
    }
    [InlineData("world")]
    [InlineData("stub")]
    [Theory]
    public void ACanaryBuildDirectoryExistsOnlyWhenABuildWritesItsLog(string id) {
        using var checkout = new Checkout();
        var builder = new CountingBuilder();
        var builtLogDirectory = CanaryCommand.BuildRunDirectory(id: id);
        var reusedLogDirectory = CanaryCommand.BuildRunDirectory(id: id);
        var namedLogDirectory = CanaryCommand.BuildRunDirectory(id: id);

        try {
            Assert.False(condition: Directory.Exists(path: builtLogDirectory));
            Assert.True(condition: WorldArtifactBuild.TryResolve(
                artifact: out var built,
                builder: builder.Build,
                error: out var error,
                logDirectory: builtLogDirectory,
                repositoryRoot: checkout.Root,
                store: checkout.Store,
                timeout: TestLiveness.Bound,
                verb: "law"
            ), userMessage: error);
            using var buildLease = built;

            Assert.True(condition: File.Exists(path: Path.Combine(path1: builtLogDirectory, path2: "Puck.World.build.log")));

            Assert.True(condition: WorldArtifactBuild.TryResolve(
                artifact: out var reused,
                builder: builder.Build,
                error: out error,
                logDirectory: reusedLogDirectory,
                repositoryRoot: checkout.Root,
                store: checkout.Store,
                timeout: TestLiveness.Bound,
                verb: "law"
            ), userMessage: error);
            using var reusedLease = reused;

            Assert.True(condition: reused.Reused);
            Assert.False(condition: Directory.Exists(path: reusedLogDirectory));

            Assert.True(condition: WorldArtifactBuild.TryResolveNamed(
                error: out error,
                lease: out var namedLease,
                logDirectory: namedLogDirectory,
                named: built.Path,
                path: out var namedPath,
                repositoryRoot: checkout.Root,
                timeout: TestLiveness.Bound,
                verb: "law"
            ), userMessage: error);
            Assert.Null(@object: namedLease);
            Assert.Equal(expected: built.Path, actual: namedPath);
            Assert.False(condition: Directory.Exists(path: namedLogDirectory));
            Assert.Equal(expected: 1, actual: builder.Builds);
        } finally {
            _ = RunDirectory.TryDelete(path: builtLogDirectory);
            _ = RunDirectory.TryDelete(path: reusedLogDirectory);
            _ = RunDirectory.TryDelete(path: namedLogDirectory);
        }
    }
    [Fact]
    public void AOneByteChangeUnderTheClosureProducesANewKeyAndAChangeOutsideItDoesNot() {
        using var checkout = new Checkout();
        var clean = checkout.Key();

        Assert.Equal(
            actual: checkout.Key(),
            expected: clean
        );

        // One byte of a referenced project's source, uncommitted.
        checkout.Write(
            name: LibrarySource,
            text: "namespace Librarz;\n"
        );

        var edited = checkout.Key();

        Assert.NotEqual(
            actual: edited,
            expected: clean
        );

        checkout.Write(
            name: LibrarySource,
            text: "namespace Library;\n"
        );
        Assert.Equal(
            actual: checkout.Key(),
            expected: clean
        );

        // Documentation, and a project the World does not reference, are outside the closure.
        checkout.Write(
            name: "docs/guide.md",
            text: "# Guidf\n"
        );
        checkout.Write(
            name: "src/Puck.Unrelated/Unrelated.cs",
            text: "namespace Unrelated;\n"
        );
        Assert.Equal(
            actual: checkout.Key(),
            expected: clean
        );

        // An untracked file inside a closure project is an input, and so is a root-level file.
        checkout.Write(
            name: "src/Puck.World/Added.cs",
            text: "namespace World;\n"
        );

        var untracked = checkout.Key();

        Assert.NotEqual(
            actual: untracked,
            expected: clean
        );

        // Committing the same bytes moves them from the working-tree half of the key to the committed half.
        checkout.Git("add", "src/Puck.World/Added.cs");
        checkout.Commit(message: "add a source");
        var committed = checkout.Key();

        Assert.NotEqual(
            actual: committed,
            expected: clean
        );
        Assert.NotEqual(
            actual: committed,
            expected: untracked
        );
        checkout.Write(
            name: "Directory.Build.props",
            text: "<Project></Project>\n"
        );
        Assert.NotEqual(
            actual: checkout.Key(),
            expected: committed
        );
    }
    [Fact]
    public async Task ConcurrentResolutionsOfOneSourceStateShareOneBuild() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        const int Resolvers = 6;
        using var waiting = new CountdownEvent(initialCount: (Resolvers - 1));
        var builder = new CountingBuilder(entered: () => Assert.True(condition: waiting.Wait(timeout: TestLiveness.Bound), userMessage: "The other resolvers never contended on the in-flight build."));
        var artifacts = new WorldArtifact[Resolvers];
        using var start = new Barrier(participantCount: Resolvers);

        var tasks = Enumerable.Range(
            count: Resolvers,
            start: 0
        ).Select(selector: index => Task.Factory.StartNew(action: () => {
            start.SignalAndWait();
            artifacts[index] = Resolve(
                builder: builder,
                checkout: checkout,
                store: store,
                waiting: () => waiting.Signal()
            );
        }, cancellationToken: CancellationToken.None, creationOptions: TaskCreationOptions.LongRunning, scheduler: TaskScheduler.Default)).ToArray();

        try {
            await Task.WhenAll(tasks: tasks);
            Assert.Equal(
                actual: builder.Builds,
                expected: 1
            );
            Assert.Single(collection: artifacts.Select(selector: static artifact => artifact.Path).Distinct());
            Assert.Single(collection: artifacts, predicate: static artifact => !artifact.Reused);
        } finally {
            foreach (var artifact in artifacts) {
                artifact?.Dispose();
            }
        }
    }
    [Fact]
    public void APublishThatLosesTheRaceDiscardsItsOwnBuildAndTakesTheWinners() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var clock = Stopwatch.StartNew();
        var key = checkout.Key();
        var winner = store.CreateStaging(key: key);
        var loser = store.CreateStaging(key: key);

        File.WriteAllText(
            contents: "winner",
            path: Path.Combine(
                path1: winner,
                path2: WorldArtifactBuild.ArtifactName
            )
        );
        File.WriteAllText(
            contents: "loser",
            path: Path.Combine(
                path1: loser,
                path2: WorldArtifactBuild.ArtifactName
            )
        );

        using var first = store.Publish(
            artifactName: WorldArtifactBuild.ArtifactName,
            budget: TimeSpan.FromSeconds(value: 30),
            clock: clock,
            key: key,
            staging: winner
        );
        using var second = store.Publish(
            artifactName: WorldArtifactBuild.ArtifactName,
            budget: TimeSpan.FromSeconds(value: 30),
            clock: clock,
            key: key,
            staging: loser
        );

        Assert.NotNull(@object: first);
        Assert.NotNull(@object: second);
        Assert.Equal(
            actual: second.Path,
            expected: first.Path
        );
        Assert.Equal(
            actual: File.ReadAllText(path: second.Path),
            expected: "winner"
        );
        Assert.False(condition: Directory.Exists(path: loser));
    }
    [Fact]
    public void PruningKeepsTheMostRecentlyUsedBuildsAndEveryLeasedOne() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var builder = new CountingBuilder();
        var edits = (WorldArtifactStore.KeepCount + 2);
        WorldArtifact? held = null;

        try {
            for (var edit = 0; (edit < edits); edit++) {
                checkout.Write(
                    name: LibrarySource,
                    text: $"namespace Library{edit};\n"
                );

                var artifact = Resolve(
                    builder: builder,
                    checkout: checkout,
                    store: store
                );

                if (edit == 0) {
                    // The oldest build stays leased, as by a run whose legs are still going.
                    held = artifact;
                } else {
                    artifact.Dispose();
                }
            }

            var entries = Directory.EnumerateDirectories(path: store.Root).Select(selector: Path.GetFileName).ToArray();

            Assert.Equal(
                actual: builder.Builds,
                expected: edits
            );
            Assert.Equal(
                actual: entries.Length,
                expected: (WorldArtifactStore.KeepCount + 1)
            );
            Assert.True(condition: File.Exists(path: held!.Path));
        } finally {
            held?.Dispose();
        }
    }
    [Fact]
    public void LeftoversOfAKilledRunAreSweptOnlyOnceTheyAreSixHoursOld() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var old = DateTime.UtcNow.AddHours(value: -7);

        _ = Directory.CreateDirectory(path: store.Root);

        string Leftover(string name, bool aged, bool directory) {
            var path = Path.Combine(
                path1: store.Root,
                path2: name
            );

            if (directory) {
                _ = Directory.CreateDirectory(path: path);
                if (aged) {
                    Directory.SetLastWriteTimeUtc(
                        lastWriteTimeUtc: old,
                        path: path
                    );
                }
            } else {
                File.WriteAllText(
                    contents: string.Empty,
                    path: path
                );
                if (aged) {
                    File.SetLastWriteTimeUtc(
                        lastWriteTimeUtc: old,
                        path: path
                    );
                }
            }

            return path;
        }

        var agedBuilding = Leftover(aged: true, directory: true, name: "dead.building-0");
        var freshBuilding = Leftover(aged: false, directory: true, name: "live.building-0");
        var agedLock = Leftover(aged: true, directory: false, name: "dead.build.lock");
        var agedLease = Leftover(aged: true, directory: false, name: "dead.lease");
        var freshLock = Leftover(aged: false, directory: false, name: "live.build.lock");
        var heldLock = Leftover(aged: true, directory: false, name: "held.build.lock");

        using (var held = new FileStream(
            access: FileAccess.ReadWrite,
            mode: FileMode.Open,
            path: heldLock,
            share: FileShare.None
        )) {
            using var artifact = Resolve(
                builder: new CountingBuilder(),
                checkout: checkout,
                store: store
            );
        }

        Assert.False(condition: Directory.Exists(path: agedBuilding));
        Assert.False(condition: File.Exists(path: agedLock));
        Assert.False(condition: File.Exists(path: agedLease));
        Assert.True(condition: Directory.Exists(path: freshBuilding));
        Assert.True(condition: File.Exists(path: freshLock));
        Assert.True(condition: File.Exists(path: heldLock));
    }
    [Fact]
    public void TheRepositoryKeyIsStableAcrossResolutions() {
        var root = RepositoryPaths.RequireRoot();

        var (roots, projects) = WorldArtifactClosure.Walk(repositoryRoot: root);

        Assert.Contains(
            collection: projects,
            expected: WorldArtifactClosure.WorldProject
        );
        Assert.Contains(
            collection: projects,
            expected: "src/Puck.Cli/Puck.Cli.csproj"
        );
        Assert.DoesNotContain(
            collection: roots,
            expected: "docs"
        );
        Assert.True(condition: WorldArtifactKey.TryCompute(
            buildArguments: ["build"],
            key: out var first,
            reason: out var reason,
            repositoryRoot: root,
            roots: roots
        ), userMessage: reason);
        Assert.True(condition: WorldArtifactKey.TryCompute(
            buildArguments: ["build"],
            key: out var second,
            reason: out reason,
            repositoryRoot: root,
            roots: roots
        ), userMessage: reason);
        Assert.Equal(
            actual: second,
            expected: first
        );
    }
    [Fact]
    public void AFailedBuildsRefusalQuotesItsFirstErrorsAndNamesTheLogThatKeepsItsWholeOutput() {
        using var checkout = new Checkout();
        const string Error = @"C:\x\Library.cs(1,1): error CS1002: ; expected [C:\x\Puck.Library.csproj]";
        var stdout = $"  Determining projects to restore...\n  3>{Error}\n\nBuild FAILED.\n\n    {Error}\n    0 Warning(s)\n    1 Error(s)\n";

        Assert.False(condition: WorldArtifactBuild.TryResolve(
            artifact: out _,
            builder: (arguments, timeout) => new CliProcessResult(
                    ExitCode: 1,
                    OutputLines: [],
                    Stderr: "a line on standard error\n",
                    Stdout: stdout,
                    TimedOut: false
                ),
            error: out var refusal,
            logDirectory: checkout.LogDirectory,
            repositoryRoot: checkout.Root,
            store: checkout.Store,
            timeout: TestLiveness.Bound,
            verb: "law"
        ));

        var log = Path.Combine(
            path1: checkout.LogDirectory,
            path2: CliProjectBuild.LogName(project: WorldArtifactClosure.WorldProject)
        );

        Assert.StartsWith(
            actualString: refusal,
            expectedStartString: "the Puck.World build exited 1. First errors:"
        );
        Assert.Equal(
            actual: refusal.Split(separator: Error).Length,
            expected: 2
        );
        Assert.Contains(
            actualString: refusal,
            expectedSubstring: CliPaths.ToDisplay(fullPath: log)
        );
        Assert.True(
            condition: File.Exists(path: log),
            userMessage: $"{log} was not kept"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: log),
            expectedSubstring: "Determining projects to restore"
        );
        Assert.Contains(
            actualString: File.ReadAllText(path: log),
            expectedSubstring: "a line on standard error"
        );
    }
    [Fact]
    public void AFailedBuildQuotesOnlyErrorDiagnosticsAndCapsDistinctErrorsAtFive() {
        using var checkout = new Checkout();
        string[] diagnostics = [
            "C:/x/error/Library.cs(1,1): error CS1002: ; expected [C:/x/Library.csproj]",
            "MSBUILD : error MSB1009: Project file does not exist.",
            "CSC : error CS0006: Metadata file could not be found.",
            "error NETSDK1004: Assets file not found.",
            "error NU1101: Unable to find package Missing.",
        ];
        string[] noise = [
            "C:/x/Library.cs(1,1): warning CS1030: #warning: 'reported error: one' [C:/x/Library.csproj]",
            "C:/x/Library.cs(2,1): warning CS1030: #warning: 'reported error: two' [C:/x/Library.csproj]",
            "C:/x/Library.cs(3,1): warning CS1030: #warning: 'reported error: three' [C:/x/Library.csproj]",
            "C:/x/Library.cs(4,1): warning CS1030: #warning: 'reported error: four' [C:/x/Library.csproj]",
            "C:/x/Library.cs(5,1): warning CS1030: #warning: 'reported error: five' [C:/x/Library.csproj]",
            "warning NU1900: error: could not load vulnerability data.",
            "a message mentioning error: without a diagnostic",
            "C:/x/error/Library.cs -> C:/x/error/Library.dll",
            "    0 Error(s)",
        ];
        var stdout = string.Join(separator: "\r\n", values: ((string[])[
            " \t ", .. noise,
            .. diagnostics.Select(selector: static line => $"  12>{line}"),
            "Build FAILED.", .. diagnostics,
            "error MSB4018: This sixth error is kept only in the log.",
            " \t ",
        ]));
        const string Stderr = "error MSB9999: This seventh error is kept only in the log.\r\n";

        Assert.False(condition: WorldArtifactBuild.TryResolve(
            artifact: out _,
            builder: (arguments, timeout) => new CliProcessResult(
                    ExitCode: 1,
                    OutputLines: [],
                    Stderr: Stderr,
                    Stdout: stdout,
                    TimedOut: false
                ),
            error: out var refusal,
            logDirectory: checkout.LogDirectory,
            repositoryRoot: checkout.Root,
            store: checkout.Store,
            timeout: TestLiveness.Bound,
            verb: "law"
        ));

        var log = Path.Combine(path1: checkout.LogDirectory, path2: CliProjectBuild.LogName(project: WorldArtifactClosure.WorldProject));

        Assert.Equal(
            actual: refusal,
            expected: string.Join(separator: Environment.NewLine, values: ((string[])[
                "the Puck.World build exited 1. First errors:",
                .. diagnostics.Select(selector: static line => $"  {line}"),
                $"Its whole output is in {CliPaths.ToDisplay(fullPath: log)}.",
            ]))
        );
        Assert.Equal(
            actual: File.ReadAllText(path: log),
            expected: $"{stdout}{Environment.NewLine}--- stderr ---{Environment.NewLine}{Stderr}"
        );
    }
}
