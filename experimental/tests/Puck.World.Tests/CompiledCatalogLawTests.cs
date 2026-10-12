using Puck.Testing;
using Xunit;
using static Puck.World.Testing.CompiledWorldFixtures;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: every shipped world the World's own build compiled into its Release catalog boots from its
/// compiled world, taking every chunk and deriving none. The catalog is the World build's output, which this suite's
/// reference to <c>Puck.World</c> builds, so the law sits beside it rather than with the compiled-world laws of
/// <c>Puck.World.Games.Tests</c>.
/// </summary>
[Collection(name: DocumentCompositionCollection.Name)]
public sealed class CompiledCatalogLawTests {
    [Fact]
    public void EveryShippedWorldTheBuildCompiledBootsFromItsCompiledWorld() {
        var catalog = RepositoryPaths.Resolve(relativePath: "src/Puck.World/bin/Release/net10.0/Assets/worlds");
        var compiled = Directory.EnumerateFiles(path: catalog, searchOption: SearchOption.AllDirectories, searchPattern: ("*" + CompiledWorld.Extension)).Order(comparer: StringComparer.Ordinal).ToArray();

        Assert.Contains(collection: compiled, filter: static file => file.EndsWith(comparisonType: StringComparison.Ordinal, value: $"puck{CompiledWorld.Extension}"));

        using var directory = new TemporaryDirectory();

        foreach (var file in compiled) {
            var document = (file[..^CompiledWorld.Extension.Length] + WorldDocumentName.DocumentSuffix);
            var boot = Boot(cache: new CompiledWorldCache(directory: directory.PathOf(name: Guid.NewGuid().ToString(format: "N"))), path: document);

            Assert.True(condition: ((boot.Hits == 1L) && (boot.Derivations == 0L)), userMessage: $"{document}: {boot.Resolution.Describe()}");
            Assert.Equal(expected: Path.GetFullPath(path: file), actual: boot.Resolution.LoadedFrom);
        }
    }
}
