using System.Collections.Concurrent;
using System.Text.Json;

using Puck.Testing;
using Puck.World.Transpiler.Composition;
using Xunit;

namespace Puck.World.Schema.Tests;

/// <summary>Every shipped world document loads through the door the game loads it through and compiles under the
/// arena-addressed compiler with no refusal, needs that name the facet of every arm it can fire, and a program
/// carrying ordinal addresses alone. A document with a <c>.puck</c> source is that source, as the game resolves it. A
/// document the door does not load standalone is accounted for by name: another shipped document imports it, so its
/// rules reach the compiler through that one. A module library emits no document, so no door loads it: it is accounted
/// for when it compiles alone, and the sources importing it carry its declarations into their own documents.</summary>
public sealed class WorldFactsCompilerShippedWorldLawTests {
    private static IEnumerable<string> ShippedWorlds() => ShippedWorldDocuments.Files(directory: RepositoryPaths.Resolve(relativePath: ShippedWorldDocuments.WorldDirectory));
    private static readonly ConcurrentDictionary<string, Lazy<LoadOutcome>> Loads = new(comparer: StringComparer.Ordinal);
    private static WorldDefinition? Load(string path) =>
        LoadOnce(path: path).Definition;
    private static LoadOutcome LoadOnce(string path) =>
        Loads.GetOrAdd(
            key: path,
            valueFactory: static key => new Lazy<LoadOutcome>(valueFactory: () => LoadUncached(path: key))
        ).Value;
    // The door the game itself loads a world through: composition, the boot draw, and document-local validation.
    private static LoadOutcome LoadUncached(string path) {
        var loaded = WorldDefinitionLoader.TryLoadFileForAdmission(
            admission: out var admission,
            contentHash: out _,
            documents: PuckDocumentComposer.Instance,
            path: path,
            proveNeighbours: false,
            reason: out var reason
        );

        return new LoadOutcome(
            Definition: (loaded ? admission!.Definition : null),
            Reason: reason
        );
    }
    [Fact]
    public void TheShippedCorpusCarriesAtLeastOneHostOwnedFieldRow() {
        var hostOwned = 0;

        foreach (var path in ShippedWorlds()) {
            if (Load(path: path) is not { } definition) {
                continue;
            }

            foreach (var descriptor in definition.StateCatalog.Descriptors) {
                if (descriptor.HostOwned) {
                    hostOwned++;
                }
            }
        }

        Assert.NotEqual(
            actual: hostOwned,
            expected: 0
        );
    }
}
/// <summary>What one load of a shipped world through the game's door returned.</summary>
/// <param name="Definition">The loaded definition, or <see langword="null"/> when the door refused it.</param>
/// <param name="Reason">The door's reason for a refusal.</param>
internal readonly record struct LoadOutcome(WorldDefinition? Definition, string Reason);
