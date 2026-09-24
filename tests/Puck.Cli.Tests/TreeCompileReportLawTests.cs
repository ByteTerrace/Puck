using Puck.Abstractions.Documents;
using Puck.Testing;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>CONTRACT UNDER TEST: a <c>puck compile --tree</c> run reports, in its <c>--written</c> file, exactly the
/// files it left under its output, and the game's build ships that report (<c>build/WorldAssets.targets</c>), since only
/// a compile knows which sources emit documents. A module library writes and reports nothing; a hand-authored
/// <c>.world.json</c> beside a library of its name ships as it stands, since the library carries no document name; and a
/// <c>.world.json</c> beside the world source of its name never ships, since the source wins.</summary>
public sealed class TreeCompileReportLawTests {
    private const string Library = "module room(value) { state { world { slot charge = value } } }\n";
    private const string World = "schema: \"puck.world.definition.v1\"\n";
    // Hand-authored, and different from what either source compiles to.
    private const string AuthoredDocument = "{\n  \"documentId\": \"hub\",\n  \"schema\": \"puck.world.definition.v1\"\n}\n";
    private const string StaleTwin = "{\n  \"documentId\": \"stale\",\n  \"schema\": \"puck.world.definition.v1\"\n}\n";

    private static string[] FilesUnder(string directory) => [.. Directory.EnumerateFiles(
        path: directory,
        searchOption: SearchOption.AllDirectories,
        searchPattern: "*"
    ).Select(selector: path => Path.GetRelativePath(
        path: path,
        relativeTo: directory
    ).Replace(
        newChar: '/',
        oldChar: '\\'
    )).Order(comparer: StringComparer.Ordinal)];

    /// <summary>The tree the targets hand the run, in the order they hand it (every source, then every document) and
    /// in the reverse order: a module library alone, a library beside a hand-authored document of its name, and a
    /// world beside a stale document of its name.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheReportNamesExactlyTheFilesTheRunWrote(bool documentsFirst) {
        using var directory = new TemporaryDirectory();
        var sources = new[] {
            directory.WriteText(name: "worlds/modules/rooms.puck", text: Library),
            directory.WriteText(name: "worlds/hub.puck", text: Library),
            directory.WriteText(name: "worlds/games/field.puck", text: World),
        };
        var documents = new[] {
            directory.WriteText(name: "worlds/hub.world.json", text: AuthoredDocument),
            directory.WriteText(name: "worlds/games/field.world.json", text: StaleTwin),
        };
        var output = directory.PathOf(name: "out");
        var report = directory.PathOf(name: "worlds.written");

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            output,
            "--written",
            report,
            .. (documentsFirst
                ? [.. documents, .. sources]
                : (string[])[.. sources, .. documents]),
        ]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: log
        );

        string[] expected = ["games/field.puckb", "games/field.world.json", "hub.puckb", "hub.world.json"];

        Assert.Equal(
            actual: File.ReadAllText(path: report),
            expected: string.Concat(values: expected.Select(selector: static line => (line + "\n")))
        );
        Assert.Equal(
            actual: FilesUnder(directory: output),
            expected: expected
        );
        // The library's neighbour ships byte for byte; the world's own document is its source's, not the stale twin.
        Assert.Equal(
            actual: File.ReadAllText(path: Path.Combine(
                path1: output,
                path2: "hub.world.json"
            )),
            expected: AuthoredDocument
        );
        Assert.Equal(
            actual: File.ReadAllBytes(path: Path.Combine(
                path1: output,
                path2: "games/field.world.json"
            )),
            expected: CanonicalJsonDocument.Serialize(node: WorldCompiler.CompileFile(
                cancellationToken: TestContext.Current.CancellationToken,
                path: sources[2]
            ).RequireJson())
        );
    }
    /// <summary>A run that fails reports nothing, and removes the report an earlier run left, so a build never ships
    /// what a partial run wrote as a whole run's output.</summary>
    [Fact]
    public void AFailedRunLeavesNoReport() {
        using var directory = new TemporaryDirectory();
        var broken = directory.WriteText(
            name: "worlds/broken.puck",
            text: "schema: \"puck.world.definition.v1\"\nstate {\n"
        );
        var report = directory.WriteText(
            name: "worlds.written",
            text: "broken.world.json\n"
        );

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            directory.PathOf(name: "out"),
            "--written",
            report,
            broken,
        ]));

        Assert.True(
            condition: (exitCode != 0),
            userMessage: log
        );
        Assert.False(condition: File.Exists(path: report));
    }
    /// <summary>A report named under a directory that does not exist yet is written there, as the output is.</summary>
    [Fact]
    public void AReportUnderAMissingDirectoryIsWritten() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(
            name: "worlds/field.puck",
            text: World
        );
        var report = directory.PathOf(name: "reports/nested/worlds.written");

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile",
            "--tree",
            directory.PathOf(name: "worlds"),
            "--output",
            directory.PathOf(name: "out"),
            "--written",
            report,
            source,
        ]));

        Assert.True(
            condition: (exitCode == 0),
            userMessage: log
        );
        Assert.Equal(
            actual: File.ReadAllText(path: report),
            expected: "field.puckb\nfield.world.json\n"
        );
    }
}
