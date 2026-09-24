using System.Text.Json;
using Puck.Abstractions;
using Puck.Assets.Documents;
using Puck.Testing;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: <c>puck compile</c> writes a document only for a source that emits one
/// (<see cref="Puck.World.Transpiler.WorldCompilation.EmitsDocument"/>). A module library, which declares no world and
/// lowers to an empty document, writes nothing and prints nothing — that outcome is its design, not a notable one —
/// so it can never stand in a tree's output under the name of the world a composition beside it declares. And no run
/// writes one document file twice: a second source claiming a document file an earlier one wrote, exactly or in
/// another letter case, is refused by name.</summary>
public sealed class ModuleLibraryCompileLawTests {
    // The shape of worlds/rulepush: a composition declaring the world `hub` from the module its sibling library,
    // named like that world, declares.
    private const string Composition = """
        import "hub.puck"
        world hub = hub(1)
        world spoke = hub(2)
        """;
    private const string Library = "module hub(value) { state { world { slot charge = value } } }\n";

    private static string[] DocumentsUnder(string directory) => [.. Directory.EnumerateFiles(
        path: directory,
        searchOption: SearchOption.AllDirectories,
        searchPattern: "*.world.json"
    ).Select(selector: path => Path.GetRelativePath(
        path: path,
        relativeTo: directory
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    )).Order(comparer: StringComparer.Ordinal)];

    [Fact]
    public void AModuleLibraryCompilesToNoFileAndPrintsNothing() {
        using var directory = new TemporaryDirectory();
        var library = directory.WriteText(
            name: "rulepush/hub.puck",
            text: Library
        );

        var (exitCode, output) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["compile", library]));

        Assert.Equal(
            actual: exitCode,
            expected: 0
        );
        Assert.Empty(collection: DocumentsUnder(directory: directory.PathOf(name: "rulepush")));
        Assert.Empty(collection: output.Trim());
    }
    /// <summary>A tree run over a composition and the library it imports, in either order, writes the composition's
    /// worlds alone: the library's empty document never replaces the world <c>hub</c> the composition declares.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATreeRunWritesTheDeclaredWorldAndNothingForTheLibraryNamedLikeIt(bool libraryFirst) {
        using var directory = new TemporaryDirectory();
        var composition = directory.WriteText(
            name: "worlds/rulepush/rulepush.puck",
            text: Composition
        );
        var library = directory.WriteText(
            name: "worlds/rulepush/hub.puck",
            text: Library
        );
        var output = directory.PathOf(name: "out");

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            output,
            .. (libraryFirst
                ? (string[])[library, composition]
                : [composition, library]),
        ]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: log
        );
        Assert.Equal(
            actual: DocumentsUnder(directory: output),
            expected: ["rulepush/hub.world.json", "rulepush/spoke.world.json"]
        );

        using var hub = JsonDocument.Parse(utf8Json: File.ReadAllBytes(path: Path.Combine(
            path1: output,
            path2: "rulepush/hub.world.json"
        )));

        Assert.Equal(
            actual: hub.RootElement.GetProperty(propertyName: "documentId").GetString(),
            expected: "hub"
        );
        Assert.DoesNotMatch(
            actualString: log,
            expectedRegexPattern: @"hub\.puck\b"
        );
    }
    /// <summary>A world source beside a composition that declares a world of its name, exactly or in another letter
    /// case, would write the same document file; the tree run refuses the pair, in either order, in the words every
    /// door refuses two carriers of one document name in (<see cref="DocumentName.Collision"/>), naming both
    /// sources.</summary>
    [Theory]
    [InlineData("hub.puck", false)]
    [InlineData("hub.puck", true)]
    [InlineData("Hub.puck", false)]
    [InlineData("Hub.puck", true)]
    public void ATreeRunRefusesTwoSourcesThatWriteOneDocumentFile(string sibling, bool siblingFirst) {
        using var directory = new TemporaryDirectory();
        var composition = directory.WriteText(
            name: "worlds/rulepush/rulepush.puck",
            text: "module room(value) { state { world { slot charge = value } } }\nworld hub = room(1)\n"
        );
        var world = directory.WriteText(
            name: ("worlds/rulepush/" + sibling),
            text: "schema: \"puck.world.definition.v1\"\n"
        );

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            directory.PathOf(name: "out"),
            .. (siblingFirst
                ? (string[])[world, composition]
                : [composition, world]),
        ]));

        // The one refusal every door gives two files carrying one document name, whichever of them is met first, each
        // name spelled relative to the tree.
        var siblingName = ("rulepush/" + Path.GetFileNameWithoutExtension(path: sibling));
        var compositionFile = PuckPaths.Normalize(path: composition);
        var worldFile = PuckPaths.Normalize(path: world);

        Assert.Equal(
            actual: exitCode,
            expected: 1
        );
        Assert.True(
            condition: (log.Contains(value: DocumentName.Collision(
                heldFile: compositionFile,
                heldName: "rulepush/hub",
                otherFile: worldFile,
                otherName: siblingName
            ), comparisonType: StringComparison.Ordinal) || log.Contains(value: DocumentName.Collision(
                heldFile: worldFile,
                heldName: siblingName,
                otherFile: compositionFile,
                otherName: "rulepush/hub"
            ), comparisonType: StringComparison.Ordinal)),
            userMessage: log
        );
    }
}
