using System.Collections.Immutable;
using System.Text;
using Puck.Abstractions.Documents;
using Puck.Testing;
using Puck.World;
using Puck.World.Transpiler;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Worlds.Tests;

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
    private static string SourceWorlds() => Path.Combine(
        path1: RepositoryPaths.RequireRoot(),
        path2: ShippedWorldDocuments.WorldDirectory
    );
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

        int Build() => ConsoleCapture.Run(run: () => SuiteRoot.Invoke(args: [
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
}
