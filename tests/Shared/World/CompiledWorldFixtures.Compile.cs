using Puck.World.Server;
using Puck.World.Transpiler.Composition;
using Xunit;
using Puck.World.Machines;

namespace Puck.World.Testing;

internal static partial class CompiledWorldFixtures {
    internal static (WorldDefinitionAdmission Admission, CompiledWorldResolution Resolution, long Hits, long Derivations, WorldBootWork Work) Boot(string path, CompiledWorldCache cache) {
        var work = new WorldBootWork();
        var request = cache.For(
            catalogFingerprint: Catalog.CompositionFingerprint,
            documentPath: path
        );

        using (WorldBootWork.Attribute(work: work)) {
            Assert.True(
                condition: WorldDefinitionLoader.TryLoadFileForAdmission(
                    admission: out var admission,
                    contentHash: out _,
                    catalog: Catalog,
                    catalogFingerprint: Catalog.CompositionFingerprint,
                    compiled: request,
                    neighbours: new WorldFileNeighbourResolver(
                        baseDirectory: () => Path.GetDirectoryName(path: path)!,
                        catalog: Catalog,
                        catalogFingerprint: Catalog.CompositionFingerprint
                    ),
                    path: path,
                    reason: out var reason
                ),
                userMessage: reason
            );

            return (admission!, request.Resolution!, work.Read(kind: WorldBootWork.CompiledHits), work.Read(kind: WorldBootWork.ChunkDerivations), work);
        }
    }
    internal static byte[] Compile(string path, string? instanceIdentity = null, string? catalogFingerprint = null, CompiledWorldChunks? chunks = null) {
        Assert.True(
            condition: WorldSourceLoader.TryReadAuthored(
                authored: out var authored,
                catalog: Catalog,
                catalogFingerprint: Catalog.CompositionFingerprint,
                document: Puck.Testing.ShippedWorldDocuments.Read(path: path),
                path: path,
                reason: out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: CompiledWorld.TryCompile(
                authored: authored,
                bytes: out var bytes,
                catalogFingerprint: (catalogFingerprint ?? Catalog.CompositionFingerprint),
                instanceIdentity: (instanceIdentity ?? WorldDefinitionLoader.BootInstanceName),
                reason: out reason,
                sourceName: path,
                chunks: chunks
            ),
            userMessage: reason
        );

        return bytes;
    }
    internal static ulong HashAfter(WorldDefinition definition, int ticks) {
        using var fixture = Fixtures.FreshServer(definition: definition);

        for (var tick = 0; (tick < ticks); tick++) {
            fixture.Step();
        }

        return WorldStateHashComposition.HashAuthoritative(
            server: fixture.Server,
            tick: ((ulong)ticks)
        );
    }

    internal static readonly WorldMachineCatalog Catalog = TestMachines.Catalog();
}
