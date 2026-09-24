using System.Diagnostics;
using Puck.Testing;
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
            Git("init", "--quiet", "--initial-branch=main");
            Git("add", "--all");
            Commit(message: "initial");
        }

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
    // A builder that writes a stand-in artifact after a pause long enough for a concurrent resolution to arrive while
    // it is still building, and counts how many times it ran.
    private sealed class CountingBuilder(TimeSpan pause) {
        private int m_builds;

        public int Builds => Volatile.Read(location: ref m_builds);

        public bool Build(string outputDirectory, TimeSpan timeout, out CliProcessResult? build, out string error) {
            _ = Interlocked.Increment(location: ref m_builds);
            Thread.Sleep(timeout: pause);
            File.WriteAllText(
                contents: Guid.NewGuid().ToString(format: "N"),
                path: Path.Combine(
                    path1: outputDirectory,
                    path2: WorldArtifactBuild.ArtifactName
                )
            );
            build = null;
            error = string.Empty;

            return true;
        }
    }

    private static WorldArtifact Resolve(Checkout checkout, WorldArtifactStore store, CountingBuilder builder) {
        Assert.True(
            condition: WorldArtifactBuild.TryResolve(
            artifact: out var artifact,
            build: out _,
            builder: builder.Build,
            error: out var error,
            repositoryRoot: checkout.Root,
            store: store,
            timeout: TimeSpan.FromMinutes(value: 2),
            verb: "law"
        ),
            userMessage: error
        );

        return artifact;
    }

    [Fact]
    public void TwoResolutionsOfAnUnchangedTreeProduceOneBuild() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var builder = new CountingBuilder(pause: TimeSpan.Zero);

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
        checkout.Commit(message: "add a source");
        Assert.NotEqual(
            actual: checkout.Key(),
            expected: clean
        );
        checkout.Write(
            name: "Directory.Build.props",
            text: "<Project></Project>\n"
        );
        Assert.NotEqual(
            actual: checkout.Key(),
            expected: untracked
        );
    }
    [Fact]
    public void ConcurrentResolutionsOfOneSourceStateShareOneBuild() {
        using var checkout = new Checkout();
        var store = checkout.Store;
        var builder = new CountingBuilder(pause: TimeSpan.FromMilliseconds(value: 500));
        const int Resolvers = 6;
        var artifacts = new WorldArtifact[Resolvers];
        using var start = new Barrier(participantCount: Resolvers);

        var threads = Enumerable.Range(
            count: Resolvers,
            start: 0
        ).Select(selector: index => new Thread(start: () => {
            start.SignalAndWait();
            artifacts[index] = Resolve(
                builder: builder,
                checkout: checkout,
                store: store
            );
        })).ToArray();

        foreach (var thread in threads) {
            thread.Start();
        }
        foreach (var thread in threads) {
            thread.Join();
        }

        try {
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
        var builder = new CountingBuilder(pause: TimeSpan.Zero);
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
                builder: new CountingBuilder(pause: TimeSpan.Zero),
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
}
