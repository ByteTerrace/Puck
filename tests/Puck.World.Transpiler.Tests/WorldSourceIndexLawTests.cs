using System.Text;
using Puck.Testing;
using Puck.Transpiler.Modules;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Transpiler.Tests;

/// <summary>CONTRACT UNDER TEST: a directory's document names come from one index (<see cref="WorldSourceIndex"/>),
/// which parses every source and compiles none: each document file carries the name it spells and each source exactly
/// the names it emits, whatever its stem. The composer resolves a name through it, so a name declared by a source of
/// another stem resolves where it ships, and every world a composition declares resolves by name. A compile that
/// resolved a name through it rests on the directory's listing and every source there, so the compile cache serves it
/// again only while no file was added, removed or renamed there, in any letter case, and no source there changed what
/// it declares. A world is declared at the top level of its source under a written name, since only such a world is
/// one the parse finds.</summary>
public sealed class WorldSourceIndexLawTests {
    private const string Module = "module room(value) { state { world { slot charge = value } } }\n";

    private static string? ComposedDocumentId(TemporaryDirectory directory, string basis) {
        Assert.True(
            condition: PuckDocumentComposer.TryComposeWorldDocument(
                chainBytes: out _,
                composed: out var composed,
                reason: out var reason,
                rootBytes: Encoding.UTF8.GetBytes(s: $$"""{ "schema": "puck.world.definition.v1", "basis": "{{basis}}" }"""),
                rootResolvedPath: directory.PathOf(name: "root.world.json")
            ),
            userMessage: reason
        );

        return composed?["documentId"]?.GetValue<string>();
    }
    // Compiles the source through the cache, returning the compile the cache served or produced.
    private static WorldCompiledSource Compile(WorldCompileCache cache, string path) {
        Assert.True(
            condition: cache.TryCompile(
                compiled: out var compiled,
                failure: out var failure,
                path: path
            ),
            userMessage: failure?.Diagnostics.FormatReport(filePath: path)
        );

        return compiled!;
    }

    /// <summary>Every document file carries its name and every source the names it emits, and a source beside the
    /// document of its own exact name wins.</summary>
    [Fact]
    public void ADirectoryCarriesOneFilePerDocumentAndASourceWinsOverItsDocument() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "klondike.puck", text: WorldSources.Header);
        directory.WriteText(name: "klondike.world.json", text: "{}");
        directory.WriteText(name: "standard.world.json", text: "{}");
        directory.WriteText(name: "games/go.PUCK", text: WorldSources.Header);
        directory.WriteText(name: "games/go.assets.json", text: "{}");
        directory.WriteText(name: "games/notes.md", text: string.Empty);

        var root = directory.RootPath.Replace(
            newChar: '/',
            oldChar: '\\'
        );

        Assert.True(condition: PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: directory.RootPath,
            libraries: out var libraries,
            option: SearchOption.AllDirectories,
            reason: out var reason
        ), userMessage: reason);
        Assert.Equal(
            actual: carriers,
            expected: [
                new WorldDocumentCarrier(Name: "games/go", Path: $"{root}/games/go.PUCK"),
                new WorldDocumentCarrier(Name: "klondike", Path: $"{root}/klondike.puck"),
                new WorldDocumentCarrier(Name: "standard", Path: $"{root}/standard.world.json"),
            ]
        );
        Assert.Empty(collection: libraries);
        Assert.True(condition: PuckDocumentComposer.TryCarriers(
            carriers: out var top,
            directory: directory.RootPath,
            libraries: out _,
            option: SearchOption.TopDirectoryOnly,
            reason: out reason
        ), userMessage: reason);
        Assert.Equal(
            actual: top.Select(selector: static carrier => carrier.Name),
            expected: ["klondike", "standard"]
        );
    }
    /// <summary>Two files whose names differ only in case are refused rather than resolved, since which one a file
    /// system reads is the machine's choice.</summary>
    [InlineData("Foo.puck", "foo.world.json")]
    [InlineData("foo.puck", "Foo.world.json")]
    [InlineData("Foo.world.json", "fOO.world.JSON")]
    [Theory]
    public void TwoFilesWhoseDocumentNamesDifferOnlyInCaseAreRefusedByName(string first, string second) {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: first, text: (WorldDocumentName.IsSourceFile(path: first) ? WorldSources.Header : "{}"));

        if (!File.Exists(path: directory.PathOf(name: second))) {
            directory.WriteText(name: second, text: (WorldDocumentName.IsSourceFile(path: second) ? WorldSources.Header : "{}"));
        }

        var carried = PuckDocumentComposer.TryCarriers(
            carriers: out var carriers,
            directory: directory.RootPath,
            libraries: out _,
            option: SearchOption.TopDirectoryOnly,
            reason: out var reason
        );

        // A case-insensitive file system holds only one file where the names match whole; nothing is ambiguous then.
        if (Directory.EnumerateFiles(path: directory.RootPath).Count() < 2) {
            Assert.True(condition: carried, userMessage: reason);

            return;
        }

        Assert.False(condition: carried);
        Assert.Empty(collection: carriers);
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "whose names differ only in letter case; a document name is unique ignoring case"
        );
    }
    /// <summary>The index reads what a source declares from its parse: a library emits nothing, an ordinary source its
    /// stem, a composition the worlds it declares under the names written, and a source that does not parse its
    /// stem.</summary>
    [Fact]
    public void TheIndexReadsEachSourcesNamesFromItsParseAlone() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "library.puck", text: (Module + "let unused = 1\n"));
        directory.WriteText(name: "ordinary.puck", text: WorldSources.Header);
        directory.WriteText(name: "estate.puck", text: (Module + "entry world north = room(1)\nworld \"south\" = room(2)\n"));
        directory.WriteText(name: "broken.puck", text: "state {\n");
        directory.WriteText(name: "lobby.world.json", text: "{}");

        Assert.Equal(
            actual: WorldSourceIndex.Claims(directory: directory.RootPath).Select(selector: static claims => (Path.GetFileName(path: claims.Path), string.Join(separator: ",", values: claims.Names))),
            expected: [
                ("broken.puck", "broken"),
                ("estate.puck", "north,south"),
                ("library.puck", string.Empty),
                ("lobby.world.json", "lobby"),
                ("ordinary.puck", "ordinary"),
            ]
        );
    }
    /// <summary>A world a composition declares resolves by its own name, which is not the composition's stem.</summary>
    [Fact]
    public void EveryWorldACompositionDeclaresResolvesByName() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "foo.puck", text: (Module + "world north = room(1)\nworld south = room(2)\n"));

        Assert.Equal(actual: ComposedDocumentId(basis: "north", directory: directory), expected: "north");
        Assert.Equal(actual: ComposedDocumentId(basis: "south", directory: directory), expected: "south");
    }
    /// <summary>A compile that resolved its basis through the index stops standing when a file appears beside the
    /// basis, when the basis document is renamed only in letter case, and when a source there changes the names it
    /// declares; it stands while nothing there moved.</summary>
    [Fact]
    public void AHeldCompileStandsOnlyWhileTheIndexItResolvedThroughHolds() {
        using var directory = new TemporaryDirectory();
        var cache = new WorldCompileCache();
        var child = directory.WriteText(name: "child.puck", text: (WorldSources.Header + "basis: \"foo\"\n"));

        directory.WriteText(name: "foo.world.json", text: """{ "schema": "puck.world.definition.v1", "documentId": "foo" }""");
        directory.WriteText(name: "bar.puck", text: (Module + "world other = room(1)\n"));

        var first = Compile(cache: cache, path: child);

        Assert.Contains(collection: first.Inputs, filter: static input => (input.Kind == CompileInputKind.Listing));
        Assert.Same(actual: Compile(cache: cache, path: child), expected: first);

        // A source beside the basis starts declaring another name: the listing holds, the source's bytes do not.
        directory.WriteText(name: "bar.puck", text: (Module + "world different = room(1)\n"));

        var renamed = Compile(cache: cache, path: child);

        Assert.NotSame(actual: renamed, expected: first);
        Assert.Same(actual: Compile(cache: cache, path: child), expected: renamed);

        // A file appears beside the basis.
        directory.WriteText(name: "notes.txt", text: string.Empty);

        var added = Compile(cache: cache, path: child);

        Assert.NotSame(actual: added, expected: renamed);

        // The basis document is renamed only in letter case.
        File.Move(
            destFileName: directory.PathOf(name: "Foo.world.json"),
            sourceFileName: directory.PathOf(name: "foo.world.json")
        );

        Assert.NotSame(actual: Compile(cache: cache, path: child), expected: added);
    }
    /// <summary>A world declared inside another construct, or under a computed name, is refused by name: a parse would
    /// not find it, so no reader could resolve it.</summary>
    [Theory]
    [InlineData("for (name, index) in [\"north\", \"south\"] {\n    world name = room(index)\n}\n", "declared at the top level of its source")]
    [InlineData("let named = \"north\"\nworld $\"{named}\" = room(1)\n", "written as a name or a plain string, never computed")]
    public void AWorldAParseCannotFindIsRefused(string declarations, string refusal) {
        var compilation = WorldCompiler.Compile(
            allowMultiple: true,
            cancellationToken: TestContext.Current.CancellationToken,
            source: (Module + declarations)
        );

        Assert.False(condition: compilation.Success);
        Assert.Contains(
            collection: compilation.Diagnostics,
            filter: diagnostic => diagnostic.Message.Contains(comparisonType: StringComparison.Ordinal, value: refusal)
        );
    }
}
