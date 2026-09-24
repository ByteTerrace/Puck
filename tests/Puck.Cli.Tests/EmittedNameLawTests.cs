using System.Text;
using System.Text.Json;
using Puck.Testing;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: the names a source carries are exactly the names it emits
/// (<see cref="WorldCompilation.EmittedNames"/>): a composition the worlds it declares, a module library none, and an
/// ordinary source its own stem, whatever file carries them. The document composer and the tree compile answer what a
/// name resolves to from one index that reads those names from each source's parse (<see cref="WorldSourceIndex"/>),
/// which agrees with what every tracked source compiles to, so a <c>basis</c> composes against the file a tree run
/// ships under the name, and both refuse the same pair by name
/// (<see cref="Puck.Assets.Documents.DocumentName.Collision"/>).</summary>
public sealed class EmittedNameLawTests {
    private const string Authored = "{\n  \"documentId\": \"authored\",\n  \"schema\": \"puck.world.definition.v1\"\n}\n";
    private const string Module = "module room(value) { state { world { slot charge = value } } }\n";
    private const string Refusal = "both carry the document 'foo'; a document name is unique ignoring case";

    // Composes a root beside the tree whose basis is the document `foo`, returning the composed documentId.
    private static (bool Composed, string? DocumentId, string Reason) ComposeFoo(string tree) {
        var composed = PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out var document,
            reason: out var reason,
            rootBytes: Encoding.UTF8.GetBytes(s: """{ "schema": "puck.world.definition.v1", "basis": "foo" }"""),
            rootResolvedPath: Path.Combine(
                path1: tree,
                path2: "host.world.json"
            )
        );

        return (composed, document?["documentId"]?.GetValue<string>(), reason);
    }
    // `puck compile --tree` over every file in the tree, reporting what it wrote.
    private static (int ExitCode, string Log, string[] Report) CompileTree(TemporaryDirectory directory) {
        var report = directory.PathOf(name: "out.written");

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            directory.PathOf(name: "out"),
            "--written",
            report,
            .. Directory.EnumerateFiles(path: directory.PathOf(name: "worlds")).Order(comparer: StringComparer.Ordinal),
        ]));

        return (exitCode, log, (File.Exists(path: report) ? File.ReadAllLines(path: report) : []));
    }
    private static string? ShippedDocumentId(TemporaryDirectory directory, string name) {
        using var document = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: directory.PathOf(name: ("out/" + name))));

        return document.RootElement.GetProperty(propertyName: "documentId").GetString();
    }

    /// <summary>A composition declaring <c>north</c> and <c>south</c> does not carry its own stem, so <c>foo</c> is the
    /// hand-authored document beside it: a basis composes against it, and a tree run ships it with both worlds.</summary>
    [Fact]
    public void ACompositionDeclaringOtherWorldsLeavesItsStemToTheDocumentBesideIt() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/foo.puck", text: (Module + "world north = room(1)\nworld south = room(2)\n"));
        directory.WriteText(name: "worlds/foo.world.json", text: Authored);

        var (composed, documentId, reason) = ComposeFoo(tree: directory.PathOf(name: "worlds"));

        Assert.True(condition: composed, userMessage: reason);
        Assert.Equal(actual: documentId, expected: "authored");

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.True(condition: (exitCode == 0), userMessage: log);
        foreach (var shipped in ((string[])["foo.world.json", "north.world.json", "south.world.json"])) {
            Assert.Contains(collection: report, expected: shipped);
        }

        Assert.Equal(actual: File.ReadAllText(path: directory.PathOf(name: "out/foo.world.json")), expected: Authored);
    }
    /// <summary>A composition declaring a world under its own exact stem carries that name, so it wins over the document
    /// beside it in both places.</summary>
    [Fact]
    public void ACompositionDeclaringItsOwnStemWinsOverTheDocumentBesideIt() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/foo.puck", text: (Module + "world foo = room(1)\nworld south = room(2)\n"));
        directory.WriteText(name: "worlds/foo.world.json", text: Authored);

        var (composed, documentId, reason) = ComposeFoo(tree: directory.PathOf(name: "worlds"));

        Assert.True(condition: composed, userMessage: reason);
        Assert.Equal(actual: documentId, expected: "foo");

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.True(condition: (exitCode == 0), userMessage: log);
        Assert.Contains(collection: report, expected: "foo.world.json");
        Assert.Equal(actual: ShippedDocumentId(directory: directory, name: "foo.world.json"), expected: "foo");
    }
    /// <summary>A name resolves to the source that emits it whatever that file's stem: <c>bar.puck</c> declaring
    /// <c>foo</c> beside no <c>foo.*</c> is <c>foo</c>'s carrier, so a basis of <c>foo</c> composes against bar's world
    /// and a tree run ships <c>foo.world.json</c> from bar.</summary>
    [Fact]
    public void ASourceOfAnotherStemCarriesTheNameItDeclaresInBothPlaces() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/bar.puck", text: (Module + "world foo = room(1)\n"));

        var (composed, documentId, reason) = ComposeFoo(tree: directory.PathOf(name: "worlds"));

        Assert.True(condition: composed, userMessage: reason);
        Assert.Equal(actual: documentId, expected: "foo");

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.True(condition: (exitCode == 0), userMessage: log);
        Assert.Contains(collection: report, expected: "foo.world.json");
        Assert.DoesNotContain(collection: report, expected: "bar.world.json");
        Assert.Equal(actual: ShippedDocumentId(directory: directory, name: "foo.world.json"), expected: "foo");
    }
    /// <summary>A source of another stem declaring a name beside the document of that name makes two claims to it,
    /// and only a source beside the document of its own exact stem wins, so both places refuse the pair by name.</summary>
    [Fact]
    public void ASourceOfAnotherStemBesideTheDocumentOfItsNameIsRefusedInBothPlaces() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/bar.puck", text: (Module + "world foo = room(1)\n"));
        directory.WriteText(name: "worlds/foo.world.json", text: Authored);

        var (composed, _, reason) = ComposeFoo(tree: directory.PathOf(name: "worlds"));

        Assert.False(condition: composed);
        Assert.Contains(actualString: reason, expectedSubstring: Refusal);

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: log, expectedSubstring: Refusal);
        Assert.Empty(collection: report);
        Assert.False(condition: Directory.Exists(path: directory.PathOf(name: "out")), userMessage: log);
    }
    /// <summary>Every world a composition declares resolves in the source tree by its own name, as a tree run ships
    /// it: <c>north</c>, declared by <c>foo.puck</c>, is a basis the composer resolves.</summary>
    [Fact]
    public void AWorldACompositionDeclaresResolvesInTheSourceTreeAsItShips() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/foo.puck", text: (Module + "world north = room(1)\nworld south = room(2)\n"));

        var composed = PuckDocumentComposer.TryComposeWorldDocument(
            chainBytes: out _,
            composed: out var document,
            reason: out var reason,
            rootBytes: Encoding.UTF8.GetBytes(s: """{ "schema": "puck.world.definition.v1", "basis": "north" }"""),
            rootResolvedPath: directory.PathOf(name: "worlds/host.world.json")
        );

        Assert.True(condition: composed, userMessage: reason);
        Assert.Equal(actual: document?["documentId"]?.GetValue<string>(), expected: "north");

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.True(condition: (exitCode == 0), userMessage: log);
        Assert.Contains(collection: report, expected: "north.world.json");
        Assert.Equal(actual: ShippedDocumentId(directory: directory, name: "north.world.json"), expected: "north");
    }
    public static TheoryData<string> TrackedSources() => TrackedPuckSources.Under();
    /// <summary>The name index reads, from a parse alone, exactly the names every tracked world source emits: the
    /// names its compile writes documents under, each world the lowering produced for a composition, nothing for a
    /// source whose lowering is empty and declares no world, and the stem for every other.</summary>
    [Theory]
    [MemberData(memberName: nameof(TrackedSources))]
    public void TheIndexReadsExactlyTheNamesEveryTrackedSourceEmits(string relative) {
        var path = RepositoryPaths.Resolve(relativePath: relative);
        var compilation = WorldCompiler.CompileFile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            path: path
        );

        // A cartridge source rides the same language core under another vocabulary and carries no world name.
        if (string.Equals(a: compilation.Document?.Schema, b: "puck.cartridge.v1", comparisonType: StringComparison.Ordinal)) {
            return;
        }

        Assert.True(condition: compilation.Success, userMessage: compilation.Diagnostics.FormatReport(filePath: path));

        var lowered = ((compilation.Worlds.Count > 0)
            ? [.. compilation.Worlds.Select(selector: static world => world.Name)]
            : ((compilation.Json is { Count: > 0 })
                ? [Path.GetFileNameWithoutExtension(path: path)]
                : Array.Empty<string>()));
        var indexed = WorldSourceIndex.Claims(directory: Path.GetDirectoryName(path: path)!).Single(predicate: claims => string.Equals(
            a: Path.GetFileName(path: claims.Path),
            b: Path.GetFileName(path: path),
            comparisonType: StringComparison.Ordinal
        )).Names;

        Assert.Equal(actual: indexed, expected: lowered);
        Assert.Equal(actual: indexed, expected: compilation.DocumentNames(sourcePath: path));
    }
    /// <summary>A source whose file stem differs only in case from the name it emits is not that name's own source, so
    /// beside the document of that name both carry it, and both places refuse the pair by name.</summary>
    [Fact]
    public void ACaseVariantSourceBesideTheDocumentItEmitsIsRefusedInBothPlaces() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/Foo.puck", text: (Module + "world foo = room(1)\n"));
        directory.WriteText(name: "worlds/foo.world.json", text: Authored);

        var (composed, _, reason) = ComposeFoo(tree: directory.PathOf(name: "worlds"));

        Assert.False(condition: composed);
        Assert.Contains(actualString: reason, expectedSubstring: Refusal);

        var (exitCode, log, report) = CompileTree(directory: directory);

        Assert.Equal(actual: exitCode, expected: 1);
        Assert.Contains(actualString: log, expectedSubstring: Refusal);
        Assert.Empty(collection: report);
    }
}
