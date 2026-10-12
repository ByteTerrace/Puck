using Puck.Cli.Affected;
using Puck.Assets;
using Puck.World;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.Cli.Gate.Tests;

/// <summary>
/// Laws for <see cref="AffectedRevisionExport"/>: an export holds exactly the named paths the revision recorded and
/// nothing else of the repository; a git run that outlasts the export's bound, or fails, refuses the export by name
/// instead of waiting on git; and on the real tree, every file a canary's world documents compose from lies in the
/// trees a revision's documents are exported from.
/// </summary>
public sealed class AffectedRevisionExportLawTests {
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
