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

    [Fact]
    public void TreeWithoutPathsChecksAndWritesExactlyTheExplicitSources() {
        using var directory = new TemporaryDirectory();
        var sources = new[] {
            directory.WriteText(name: "worlds/z.puck", text: World),
            directory.WriteText(name: "worlds/A.puck", text: World),
            directory.WriteText(name: "worlds/modules/rooms.puck", text: Library),
            directory.WriteText(name: "worlds/games/field.puck", text: World),
            directory.WriteText(name: "worlds/games/document.world.json", text: AuthoredDocument),
            directory.WriteText(name: "worlds/hub.world.json", text: AuthoredDocument),
            directory.WriteText(name: "worlds/z.world.json", text: StaleTwin),
        }.Order(comparer: StringComparer.Ordinal).ToArray();

        _ = directory.WriteText(name: "worlds/ignored.json", text: "not a world");
        _ = directory.WriteText(name: "worlds/ignored.txt", text: "not a source");
        var tree = directory.PathOf(name: "worlds");
        var output = directory.PathOf(name: "explicit");
        var automatic = directory.PathOf(name: "automatic");
        string[] compile = ["compile", "--tree", tree, "--output", output];

        var built = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [.. compile, .. sources]));

        Assert.True(condition: (built.ExitCode == 0), userMessage: built.Output);
        var explicitCheck = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [.. compile, "--check", .. sources]));

        Assert.True(condition: (explicitCheck.ExitCode == 0), userMessage: explicitCheck.Output);
        var automaticCheck = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [.. compile, "--check"]));

        Assert.True(condition: (automaticCheck.ExitCode == 0), userMessage: automaticCheck.Output);
        var automaticBuild = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: ["compile", "--tree", tree, "--output", automatic]));

        Assert.True(condition: (automaticBuild.ExitCode == 0), userMessage: automaticBuild.Output);
        Assert.Equal(expected: FilesUnder(directory: output), actual: FilesUnder(directory: automatic));

        foreach (var file in FilesUnder(directory: output)) {
            Assert.Equal(expected: File.ReadAllBytes(path: Path.Combine(path1: output, path2: file)),
                actual: File.ReadAllBytes(path: Path.Combine(path1: automatic, path2: file)));
        }

        Assert.True(condition: (automaticBuild.Output.IndexOf(comparisonType: StringComparison.Ordinal, value: "'A.puck'") <
            automaticBuild.Output.IndexOf(comparisonType: StringComparison.Ordinal, value: "'z.puck'")), userMessage: automaticBuild.Output);

        // Paths cross the output boundary with forward slashes.
        foreach (var run in new[] { built, explicitCheck, automaticCheck, automaticBuild }) {
            Assert.DoesNotContain(actualString: run.Output, expectedSubstring: "\\");
        }
    }
    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void PackageCleanupRefusesALinkedPackageAndCleansAStoreReachedThroughALink(bool linkPackage) {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "worlds/field.puck", text: World);
        const string Key = "0123456789abcdef";
        var sentinel = directory.WriteText(name: $"outside/{Key}/foreign.hlsl", text: "keep this file");
        var link = directory.PathOf(name: (linkPackage ? $"out/packages/{Key}" : "out/packages"));
        var target = directory.PathOf(name: (linkPackage ? $"outside/{Key}" : "outside"));

        Directory.CreateDirectory(path: Path.GetDirectoryName(path: link)!);
        DirectoryLinks.Create(link: link, target: target);

        try {
            var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
                "compile", "--tree", directory.PathOf(name: "worlds"),
                "--output", directory.PathOf(name: "out"), source,
            ]));

            if (linkPackage) {
                // A package inside the store that links out of it is never removed through.
                Assert.True(condition: (exitCode != 0), userMessage: log);
                Assert.Contains(actualString: log, expectedSubstring: "SHADERPKG_OUTPUT");
                Assert.Equal(actual: File.ReadAllText(path: sentinel), expected: "keep this file");
            } else {
                // The store itself may be a link: it is wherever the link leads, and its unnamed package is removed there.
                Assert.True(condition: (exitCode == 0), userMessage: log);
                Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: sentinel)!));
            }
            Assert.True(condition: ((File.GetAttributes(path: link) & FileAttributes.ReparsePoint) != 0));
        } finally {
            DirectoryLinks.Remove(link: link);
        }
    }
    [Fact]
    public void PackageCleanupPreservesAnotherWritersStagingAndReplacementDirectories() {
        using var directory = new TemporaryDirectory();
        var source = directory.WriteText(name: "worlds/field.puck", text: World);
        const string Key = "0123456789abcdef";
        var staged = directory.WriteText(name: $"out/packages/{Key}.partial-111111111111/staged.hlsl", text: "being written");
        var replaced = directory.WriteText(name: $"out/packages/{Key}.replaced-222222222222/saved.hlsl", text: "being replaced");
        var stale = directory.WriteText(name: $"out/packages/{Key}/unused.hlsl", text: "unreferenced package");
        var report = directory.PathOf(name: "worlds.written");

        var (exitCode, log) = ConsoleCapture.Run(run: () => PuckRootCommand.Invoke(args: [
            "compile", "--tree", directory.PathOf(name: "worlds"),
            "--output", directory.PathOf(name: "out"), "--written", report, source,
        ]));

        Assert.True(condition: (exitCode == 0), userMessage: log);
        Assert.Equal(actual: File.ReadAllText(path: staged), expected: "being written");
        Assert.Equal(actual: File.ReadAllText(path: replaced), expected: "being replaced");
        Assert.False(condition: Directory.Exists(path: Path.GetDirectoryName(path: stale)!));
        Assert.Equal(actual: File.ReadAllText(path: report), expected: "field.puckb\nfield.world.json\n");
    }
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
