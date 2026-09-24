using System.Collections.Immutable;
using System.Text;
using Puck.Abstractions.Documents;
using Puck.Testing;
using Puck.World;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a compiled world document is build output, never source. The build compiles every
/// shipped <c>.puck</c> source under <c>src/Puck.World/Assets/worlds</c> into the game's own output, so the source
/// trees hold only sources, a renamed source leaves nothing behind that a document name can still reach, and the
/// output the game ships carries every document a source emits exactly once, under the name the source emits it by and
/// byte for byte what the source compiles to, every hand-authored document it ships byte for byte as authored,
/// beside every document that draws as a world its compiled world, byte for byte what the document derives fresh, and
/// at its root one bake pack holding each bake those compiled worlds name once, byte for byte what they bake
/// fresh.</summary>
/// <remarks>What an output must hold is what a tree compile reports writing (<c>--written</c>), the report
/// <c>build/WorldAssets.targets</c> ships, since only a compile knows what a source emits: a composition emits the
/// worlds it declares and nothing under its own name, and a module library emits nothing. The game's own Release
/// catalog is checked against a fresh run by <c>puck compile --tree … --check</c>, which <c>puck affected</c> chooses
/// when the shipped worlds or the compile that writes them change, rather than by every run of this suite.</remarks>
public sealed class WorldDocumentOutputLawTests {
    private static readonly string[] SourceTrees = ["src", "worlds"];

    private static string SourceWorlds() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: ShippedWorldDocuments.WorldDirectory
    );

    // One document a tree run ships: its output-relative path, the bytes it must hold, the path it composes at (where
    // its source's compile composes it), and the file those bytes come from.
    internal readonly record struct ShippedDocument(string Relative, ReadOnlyMemory<byte> Bytes, string ComposeAt, string Origin);
    // What a tree run over one tree ships: the files its report names, in the report's order, and each document among
    // them with the bytes it must hold.
    internal sealed record Shipment(ImmutableArray<string> Report, ImmutableArray<ShippedDocument> Documents);

    private static string[] FilesUnder(string tree, string suffix) => [.. Directory.EnumerateFiles(
        path: tree,
        searchOption: SearchOption.AllDirectories,
        searchPattern: ("*" + suffix)
    ).Order(comparer: StringComparer.Ordinal)];
    // Every file of every pipeline package in the store at a catalog's root; none when it holds no store.
    private static string[] PackageFilesUnder(string catalog) {
        var store = Path.Combine(
            path1: catalog,
            path2: Puck.Shaders.ShaderPackager.StoreDirectoryName
        );

        return (Directory.Exists(path: store)
            ? FilesUnder(
                suffix: string.Empty,
                tree: store
            )
            : []
        );
    }
    // `puck compile --tree` as build/WorldAssets.targets runs it: every .puck source and every .world.json document
    // under the tree, into `output`, reporting to `report`. Returns the report's lines.
    private static string[] CompileTree(string tree, string output, string report) {
        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            tree,
            "--output",
            output,
            "--written",
            report,
            .. FilesUnder(
                suffix: WorldDocumentName.SourceSuffix,
                tree: tree
            ),
            .. FilesUnder(
                suffix: WorldDocumentName.DocumentSuffix,
                tree: tree
            ),
        ]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: log
        );

        return File.ReadAllLines(path: report);
    }
    // Every document the sources under the tree emit, keyed by output-relative path ignoring case, each compiled
    // here on its own rather than read from the run under test, under each name the compile says it emits
    // (WorldCompilation.DocumentNames): each world a composition declares, or else the one document an ordinary
    // source's top level lowers to, under its file's stem. A module library emits nothing.
    private static Dictionary<string, ShippedDocument> Emissions(string tree) {
        var emissions = new Dictionary<string, ShippedDocument>(comparer: StringComparer.OrdinalIgnoreCase);

        foreach (var source in FilesUnder(
            suffix: WorldDocumentName.SourceSuffix,
            tree: tree
        )) {
            var compilation = WorldCompiler.CompileFile(
                allowMultiple: true,
                cancellationToken: TestContext.Current.CancellationToken,
                path: source
            );

            Assert.False(
                condition: compilation.Diagnostics.HasErrors,
                userMessage: $"{source} does not compile: {string.Join(separator: "; ", values: compilation.Diagnostics.Select(selector: static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"))}"
            );

            foreach (var name in compilation.DocumentNames(sourcePath: source)) {
                var json = (compilation.Worlds.FirstOrDefault(predicate: world => string.Equals(
                    a: world.Name,
                    b: name,
                    comparisonType: StringComparison.Ordinal
                ))?.Json ?? compilation.Json!);
                var composeAt = Path.GetFullPath(path: Path.Combine(
                    path1: Path.GetDirectoryName(path: source)!,
                    path2: WorldDocumentName.DocumentFile(name: name)
                ));
                var relative = Relative(
                    path: composeAt,
                    root: tree
                );

                Assert.True(
                    condition: emissions.TryAdd(
                        key: relative,
                        value: new ShippedDocument(
                            Bytes: CanonicalJsonDocument.Serialize(node: json),
                            ComposeAt: composeAt,
                            Origin: source,
                            Relative: relative
                        )
                    ),
                    userMessage: $"{source} and {emissions.GetValueOrDefault(key: relative).Origin} both emit {relative}"
                );
            }
        }

        return emissions;
    }
    // What the tree ships, from a fresh tree run's report: every emission, which the report must name, and every other
    // document the report names, which can only be a hand-authored document of the tree shipped as it stands.
    private static Shipment Ship(string tree) {
        using var directory = new TemporaryDirectory();
        var report = CompileTree(
            output: directory.PathOf(name: "worlds"),
            report: directory.PathOf(name: "worlds.written"),
            tree: tree
        );
        var emissions = Emissions(tree: tree);
        var documents = new List<ShippedDocument>();

        foreach (var emission in emissions.Values) {
            Assert.True(
                condition: report.Contains(value: emission.Relative),
                userMessage: $"{emission.Origin} emits {emission.Relative}, which the tree run does not report writing"
            );
        }

        foreach (var line in report.Where(predicate: static line => line.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: WorldDocumentName.DocumentSuffix
        ))) {
            if (emissions.TryGetValue(
                key: line,
                value: out var emission
            )) {
                documents.Add(item: emission);

                continue;
            }

            var authored = Path.GetFullPath(path: Path.Combine(
                path1: tree,
                path2: line
            ));

            Assert.True(
                condition: File.Exists(path: authored),
                userMessage: $"the tree run reports writing {line}, which no source emits and the tree holds no document at"
            );
            documents.Add(item: new ShippedDocument(
                Bytes: File.ReadAllBytes(path: authored),
                ComposeAt: authored,
                Origin: authored,
                Relative: line
            ));
        }

        return new Shipment(
            Documents: [.. documents],
            Report: [.. report]
        );
    }
    // The world documents, compiled worlds, bake packs and pipeline packages under a catalog, as forward-slashed
    // catalog-relative paths.
    private static string[] ShippedFiles(string catalog) => [.. ((string[])[WorldDocumentName.DocumentSuffix, CompiledWorld.Extension, WorldBakePack.Extension])
        .SelectMany(selector: suffix => FilesUnder(
            suffix: suffix,
            tree: catalog
        ))
        .Concat(second: PackageFilesUnder(catalog: catalog))
        .Select(selector: path => Relative(
            path: path,
            root: catalog
        ))
        .Order(comparer: StringComparer.Ordinal)];
    // The catalog holds exactly the files the tree run reports, each once ignoring case, every document byte for byte
    // what it must hold, and no source.
    private static void AssertTheCatalogShipsEachDocumentOnce(string catalog, Shipment shipment) {
        var shipped = ShippedFiles(catalog: catalog);
        var repeated = shipped
            .GroupBy(
                comparer: StringComparer.OrdinalIgnoreCase,
                keySelector: static path => path
            )
            .Where(predicate: static group => (group.Count() != 1))
            .Select(selector: static group => group.Key);

        Assert.Empty(collection: repeated);
        Assert.Equal(
            actual: shipped,
            expected: shipment.Report.Order(comparer: StringComparer.Ordinal)
        );

        foreach (var document in shipment.Documents) {
            Assert.True(
                condition: File.ReadAllBytes(path: Path.Combine(
                    path1: catalog,
                    path2: document.Relative
                )).AsSpan().SequenceEqual(other: document.Bytes.Span),
                userMessage: $"{document.Relative} in the catalog is not what {document.Origin} ships as it"
            );
        }

        Assert.Empty(collection: FilesUnder(
            suffix: WorldDocumentName.SourceSuffix,
            tree: catalog
        ));
    }
    // Beside each shipped document that draws as a world, the catalog holds its compiled world, byte for byte what the
    // document derives fresh where its source composes it, naming the pack at the catalog's root; the catalog holds no
    // other compiled world; and that pack holds each key the compiled worlds name once, byte for byte what the fresh
    // derivations baked, or is absent when they name none. Returns the compiled worlds derived.
    private static IReadOnlySet<string> AssertEveryShippedCompiledWorldIsWhatItsDocumentDerivesFresh(string catalog, Shipment shipment) {
        var machines = CliWorldVocabulary.EnsureInstalled();
        var shipped = FilesUnder(
            suffix: CompiledWorld.Extension,
            tree: catalog
        ).Select(selector: path => Relative(
            path: path,
            root: catalog
        )).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);
        var derived = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var packPath = Path.Combine(path1: catalog, path2: WorldBakePack.FileName);
        var keys = new HashSet<Puck.Assets.ContentPin>();

        foreach (var document in shipment.Documents) {
            if (!WorldSourceLoader.TryCompileWorld(
                bakePack: WorldBakePack.Reference(documentPath: Path.Combine(path1: catalog, path2: document.Relative), packPath: packPath),
                catalog: machines,
                catalogFingerprint: CliWorldVocabulary.Fingerprint(catalog: machines),
                compiledWorld: out var fresh,
                document: document.Bytes.ToArray(),
                path: document.ComposeAt,
                reason: out _
            )) {
                continue;
            }

            var file = (document.Relative[..^WorldDocumentName.DocumentSuffix.Length] + CompiledWorld.Extension);

            derived.Add(item: file);
            Assert.True(condition: shipped.Contains(item: file), userMessage: $"{document.Origin} derives a compiled world the catalog does not carry as {file}");
            Assert.True(
                condition: File.ReadAllBytes(path: Path.Combine(path1: catalog, path2: file)).AsSpan().SequenceEqual(other: fresh),
                userMessage: $"{file} in the catalog is not what {document.Origin} derives fresh"
            );
            Assert.True(condition: CompiledWorld.TryDecode(container: out var container, content: fresh, header: out _, reason: out var reason), userMessage: reason);
            Assert.True(condition: container.TryFind(chunk: out var bakes, code: WorldBakeChunk.BakeCode), userMessage: $"{file} names no bakes");
            Assert.True(condition: WorldBakeChunk.TryRead(keys: out var named, packReference: out _, payload: bakes.Payload.Span, reason: out reason), userMessage: reason);
            keys.UnionWith(other: named);
        }

        Assert.Empty(collection: shipped.Except(second: derived, comparer: StringComparer.OrdinalIgnoreCase));

        if (keys.Count == 0) {
            Assert.False(condition: File.Exists(path: packPath), userMessage: $"the catalog carries a bake pack no compiled world names a bake in");
        } else {
            var fresh = WorldBakePack.Encode(outcomes: keys.Select(selector: key => {
                Assert.True(condition: WorldSourceLoader.Bakes.TryGetHeld(key: key, outcome: out var outcome), userMessage: $"no fresh outcome for {key.Hex}");

                return KeyValuePair.Create(key: key, value: outcome);
            }));

            Assert.True(
                condition: File.ReadAllBytes(path: packPath).AsSpan().SequenceEqual(other: fresh),
                userMessage: $"the catalog's {WorldBakePack.FileName} is not the pack of the bakes its compiled worlds derive fresh"
            );
        }

        return derived;
    }
    private static string Relative(string root, string path) => Path.GetRelativePath(
        path: path,
        relativeTo: root
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    );
    private static IEnumerable<string> DocumentsUnder(string directory) {
        var pending = new Stack<string>(collection: [directory]);

        while (pending.TryPop(result: out var current)) {
            foreach (var child in Directory.EnumerateDirectories(path: current)) {
                if (Path.GetFileName(path: child) is not ("bin" or "obj" or "node_modules")) {
                    pending.Push(item: child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(
                path: current,
                searchPattern: ("*" + WorldDocumentName.DocumentSuffix)
            )) {
                yield return file;
            }
        }
    }

    // The source tree's own world documents: every *.world.json under src/ and worlds/ outside build output, as
    // forward-slashed repository-relative paths, set against the ones git tracks.
    internal static IReadOnlyList<string> UntrackedDocuments(string root) {
        var tracked = CliGit.Run(
            arguments: ["ls-files", "-z", "--", .. SourceTrees],
            repository: root
        );

        Assert.Equal(
            actual: tracked.ExitCode,
            expected: 0
        );

        var known = tracked.Stdout.Split(
            options: StringSplitOptions.RemoveEmptyEntries,
            separator: '\0'
        ).ToHashSet(comparer: StringComparer.OrdinalIgnoreCase);

        return [.. SourceTrees
            .Select(selector: tree => Path.Combine(
                path1: root,
                path2: tree
            ))
            .Where(predicate: Directory.Exists)
            .SelectMany(selector: DocumentsUnder)
            .Select(selector: path => Relative(
                path: path,
                root: root
            ))
            .Where(predicate: path => !known.Contains(item: path))
            .Order(comparer: StringComparer.Ordinal)];
    }

    [Fact]
    public void AfterABuildNoWorldDocumentUnderTheSourceTreesIsUntracked() {
        var untracked = UntrackedDocuments(root: RepositoryPaths.RequireRoot());

        Assert.True(
            condition: (untracked.Count == 0),
            userMessage: $"a world document under src/ or worlds/ that git does not track is build output written into a source tree, or an orphan a rename left behind: {string.Join(separator: ", ", values: untracked)}"
        );
    }
    [Fact]
    public void RenamingASourceLeavesNoLoadableOrphan() {
        using var directory = new TemporaryDirectory();
        var tree = Directory.CreateDirectory(path: directory.PathOf(name: "worlds/games")).Parent!.FullName;
        var output = directory.PathOf(name: "out");
        var sources = new[] { "games/go.puck", "games/tictactoe.puck" };

        foreach (var source in sources) {
            File.Copy(
                destFileName: Path.Combine(
                    path1: tree,
                    path2: source
                ),
                sourceFileName: Path.Combine(
                    path1: SourceWorlds(),
                    path2: source
                )
            );
        }

        int Build() => ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            tree,
            "--output",
            output,
            .. Directory.EnumerateFiles(
                path: tree,
                searchOption: SearchOption.AllDirectories,
                searchPattern: ("*" + WorldDocumentName.SourceSuffix)
            ),
        ])).ExitCode;

        Assert.Equal(
            actual: Build(),
            expected: 0
        );
        Assert.True(condition: File.Exists(path: Path.Combine(
            path1: output,
            path2: "games/go.world.json"
        )));

        File.Move(
            destFileName: Path.Combine(
                path1: tree,
                path2: "games/weiqi.puck"
            ),
            sourceFileName: Path.Combine(
                path1: tree,
                path2: "games/go.puck"
            )
        );

        Assert.Equal(
            actual: Build(),
            expected: 0
        );
        Assert.Equal(
            actual: Directory.EnumerateFiles(
                path: output,
                searchOption: SearchOption.AllDirectories,
                searchPattern: ("*" + WorldDocumentName.DocumentSuffix)
            ).Select(selector: path => Relative(
                path: path,
                root: output
            )).Order(comparer: StringComparer.Ordinal),
            expected: ["games/tictactoe.world.json", "games/weiqi.world.json"]
        );
        // Go draws as a world and tic-tac-toe is a fragment, so the renamed world's compiled world is the only one.
        Assert.Equal(
            actual: Directory.EnumerateFiles(
                path: output,
                searchOption: SearchOption.AllDirectories,
                searchPattern: ("*" + CompiledWorld.Extension)
            ).Select(selector: path => Relative(
                path: path,
                root: output
            )),
            expected: ["games/weiqi.puckb"]
        );

        // The old name reaches nothing, in the source tree or in the output.
        foreach (var root in ((string[])[tree, output])) {
            Assert.False(condition: PuckDocumentComposer.TryComposeWorldDocument(
                chainBytes: out _,
                composed: out _,
                reason: out var reason,
                rootBytes: Encoding.UTF8.GetBytes(s: """{ "schema": "puck.world.definition.v1", "imports": [ { "document": "games/go" } ] }"""),
                rootResolvedPath: Path.Combine(
                    path1: root,
                    path2: "host.world.json"
                )
            ));
            Assert.Contains(
                actualString: reason,
                expectedSubstring: "has neither a source"
            );
        }
    }
    /// <summary>The tree check passes over the output its own run wrote, and names a file whose bytes differ, one the
    /// output lacks, and one a run of the tree does not write, writing nothing under the output.</summary>
    [Fact]
    public void TheTreeCheckNamesEveryFileTheOutputHoldsOtherwiseThanAFreshRun() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "worlds/field.puck",
            text: "schema: \"puck.world.definition.v1\"\n"
        );
        directory.WriteText(
            name: "worlds/lobby.world.json",
            text: "{\n  \"documentId\": \"lobby\",\n  \"schema\": \"puck.world.definition.v1\"\n}\n"
        );

        var tree = directory.PathOf(name: "worlds");
        var output = directory.PathOf(name: "catalog");

        _ = CompileTree(
            output: output,
            report: directory.PathOf(name: "written"),
            tree: tree
        );

        (int ExitCode, string Log) Check() => ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            tree,
            "--output",
            output,
            "--check",
            .. FilesUnder(suffix: WorldDocumentName.SourceSuffix, tree: tree),
            .. FilesUnder(suffix: WorldDocumentName.DocumentSuffix, tree: tree),
        ]));

        Assert.Equal(actual: Check().ExitCode, expected: 0);

        File.AppendAllText(contents: " ", path: Path.Combine(path1: output, path2: "field.world.json"));
        File.Delete(path: Path.Combine(path1: output, path2: "lobby.world.json"));
        File.WriteAllText(contents: "stale", path: Path.Combine(path1: output, path2: "orphan.puckb"));

        var (exitCode, log) = Check();

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: log, comparisonType: StringComparison.Ordinal, expectedSubstring: "differs field.world.json");
        Assert.Contains(actualString: log, comparisonType: StringComparison.Ordinal, expectedSubstring: "missing lobby.world.json");
        Assert.Contains(actualString: log, comparisonType: StringComparison.Ordinal, expectedSubstring: "stale orphan.puckb");
        Assert.True(condition: File.Exists(path: Path.Combine(path1: output, path2: "orphan.puckb")));
    }
    /// <summary>A composition ships the worlds it declares, each under the name it declares, and nothing under its own
    /// name, beside an ordinary world and a hand-authored document: the catalog the targets ship from a tree run's
    /// report holds exactly those, and both laws hold over it.</summary>
    [Fact]
    public void ACompositionShipsTheWorldsItDeclaresAndNothingUnderItsOwnName() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(
            name: "worlds/estate/estate.puck",
            text: "module room(value) { state { world { slot charge = value } } }\nentry world north = room(1)\nworld south = room(2)\n"
        );
        directory.WriteText(
            name: "worlds/field.puck",
            text: "schema: \"puck.world.definition.v1\"\n"
        );
        directory.WriteText(
            name: "worlds/lobby.world.json",
            text: "{\n  \"documentId\": \"lobby\",\n  \"schema\": \"puck.world.definition.v1\"\n}\n"
        );

        var tree = directory.PathOf(name: "worlds");
        var catalog = directory.PathOf(name: "catalog");
        var output = directory.PathOf(name: "generated");

        // The targets copy exactly the report's files from the run's output into the output's Assets/worlds.
        foreach (var line in CompileTree(
            output: output,
            report: directory.PathOf(name: "generated.written"),
            tree: tree
        )) {
            var destination = Path.Combine(
                path1: catalog,
                path2: line
            );

            Directory.CreateDirectory(path: Path.GetDirectoryName(path: destination)!);
            File.Copy(
                destFileName: destination,
                sourceFileName: Path.Combine(
                    path1: output,
                    path2: line
                )
            );
        }

        var shipment = Ship(tree: tree);

        Assert.Equal(
            actual: shipment.Documents.Select(selector: static document => document.Relative).Order(comparer: StringComparer.Ordinal),
            expected: ["estate/north.world.json", "estate/south.world.json", "field.world.json", "lobby.world.json"]
        );
        AssertTheCatalogShipsEachDocumentOnce(
            catalog: catalog,
            shipment: shipment
        );
        AssertEveryShippedCompiledWorldIsWhatItsDocumentDerivesFresh(
            catalog: catalog,
            shipment: shipment
        );
    }
}
