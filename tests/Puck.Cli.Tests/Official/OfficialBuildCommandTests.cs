using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Puck.Assets.Documents;
using Puck.Cli.Official;
using Puck.Launcher.Release;
using Puck.World;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Decompiler;

using Xunit;

namespace Puck.Cli.Tests.Official;

/// <summary>Builds a real puck.official.manifest.v1 tree once, from this checkout's own worlds and the read-only browser-wasm
/// AppBundle in the current checkout, so every test in <see cref="OfficialBuildCommandTests"/> exercises the actual
/// verb end to end rather than a synthetic fixture.</summary>
public sealed class OfficialBuildFixture : IDisposable {
    // Build the browser in this checkout before running the official-content integration tests.
    public static string AppBundlePath {
        get {
            Assert.True(
                condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root),
                userMessage: "Cannot locate this checkout's Puck.slnx."
            );

            return Path.Combine(
                path1: root!,
                path2: "src/Puck.World.Browser/bin/Release/net10.0/browser-wasm/AppBundle"
            );
        }
    }
    public int ExitCode { get; }
    public string OutRoot { get; }
    public string StdErr { get; }
    public string StdOut { get; }

    public const string Channel = "dev";

    public OfficialBuildFixture() {
        Assert.True(
            condition: Directory.Exists(path: AppBundlePath),
            userMessage: (((string)$"the read-only AppBundle at {AppBundlePath} does not exist. Publish the browser first: dotnet publish src/Puck.World.Browser -c Release. CI's artifacts job always publishes it before ") +
                "any test project runs, so this failure means a local run reached this fixture without that step.")
        );

        OutRoot = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-official-tests-{Guid.NewGuid():n}"
        );
        (ExitCode, StdOut, StdErr) = RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--tree", OutRoot,
            "--channel", Channel,
            "--engine", AppBundlePath,
            "--allow-dirty",
        ]).Invoke());
    }

    internal static (int ExitCode, string StdOut, string StdErr) RunCapturingConsole(Func<int> run) =>
        ConsoleCapture.RunSplit(run: run);

    public void Dispose() {
        try {
            if (Directory.Exists(path: OutRoot)) {
                Directory.Delete(
                    path: OutRoot,
                    recursive: true
                );
            }
        } catch (IOException) {
            // Best-effort cleanup — a locked file from a still-draining stream never fails the suite.
        }
    }
}
/// <summary>Exercises <c>puck official build</c> and <c>puck official verify</c> end to end against a real tree, and
/// <see cref="OfficialBuildCommand.IsDirtyPorcelainOutput"/> as a pure unit in isolation. The tree state a build is
/// named by is held over checkouts of its own in <see cref="OfficialTreeStateLawTests"/>, never over this checkout's
/// shared, concurrently-mutated working tree.</summary>
public sealed class OfficialBuildCommandTests(OfficialBuildFixture fixture) : IClassFixture<OfficialBuildFixture> {
    private string ChannelManifestPath => Path.Combine(
        path1: fixture.OutRoot,
        path2: OfficialBuildFixture.Channel,
        path3: "manifest.json"
    );
    private static string WorldsDirectory {
        get {
            Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var root));

            return Path.Combine(
                path1: root!,
                path2: "src/Puck.World/Assets/worlds"
            );
        }
    }

    private OfficialManifest ReadManifest() => JsonSerializer.Deserialize<OfficialManifest>(
        options: DocumentJsonOptions.Shared,
        utf8Json: File.ReadAllBytes(path: ChannelManifestPath)
    )!;
    private byte[] ReadObject(string path) => File.ReadAllBytes(path: Path.Combine(
        path1: fixture.OutRoot,
        path2: path
    ));
    private static string WorkspaceName(string file) => Path.GetRelativePath(
        path: file,
        relativeTo: WorldsDirectory
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    // Runs verify over a copy of the tree whose channel and builds manifests both carry `tamper`'s rewrite, so the
    // only discrepancy verify can find is the one the rewrite introduced.
    private (int ExitCode, string StdErr) VerifyWithTamperedManifest(Func<JsonObject, JsonObject> tamper) {
        var original = File.ReadAllBytes(path: ChannelManifestPath);
        var commit = ReadManifest().Build.Commit;
        var buildsManifestPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: "builds",
            path3: commit,
            path4: "manifest.json"
        );
        var tampered = Encoding.UTF8.GetBytes(s: tamper(arg: ((JsonObject)JsonNode.Parse(utf8Json: original)!)).ToJsonString());

        try {
            File.WriteAllBytes(
                bytes: tampered,
                path: ChannelManifestPath
            );
            File.WriteAllBytes(
                bytes: tampered,
                path: buildsManifestPath
            );

            var (exitCode, _, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
                "--tree", fixture.OutRoot,
                "--channel", OfficialBuildFixture.Channel,
            ]).Invoke());

            return (exitCode, stdErr);
        } finally {
            File.WriteAllBytes(
                bytes: original,
                path: ChannelManifestPath
            );
            File.WriteAllBytes(
                bytes: original,
                path: buildsManifestPath
            );
        }
    }

    [Fact]
    public void Build_PublishesEveryPuckSourceOnceByteForByte() {
        var sources = ReadManifest().Sources;
        var sourceFiles = Directory.EnumerateFiles(
            path: WorldsDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.puck"
        ).ToList();

        Assert.NotEmpty(collection: sourceFiles);

        foreach (var file in sourceFiles) {
            var name = WorkspaceName(file: file);
            var entry = Assert.Single(
                collection: sources,
                predicate: entry => (entry.Name == name)
            );

            Assert.Equal(
                expected: OfficialSourceContentTypes.Puck,
                actual: entry.ContentType
            );
            Assert.Equal(
                expected: File.ReadAllBytes(path: file),
                actual: ReadObject(path: entry.Path)
            );
        }
    }
    [Fact]
    public void Build_PublishesTheLocksAndSourcelessDocumentsTheWorkspaceCompilesFrom() {
        var names = ReadManifest().Sources.Select(selector: static entry => entry.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(
            path: WorldsDirectory,
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*.json"
        )) {
            var name = WorkspaceName(file: file);
            var stem = name[..name.IndexOf(
                comparisonType: StringComparison.Ordinal,
                value: '.'
            )];
            var hasSource = File.Exists(path: Path.Combine(
                path1: WorldsDirectory,
                path2: (stem + ".puck")
            ));
            var expected = (name.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: ".world.json"
            )
                ? !hasSource
                : (hasSource && (name.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: ".embeddings.json"
                ) || name.EndsWith(
                    comparisonType: StringComparison.Ordinal,
                    value: ".assets.json"
                )))
            );

            Assert.True(
                condition: (names.Contains(item: name) == expected),
                userMessage: $"'{name}' should {(expected ? string.Empty : "not ")}be a published source."
            );
        }

        Assert.Contains(
            collection: names,
            expected: "games/wordspy.embeddings.json"
        );
    }
    [Fact]
    public void Build_TheMountedSourcesCompileToEveryPublishedDocument() {
        var manifest = ReadManifest();
        var mount = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-official-sources-{Guid.NewGuid():n}"
        );

        try {
            foreach (var entry in manifest.Sources) {
                var target = Path.Combine(
                    path1: mount,
                    path2: entry.Name
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
                File.WriteAllBytes(
                    bytes: ReadObject(path: entry.Path),
                    path: target
                );
            }

            // Each source compiles once; a composition's worlds are named beside it, as `puck compile` writes them.
            var compiled = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

            foreach (var source in manifest.Documents.Select(selector: static document => document.Source).Distinct().Where(predicate: static source => WorldDocumentName.IsSourceFile(path: source))) {
                var compilation = WorldCompiler.CompileFile(
                    allowMultiple: true,
                    cancellationToken: TestContext.Current.CancellationToken,
                    path: Path.Combine(
                        path1: mount,
                        path2: source
                    )
                );

                Assert.True(
                    condition: compilation.Success,
                    userMessage: $"{source}: {string.Join(separator: "; ", values: compilation.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code} {diagnostic.Message}"))}"
                );

                if (compilation.Worlds.Count == 0) {
                    compiled[WorldDocumentName.OfSourceFile(path: source)] = compilation.RequireJson().ToJsonString();
                } else {
                    foreach (var world in compilation.Worlds) {
                        compiled[(source[..(source.LastIndexOf(value: '/') + 1)] + world.Name)] = world.Json.ToJsonString();
                    }
                }
            }

            foreach (var document in manifest.Documents.Where(predicate: static document => WorldDocumentName.IsSourceFile(path: document.Source))) {
                Assert.True(
                    condition: compiled.TryGetValue(
                        key: document.Name,
                        value: out var json
                    ),
                    userMessage: $"{document.Source} compiles to no document named '{document.Name}'."
                );
                Assert.Equal(
                    expected: Encoding.UTF8.GetString(bytes: ReadObject(path: document.Path)),
                    actual: json
                );
            }
        } finally {
            Directory.Delete(
                path: mount,
                recursive: true
            );
        }
    }
    [Fact]
    public void Build_EveryDocumentIsNamedByItsDocumentNameAndNamesTheSourceThatAuthorsIt() {
        var manifest = ReadManifest();
        var sources = manifest.Sources.ToDictionary(
            comparer: StringComparer.Ordinal,
            elementSelector: static entry => entry,
            keySelector: static entry => entry.Name
        );

        foreach (var document in manifest.Documents) {
            Assert.True(
                condition: WorldDocumentName.TryValidate(
                    name: document.Name,
                    reason: out var nameReason
                ),
                userMessage: nameReason
            );
            Assert.True(
                condition: sources.TryGetValue(
                    key: document.Source,
                    value: out var source
                ),
                userMessage: $"{document.Name}'s source '{document.Source}' names no sources[] file."
            );

            if (WorldDocumentName.IsDocumentFile(path: document.Source)) {
                Assert.Equal(
                    expected: WorldDocumentName.DocumentFile(name: document.Name),
                    actual: document.Source
                );
                Assert.Equal(
                    expected: source!.Hash,
                    actual: document.Hash
                );
            } else if (File.Exists(path: Path.Combine(
                path1: WorldsDirectory,
                path2: WorldDocumentName.SourceFile(name: document.Name)
            ))) {
                Assert.Equal(
                    expected: WorldDocumentName.SourceFile(name: document.Name),
                    actual: document.Source
                );
            } else {
                // A world a composition source declares, named beside it.
                Assert.Equal(
                    expected: document.Source[..(document.Source.LastIndexOf(value: '/') + 1)],
                    actual: document.Name[..(document.Name.LastIndexOf(value: '/') + 1)]
                );
            }
        }

        Assert.Equal(
            expected: "puck",
            actual: Assert.Single(collection: manifest.Composed).Name
        );
    }
    [Fact]
    public void Build_PublishesEveryDocumentTheWorkspaceAuthors() {
        var manifest = ReadManifest();
        var authoring = manifest.Documents.Select(selector: static document => document.Source).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var source in manifest.Sources.Where(predicate: static source => (WorldDocumentName.IsSourceFile(path: source.Name) || WorldDocumentName.IsDocumentFile(path: source.Name)))) {
            Assert.True(
                condition: authoring.Contains(item: source.Name),
                userMessage: $"'{source.Name}' authors no published document."
            );
        }

        foreach (var name in ((string[])["puck", "standard", "avatars/moth", "moth-courtyard", "pipeline", "tools/hgb-mirror", "games/klondike", "modules/arcade", "shards/quilt-ne"])) {
            Assert.Contains(
                collection: manifest.Documents,
                filter: document => (document.Name == name)
            );
        }
    }
    // The root and the basis resolve by document name, so either authored as a .puck source publishes and composes
    // like any other; a composition source publishes one document per world it declares.
    [Fact]
    public void Build_ResolvesTheRootAndBasisByNameAndPublishesEachWorldACompositionDeclares() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));

        var scratch = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-official-authored-{Guid.NewGuid():n}"
        );
        var worlds = Path.Combine(
            path1: scratch,
            path2: "worlds"
        );
        var tree = Path.Combine(
            path1: scratch,
            path2: "tree"
        );

        try {
            foreach (var file in Directory.EnumerateFiles(
                path: WorldsDirectory,
                searchOption: SearchOption.AllDirectories,
                searchPattern: "*"
            )) {
                var target = Path.Combine(
                    path1: worlds,
                    path2: Path.GetRelativePath(
                        path: file,
                        relativeTo: WorldsDirectory
                    )
                );

                Directory.CreateDirectory(path: Path.GetDirectoryName(path: target)!);
                File.Copy(
                    destFileName: target,
                    sourceFileName: file
                );
            }

            foreach (var name in ((string[])["puck", "standard"])) {
                var document = Path.Combine(
                    path1: worlds,
                    path2: WorldDocumentName.DocumentFile(name: name)
                );

                File.WriteAllText(
                    contents: WorldDecompiler.Decompile(jsonText: File.ReadAllText(path: document)),
                    path: Path.Combine(
                        path1: worlds,
                        path2: WorldDocumentName.SourceFile(name: name)
                    )
                );
                File.Delete(path: document);
            }

            Directory.CreateDirectory(path: Path.Combine(
                path1: worlds,
                path2: "beacons"
            ));

            foreach (var file in Directory.EnumerateFiles(
                path: Path.Combine(
                    path1: repositoryRoot!,
                    path2: "worlds/beacons"
                ),
                searchPattern: "*.puck"
            )) {
                File.Copy(
                    destFileName: Path.Combine(
                        path1: worlds,
                        path2: "beacons",
                        path3: Path.GetFileName(path: file)
                    ),
                    sourceFileName: file
                );
            }

            var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
                "--tree", tree,
                "--channel", OfficialBuildFixture.Channel,
                "--engine", OfficialBuildFixture.AppBundlePath,
                "--worlds", worlds,
                "--allow-dirty",
            ]).Invoke());

            Assert.True(
                condition: (exitCode == 0),
                userMessage: $"official build exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}"
            );

            var manifest = JsonSerializer.Deserialize<OfficialManifest>(
                options: DocumentJsonOptions.Shared,
                utf8Json: File.ReadAllBytes(path: Path.Combine(
                    path1: tree,
                    path2: OfficialBuildFixture.Channel,
                    path3: "manifest.json"
                ))
            )!;
            var bySource = manifest.Documents.ToLookup(
                comparer: StringComparer.Ordinal,
                keySelector: static document => document.Source
            );

            Assert.Equal(
                expected: ["puck"],
                actual: bySource["puck.puck"].Select(selector: static document => document.Name)
            );
            Assert.Equal(
                expected: [("standard", OfficialDocumentRoles.Basis)],
                actual: bySource["standard.puck"].Select(selector: static document => (document.Name, document.Role))
            );
            Assert.Equal(
                expected: ["beacons/north", "beacons/south"],
                actual: bySource["beacons/beacons.puck"].Select(selector: static document => document.Name).Order(comparer: StringComparer.Ordinal)
            );
            // beacon.puck is the module library the composition expands: it emits no document of its own.
            Assert.Empty(collection: bySource["beacons/beacon.puck"]);
            Assert.Contains(
                collection: manifest.Sources,
                filter: static source => (source.Name == "beacons/beacon.puck")
            );
            Assert.Equal(
                expected: "puck",
                actual: Assert.Single(collection: manifest.Composed).Name
            );
            // The scratch worlds directory sits in no checkout, so no commit names the tree it was built from.
            Assert.Equal(
                expected: (OfficialBuildInfo.NoCommit, true),
                actual: (manifest.Build.Commit, manifest.Build.Dirty)
            );

            var (verifyExitCode, verifyStdOut, verifyStdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
                "--tree", tree,
                "--channel", OfficialBuildFixture.Channel,
            ]).Invoke());

            Assert.True(
                condition: (verifyExitCode == 0),
                userMessage: $"official verify exited {verifyExitCode}:\nSTDOUT:\n{verifyStdOut}\nSTDERR:\n{verifyStdErr}"
            );
        } finally {
            if (Directory.Exists(path: scratch)) {
                Directory.Delete(
                    path: scratch,
                    recursive: true
                );
            }
        }
    }
    [Fact]
    public void Build_Succeeds() {
        Assert.True(
            condition: (fixture.ExitCode == 0),
            userMessage: $"official build exited {fixture.ExitCode}:\nSTDOUT:\n{fixture.StdOut}\nSTDERR:\n{fixture.StdErr}"
        );
    }
    [Fact]
    public void Build_WritesChannelAndBuildsManifests() {
        var channelManifestPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: OfficialBuildFixture.Channel,
            path3: "manifest.json"
        );

        Assert.True(condition: File.Exists(path: channelManifestPath));

        using var document = JsonDocument.Parse(json: File.ReadAllText(path: channelManifestPath));
        var root = document.RootElement;

        Assert.Equal(
            expected: "puck.official.manifest.v1",
            actual: root.GetProperty(propertyName: "schema").GetString()
        );
        Assert.Equal(
            expected: "dev",
            actual: root.GetProperty(propertyName: "channel").GetString()
        );

        var commit = root.GetProperty(propertyName: "build").GetProperty(propertyName: "commit").GetString()!;

        // The default worlds directory is this checkout's own, so the build is named by its HEAD commit.
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: CliGit.TryResolveCommit(
            repository: repositoryRoot!,
            resolved: out var head,
            revision: "HEAD"
        ));
        Assert.Equal(
            actual: commit,
            expected: head
        );

        var buildsManifestPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: "builds",
            path3: commit,
            path4: "manifest.json"
        );

        Assert.True(condition: File.Exists(path: buildsManifestPath));
        Assert.Equal(
            expected: File.ReadAllBytes(path: channelManifestPath),
            actual: File.ReadAllBytes(path: buildsManifestPath)
        );

        var composed = root.GetProperty(propertyName: "composed");

        Assert.True(condition: (composed.GetArrayLength() > 0));
        Assert.Equal(
            expected: "puck",
            actual: composed[0].GetProperty(propertyName: "documentId").GetString()
        );
        Assert.NotNull(@object: composed[0].GetProperty(propertyName: "identity").GetString());

        var engineFiles = root.GetProperty(propertyName: "engine").GetProperty(propertyName: "files");

        Assert.True(condition: (engineFiles.GetArrayLength() > 0));
    }
    [InlineData("", false)]
    [InlineData("   \n  ", false)]
    [InlineData(" M src/Puck.Cli/Program.cs\n", true)]
    [InlineData("?? new-file.txt\n", true)]
    [Theory]
    public void IsDirtyPorcelainOutput_ReadsGitPorcelainCorrectly(string porcelain, bool expectedDirty) {
        Assert.Equal(
            expected: expectedDirty,
            actual: OfficialBuildCommand.IsDirtyPorcelainOutput(porcelainStdout: porcelain)
        );
    }
    [Fact]
    public void SecondBuild_RewritesNoObjectsAndReproducesTheSameManifestBytes() {
        var objectPaths = Directory.EnumerateFiles(
            path: Path.Combine(
                path1: fixture.OutRoot,
                path2: "objects"
            ),
            searchOption: SearchOption.AllDirectories,
            searchPattern: "*"
        ).ToList();
        var mtimesBefore = objectPaths.ToDictionary(
            elementSelector: File.GetLastWriteTimeUtc,
            keySelector: path => path
        );
        var channelManifestPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: OfficialBuildFixture.Channel,
            path3: "manifest.json"
        );
        var bytesBefore = File.ReadAllBytes(path: channelManifestPath);

        var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
            "--tree", fixture.OutRoot,
            "--channel", OfficialBuildFixture.Channel,
            "--engine", OfficialBuildFixture.AppBundlePath,
            "--allow-dirty",
        ]).Invoke());

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"official build exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}"
        );

        foreach (var path in objectPaths) {
            Assert.Equal(
                expected: mtimesBefore[path],
                actual: File.GetLastWriteTimeUtc(path: path)
            );
        }

        Assert.Equal(
            expected: bytesBefore,
            actual: File.ReadAllBytes(path: channelManifestPath)
        );
    }
    [Fact]
    public void TamperedObject_MakesVerifyFailNamingIt() {
        using var manifestDocument = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
            path1: fixture.OutRoot,
            path2: OfficialBuildFixture.Channel,
            path3: "manifest.json"
        )));
        var relativeObjectPath = manifestDocument.RootElement.GetProperty(propertyName: "worldSchemaBundle").GetProperty(propertyName: "path").GetString()!;
        var fullObjectPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: relativeObjectPath
        );
        var original = File.ReadAllBytes(path: fullObjectPath);

        try {
            File.WriteAllBytes(
                bytes: [.. original, ((byte)'!')],
                path: fullObjectPath
            );

            var (exitCode, _, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
                "--tree", fixture.OutRoot,
                "--channel", OfficialBuildFixture.Channel,
            ]).Invoke());

            Assert.Equal(
                actual: exitCode,
                expected: 1
            );
            Assert.Contains(
                actualString: stdErr,
                expectedSubstring: relativeObjectPath
            );
        } finally {
            File.WriteAllBytes(
                bytes: original,
                path: fullObjectPath
            );
        }
    }
    [Fact]
    public void TamperedSourceObject_MakesVerifyFailNamingIt() {
        var source = ReadManifest().Sources.First(predicate: static entry => entry.Name.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".puck"
        ));
        var fullObjectPath = Path.Combine(
            path1: fixture.OutRoot,
            path2: source.Path
        );
        var original = File.ReadAllBytes(path: fullObjectPath);

        try {
            File.WriteAllBytes(
                bytes: [.. original, ((byte)'!')],
                path: fullObjectPath
            );

            var (exitCode, _, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
                "--tree", fixture.OutRoot,
                "--channel", OfficialBuildFixture.Channel,
            ]).Invoke());

            Assert.Equal(
                actual: exitCode,
                expected: 1
            );
            Assert.Contains(
                actualString: stdErr,
                expectedSubstring: $"sources[{source.Name}]: '{source.Path}' hashes to"
            );
        } finally {
            File.WriteAllBytes(
                bytes: original,
                path: fullObjectPath
            );
        }
    }
    [Fact]
    public void DanglingDocumentSource_MakesVerifyFailNamingIt() {
        var (exitCode, stdErr) = VerifyWithTamperedManifest(tamper: static manifest => {
            manifest["documents"]![0]!["source"] = "games/absent.puck";

            return manifest;
        });

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: stdErr,
            expectedSubstring: "documents[0].source: 'games/absent.puck' does not name a file in sources."
        );
    }
    [Fact]
    public void FileFormDocumentName_MakesVerifyFailNamingIt() {
        var (exitCode, stdErr) = VerifyWithTamperedManifest(tamper: static manifest => {
            var klondike = manifest["documents"]!.AsArray().Single(predicate: static document => (((string?)document!["name"]) == "games/klondike"))!;

            klondike["name"] = "games/klondike.world.json";

            return manifest;
        });

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: stdErr,
            expectedSubstring: "documents[games/klondike.world.json]: 'games/klondike.world.json' names a file"
        );
    }
    [Fact]
    public void DocumentAuthoredByAnotherDocumentsFile_MakesVerifyFailNamingIt() {
        var (exitCode, stdErr) = VerifyWithTamperedManifest(tamper: static manifest => {
            var arcade = manifest["documents"]!.AsArray().Single(predicate: static document => (((string?)document!["name"]) == "modules/arcade"))!;

            arcade["source"] = "modules/kart.world.json";

            return manifest;
        });

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: stdErr,
            expectedSubstring: "documents[modules/arcade]: source 'modules/kart.world.json' is another document's file."
        );
    }
    [Fact]
    public void DuplicateSourceName_MakesVerifyFailNamingIt() {
        var (exitCode, stdErr) = VerifyWithTamperedManifest(tamper: static manifest => {
            var sources = manifest["sources"]!.AsArray();

            sources.Add(item: sources[0]!.DeepClone());

            return manifest;
        });

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.Contains(
            actualString: stdErr,
            expectedSubstring: "is declared more than once."
        );
    }
    [Fact]
    public void TwoIndependentBuilds_ProduceByteIdenticalCommitManifest() {
        var secondRoot = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-official-tests-{Guid.NewGuid():n}"
        );

        try {
            var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialBuildCommand.Create().Parse(args: [
                "--tree", secondRoot,
                "--channel", OfficialBuildFixture.Channel,
                "--engine", OfficialBuildFixture.AppBundlePath,
                "--allow-dirty",
            ]).Invoke());

            Assert.True(
                condition: (exitCode == 0),
                userMessage: $"official build exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}"
            );

            using var firstManifest = JsonDocument.Parse(json: File.ReadAllText(path: Path.Combine(
                path1: fixture.OutRoot,
                path2: OfficialBuildFixture.Channel,
                path3: "manifest.json"
            )));
            var commit = firstManifest.RootElement.GetProperty(propertyName: "build").GetProperty(propertyName: "commit").GetString()!;
            var firstBuildsManifest = Path.Combine(
                path1: fixture.OutRoot,
                path2: "builds",
                path3: commit,
                path4: "manifest.json"
            );
            var secondBuildsManifest = Path.Combine(
                path1: secondRoot,
                path2: "builds",
                path3: commit,
                path4: "manifest.json"
            );

            Assert.Equal(
                expected: File.ReadAllBytes(path: firstBuildsManifest),
                actual: File.ReadAllBytes(path: secondBuildsManifest)
            );
        } finally {
            if (Directory.Exists(path: secondRoot)) {
                Directory.Delete(
                    path: secondRoot,
                    recursive: true
                );
            }
        }
    }
    [Fact]
    public void Verify_PassesOnTheFreshTree() {
        var (exitCode, stdOut, stdErr) = OfficialBuildFixture.RunCapturingConsole(run: () => OfficialVerifyCommand.Create().Parse(args: [
            "--tree", fixture.OutRoot,
            "--channel", OfficialBuildFixture.Channel,
        ]).Invoke());

        Assert.True(
            condition: (exitCode == 0),
            userMessage: $"official verify exited {exitCode}:\nSTDOUT:\n{stdOut}\nSTDERR:\n{stdErr}"
        );
    }
}
