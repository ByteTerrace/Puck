using Puck.Cli.Affected;
using Puck.Transpiler.Modules;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Tests;

/// <summary>
/// Laws for <see cref="AffectedRevisionExport"/>: an export holds exactly the named paths the revision recorded and
/// nothing else of the repository; a git run that outlasts the export's bound, or fails, refuses the export by name
/// instead of waiting on git; and on the real tree, every file a canary's world documents compose from lies in the
/// trees a revision's documents are exported from.
/// </summary>
public sealed class AffectedRevisionExportLawTests {
    private static string[] Files(string root) => [.. Directory.EnumerateFiles(path: root, searchOption: SearchOption.AllDirectories, searchPattern: "*")
        .Select(selector: file => Path.GetRelativePath(path: file, relativeTo: root).Replace(newChar: '/', oldChar: '\\'))
        .Order(comparer: StringComparer.Ordinal)];
    private static (GitScratchCheckout Checkout, string Revision) Recorded() {
        var checkout = new GitScratchCheckout();

        checkout.Write(name: "documents/world.puck", text: "schema: \"puck.world.definition.v1\"\n");
        checkout.Write(name: "documents/nested/layer.world.json", text: "{}");
        checkout.Write(name: "single.json", text: "{}");
        checkout.Write(name: "elsewhere/code.cs", text: "class Code { }");
        checkout.Write(name: "readme.md", text: "# Readme");

        var revision = checkout.Commit(message: "the tree");

        // The working tree moves on; the export reads the revision.
        checkout.Write(name: "documents/later.puck", text: "schema: \"puck.world.definition.v1\"\n");
        File.Delete(path: Path.Combine(path1: checkout.Root, path2: "single.json"));

        return (checkout, revision);
    }

    [Fact]
    public void AnExportHoldsExactlyTheNamedPathsTheRevisionRecorded() {
        var (checkout, revision) = Recorded();

        using (checkout) {
            using var export = AffectedRevisionExport.Create(paths: ["documents", "single.json", "absent"], repository: checkout.Root, revision: revision);

            Assert.Equal(actual: Files(root: export.Root), expected: ["documents/nested/layer.world.json", "documents/world.puck", "single.json"]);
            Assert.Equal(actual: File.ReadAllText(path: Path.Combine(path1: export.Root, path2: "single.json")), expected: "{}");

            var directory = Path.GetDirectoryName(path: export.Root)!;

            export.Dispose();
            Assert.False(condition: Directory.Exists(path: directory));
        }
    }
    [Fact]
    public void AnExportOfPathsTheRevisionDoesNotHoldIsEmpty() {
        var (checkout, revision) = Recorded();

        using (checkout) {
            using var export = AffectedRevisionExport.Create(paths: ["absent"], repository: checkout.Root, revision: revision);

            Assert.Empty(collection: Files(root: export.Root));
        }
    }
    [Fact]
    public void AGitRunThatOutlastsTheBoundRefusesTheExportByName() {
        var (checkout, revision) = Recorded();

        using (checkout) {
            var refusal = Assert.Throws<AffectedRevisionExportRefusedException>(testCode: () => AffectedRevisionExport.Create(bound: TimeSpan.Zero, paths: ["documents"], repository: checkout.Root, revision: revision));

            Assert.StartsWith(actualString: refusal.Message, expectedStartString: $"the export of {revision} is refused: git ");
            Assert.EndsWith(actualString: refusal.Message, expectedEndString: " did not finish within 0 s.");
        }
    }
    [Fact]
    public void AFailingGitRefusesTheExportByName() {
        var (checkout, _) = Recorded();

        using (checkout) {
            var refusal = Assert.Throws<AffectedRevisionExportRefusedException>(testCode: () => AffectedRevisionExport.Create(paths: ["documents"], repository: checkout.Root, revision: "no-such-revision"));

            Assert.StartsWith(actualString: refusal.Message, expectedStartString: "the export of no-such-revision is refused: git ls-tree exited ");
        }
    }
    /// <summary>On the real tree: every world document and <c>.puck</c> source a canary reaches, and every file, probe and
    /// listing the compile of each such source reads, lies in <see cref="AffectedRevisionExport.DocumentTrees"/>, so a
    /// base revision's export composes each canary's worlds as the working tree does.</summary>
    [Fact]
    public void EveryFileACanaryWorldComposesFromLiesInTheExportedTrees() {
        Assert.True(condition: CliPaths.TryGetRepositoryRoot(repositoryRoot: out var repositoryRoot));
        Assert.True(condition: AffectedCommand.TryCanaries(canaries: out var canaries, error: out var error, repositoryRoot: repositoryRoot), userMessage: error);

        var tree = new AffectedWorkingTree(root: repositoryRoot);
        var read = new SortedSet<string>(comparer: StringComparer.Ordinal);

        void Add(string path) => _ = read.Add(item: Path.GetRelativePath(path: Path.GetFullPath(path: path), relativeTo: repositoryRoot).Replace(newChar: '/', oldChar: '\\'));

        foreach (var root in canaries.SelectMany(selector: static canary => canary.Files).Distinct(comparer: StringComparer.Ordinal)) {
            foreach (var file in AffectedDocuments.Reach(path: root, tree: tree)) {
                var isSource = WorldDocumentName.IsSourceFile(path: file);

                if (!isSource && !file.EndsWith(comparisonType: StringComparison.OrdinalIgnoreCase, value: ".world.json")) {
                    continue;
                }

                Add(path: Path.Combine(path1: repositoryRoot, path2: file));

                if (isSource && WorldCompileCache.Shared.TryCompile(compiled: out var compiled, failure: out _, path: Path.Combine(path1: repositoryRoot, path2: file))) {
                    foreach (var input in compiled!.Inputs.Where(predicate: static input => (input.Kind is not CompileInputKind.Absent))) {
                        Add(path: input.Path);
                    }
                }
            }
        }

        Assert.NotEmpty(collection: read);
        Assert.Equal(
            actual: [.. read.Where(predicate: static path => !AffectedRevisionExport.DocumentTrees.Any(predicate: tree => (string.Equals(a: path, b: tree, comparisonType: StringComparison.Ordinal) || path.StartsWith(comparisonType: StringComparison.Ordinal, value: (tree + "/")))))],
            expected: Array.Empty<string>()
        );
    }
}
