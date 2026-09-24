using Puck.Cli.Official;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: every verb that enumerates a directory's world documents reads it through the one
/// enumeration the composer resolves names by (<see cref="Puck.World.Transpiler.Composition.PuckDocumentComposer.TryCarriers"/>),
/// so the official build and <c>puck test</c> agree with the game on which file carries each document name, and both
/// refuse by name two files whose document names differ only in letter case. A module library emits no document, so its
/// file name is no document name anywhere: a document file named like it is its name's carrier. The official build
/// holds the worlds a composition source declares to the same rule and the same refusal
/// (<see cref="Puck.Assets.Documents.DocumentName.Collision"/>).</summary>
/// <remarks>The official build once keyed names ordinally while the other enumerations ignored case, so
/// <c>Foo.puck</c> beside <c>foo.world.json</c> published two documents for what a Windows checkout resolves as one.</remarks>
public sealed class WorldDocumentCarrierLawTests {
    private const string NoRootRefusal = "no document named 'puck'";
    private const string Refusal = "whose names differ only in letter case; a document name is unique ignoring case";
    private const string RepeatedRefusal = "both carry the document 'beacons/north'; a document name is unique ignoring case";
    // A composition that declares the worlds north and south beside it: two documents no file carries.
    private const string Composition = """
        module beacon(value) { state { world { slot charge = value } } }
        world north = beacon(1)
        world south = beacon(2)
        """;

    private static TemporaryDirectory CaseCollision() {
        var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/Foo.puck", text: "schema: \"puck.world.definition.v1\"\n");
        directory.WriteText(name: "worlds/foo.world.json", text: "{ \"schema\": \"puck.world.definition.v1\" }");

        return directory;
    }

    [Fact]
    public void TheOfficialBuildRefusesTwoDocumentNamesThatDifferOnlyInCase() {
        using var directory = CaseCollision();

        Assert.False(condition: OfficialWorldDocumentScanner.TryScan(
            composed: out _,
            composedDefinition: out _,
            documents: out _,
            reason: out var reason,
            sources: out _,
            worldsDirectory: directory.PathOf(name: "worlds"),
            writer: new OfficialObjectWriter(root: directory.PathOf(name: "objects"))
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: Refusal
        );
    }

    // Scans a worlds directory holding beacons/beacons.puck (Composition) and one sibling file, and returns the refusal.
    private static string ScanCompositionBeside(string sibling, string text) {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/beacons/beacons.puck", text: Composition);
        directory.WriteText(name: ("worlds/beacons/" + sibling), text: text);

        Assert.False(condition: OfficialWorldDocumentScanner.TryScan(
            composed: out _,
            composedDefinition: out _,
            documents: out _,
            reason: out var reason,
            sources: out _,
            worldsDirectory: directory.PathOf(name: "worlds"),
            writer: new OfficialObjectWriter(root: directory.PathOf(name: "objects"))
        ));

        return reason;
    }

    /// <summary>A world a composition declares is a document name like a file's, so one that differs only in case from
    /// a sibling document's name is refused by the carriers' own rule, whether the sibling is a source or a
    /// document.</summary>
    [Theory]
    [InlineData("North.world.json", "{ \"schema\": \"puck.world.definition.v1\" }")]
    [InlineData("North.puck", "schema: \"puck.world.definition.v1\"\n")]
    public void TheOfficialBuildRefusesADeclaredWorldThatDiffersOnlyInCaseFromASibling(string sibling, string text) => Assert.Contains(
        actualString: ScanCompositionBeside(
            sibling: sibling,
            text: text
        ),
        expectedSubstring: Refusal
    );
    /// <summary>A declared world under a sibling's exact name is two carriers of one document, not the source beside
    /// its own document the carriers admit: a reference to <c>beacons/north</c> resolves to the sibling file, never to
    /// the declared world, so the two would publish one name twice.</summary>
    [Theory]
    [InlineData("north.world.json", "{ \"schema\": \"puck.world.definition.v1\" }")]
    [InlineData("north.puck", "schema: \"puck.world.definition.v1\"\n")]
    public void TheOfficialBuildRefusesADeclaredWorldThatRepeatsASiblingName(string sibling, string text) => Assert.Contains(
        actualString: ScanCompositionBeside(
            sibling: sibling,
            text: text
        ),
        expectedSubstring: RepeatedRefusal
    );
    /// <summary>CONTROL: a sibling whose name no declared world shares passes the name check, so the scan goes on to
    /// refuse only the tree's missing root document.</summary>
    [Fact]
    public void TheOfficialBuildAdmitsADeclaredWorldBesideADistinctSibling() {
        var reason = ScanCompositionBeside(
            sibling: "east.world.json",
            text: "{ \"schema\": \"puck.world.definition.v1\" }"
        );

        Assert.Contains(
            actualString: reason,
            expectedSubstring: NoRootRefusal
        );
        Assert.DoesNotContain(
            actualString: reason,
            expectedSubstring: "unique ignoring case"
        );
    }

    // A composition whose worlds expand a module a sibling library declares, the shape of worlds/rulepush: the library
    // is named like the world it backs and declares no world of its own.
    private const string ImportingComposition = """
        import "north.puck"
        world north = north(1)
        world south = north(2)
        """;
    private const string ModuleLibrary = "module north(value) { state { world { slot charge = value } } }\n";

    // Scans a worlds directory holding beacons/beacons.puck (ImportingComposition), its module library under
    // `library`, and any further sibling files, and returns the refusal.
    private static string ScanImportingCompositionBeside(string library, params (string Name, string Text)[] siblings) {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/beacons/beacons.puck", text: ImportingComposition.Replace(
            comparisonType: StringComparison.Ordinal,
            newValue: library,
            oldValue: "north.puck"
        ));
        directory.WriteText(name: ("worlds/beacons/" + library), text: ModuleLibrary);

        foreach (var (name, text) in siblings) {
            directory.WriteText(name: ("worlds/beacons/" + name), text: text);
        }

        Assert.False(condition: OfficialWorldDocumentScanner.TryScan(
            composed: out _,
            composedDefinition: out _,
            documents: out _,
            reason: out var reason,
            sources: out _,
            worldsDirectory: directory.PathOf(name: "worlds"),
            writer: new OfficialObjectWriter(root: directory.PathOf(name: "objects"))
        ));

        return reason;
    }

    /// <summary>A module library emits no document, so it carries no document name: one named like a world the
    /// composition beside it declares, in either case, collides with nothing, and the scan goes on to refuse only the
    /// tree's missing root document.</summary>
    [Theory]
    [InlineData("north.puck")]
    [InlineData("North.puck")]
    public void TheOfficialBuildAdmitsAModuleLibraryNamedLikeADeclaredWorld(string library) {
        var reason = ScanImportingCompositionBeside(library: library);

        Assert.Contains(
            actualString: reason,
            expectedSubstring: NoRootRefusal
        );
        Assert.DoesNotContain(
            actualString: reason,
            expectedSubstring: "unique ignoring case"
        );
    }
    /// <summary>Beside that module library, a declared world still collides with a sibling that does emit a document
    /// under its name, ignoring case, whether the sibling is a document or a world source.</summary>
    [Theory]
    [InlineData("south.world.json", "{ \"schema\": \"puck.world.definition.v1\" }", "both carry the document 'beacons/south'")]
    [InlineData("South.world.json", "{ \"schema\": \"puck.world.definition.v1\" }", "whose names differ only in letter case")]
    [InlineData("south.puck", "schema: \"puck.world.definition.v1\"\n", "both carry the document 'beacons/south'")]
    [InlineData("South.puck", "schema: \"puck.world.definition.v1\"\n", "whose names differ only in letter case")]
    public void TheOfficialBuildRefusesADeclaredWorldBesideAModuleLibraryThatASiblingEmits(string sibling, string text, string refusal) {
        var reason = ScanImportingCompositionBeside(
            library: "north.puck",
            siblings: (sibling, text)
        );

        Assert.Contains(
            actualString: reason,
            expectedSubstring: refusal
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "a document name is unique ignoring case"
        );
    }
    /// <summary>A module library carries no document name, so a document file named like it is its name's carrier and
    /// a published document: beside a library <c>hub.puck</c>, <c>hub.world.json</c> collides with the world
    /// <c>Hub</c> a composition declares, which it could not do while the library hid it.</summary>
    [Fact]
    public void TheOfficialBuildAuthorsTheDocumentNamedLikeAModuleLibrary() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/beacons/beacons.puck", text: "import \"hub.puck\"\nworld Hub = hub(1)\n");
        directory.WriteText(name: "worlds/beacons/hub.puck", text: "module hub(value) { state { world { slot charge = value } } }\n");
        directory.WriteText(name: "worlds/beacons/hub.world.json", text: "{ \"schema\": \"puck.world.definition.v1\" }");

        Assert.False(condition: OfficialWorldDocumentScanner.TryScan(
            composed: out _,
            composedDefinition: out _,
            documents: out _,
            reason: out var reason,
            sources: out _,
            worldsDirectory: directory.PathOf(name: "worlds"),
            writer: new OfficialObjectWriter(root: directory.PathOf(name: "objects"))
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: Refusal
        );
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "beacons/hub.world.json"
        );
    }
    /// <summary>A module library spelled in another letter case than a document file beside it names nothing, so the
    /// pair is no collision and the scan goes on to refuse only the tree's missing root document.</summary>
    [Fact]
    public void TheOfficialBuildAdmitsAModuleLibraryBesideADocumentInAnotherCase() {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: "worlds/Hub.puck", text: "module hub(value) { state { world { slot charge = value } } }\n");
        directory.WriteText(name: "worlds/hub.world.json", text: "{ \"schema\": \"puck.world.definition.v1\" }");

        Assert.False(condition: OfficialWorldDocumentScanner.TryScan(
            composed: out _,
            composedDefinition: out _,
            documents: out _,
            reason: out var reason,
            sources: out _,
            worldsDirectory: directory.PathOf(name: "worlds"),
            writer: new OfficialObjectWriter(root: directory.PathOf(name: "objects"))
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: NoRootRefusal
        );
    }
    /// <summary><c>puck test</c> sweeps a directory as the composer resolves it: the document file named like a
    /// module library is swept (and skipped here, since it declares no schedule), and so is the library itself.</summary>
    [Theory]
    [InlineData("hub.puck")]
    [InlineData("Hub.puck")]
    public void PuckTestSweepsTheDocumentNamedLikeAModuleLibrary(string library) {
        using var directory = new TemporaryDirectory();

        directory.WriteText(name: ("worlds/" + library), text: "module hub(value) { state { world { slot charge = value } } }\n");

        var document = directory.WriteText(name: "worlds/hub.world.json", text: "{ \"schema\": \"puck.world.definition.v1\" }").Replace(
            newChar: '/',
            oldChar: '\\'
        );

        var (exitCode, output) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["test", directory.PathOf(name: "worlds")]));

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            expectedSubstring: $"test: skipped {document} — it declares no schedule section."
        );
        Assert.Contains(
            actualString: output,
            expectedSubstring: $"{library} — it declares no schema, so it is a module rather than a world"
        );
        Assert.DoesNotContain(
            actualString: output,
            expectedSubstring: "unique ignoring case"
        );
    }
    [Fact]
    public void PuckTestRefusesTwoDocumentNamesThatDifferOnlyInCase() {
        using var directory = CaseCollision();

        var (exitCode, output) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["test", directory.PathOf(name: "worlds")]));

        Assert.Equal(
            actual: exitCode,
            expected: 2
        );
        Assert.Contains(
            actualString: output,
            expectedSubstring: Refusal
        );
    }
}
