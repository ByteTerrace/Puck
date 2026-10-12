using System.Text.Json.Nodes;
using Puck.Transpiler.Ast;
using Puck.Transpiler.Diagnostics;
using Puck.World.Transpiler;
using Xunit;

namespace Puck.State.Rebuild.Corpus;

/// <summary>The corpus inventory is generated from the corpus.</summary>
/// <remarks><see cref="TheCommittedInventoryIsTheCorpusCount"/> writes the fresh inventory beside the test assembly
/// (<see cref="Puck.Testing.TestRecords"/>) before comparing, and <c>puck baselines corpus-inventory</c> promotes that
/// copy over <see cref="CorpusInventory.CommittedPath"/>.</remarks>
public class CorpusInventoryTests {
    private static readonly Lazy<IReadOnlyList<CorpusInventoryRow>> Inventory = new(valueFactory: static () => CorpusInventory.Count(corpus: Load()));

    private static IEnumerable<(string Path, JsonObject Document, DocumentNode Source)> Load() {
        foreach (var path in CorpusSources.Paths()) {
            var absolute = RepositoryPaths.Resolve(relativePath: path);
            var text = File.ReadAllText(path: absolute);
            var diagnostics = new DiagnosticBag();
            var compiled = WorldCompiler.Compile(
                cancellationToken: TestContext.Current.CancellationToken,
                diagnostics: diagnostics,
                source: text,
                sourcePath: absolute
            );

            Assert.False(condition: diagnostics.HasErrors, userMessage: $"{path}:{Environment.NewLine}{diagnostics.FormatReport(text)}");
            Assert.NotNull(@object: compiled.Document);

            yield return (path, compiled.RequireJson(), compiled.Document);
        }
    }

    [Fact]
    public void TheCommittedInventoryIsTheCorpusCount() {
        var rendered = CorpusInventory.Render(rows: Inventory.Value);
        var committed = RepositoryPaths.Resolve(relativePath: CorpusInventory.CommittedPath);

        _ = Puck.Testing.TestRecords.Write(
            artifact: CorpusInventory.RecordArtifact,
            bytes: System.Text.Encoding.UTF8.GetBytes(s: rendered),
            fileName: Path.GetFileName(path: CorpusInventory.CommittedPath)
        );
        Assert.True(condition: File.Exists(path: committed), userMessage: $"{CorpusInventory.CommittedPath} is missing; record it with {CorpusInventory.RecordCommand}");
        Assert.Equal(actual: File.ReadAllText(path: committed), expected: rendered);
    }
}
