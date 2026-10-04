using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a document named by file or by name resolves to its <c>.puck</c> source when no
/// <c>.world.json</c> stands beside, and reads exactly as the document that source compiles to. A neighbour, a
/// crossing's destination and a session screen's destination name a document, never a source format: the same
/// document authored as <c>sky.puck</c> resolves through <see cref="WorldFileNeighbourResolver"/>, loads through
/// <see cref="WorldDefinitionLoader"/> and names a canonical identity through <see cref="WorldFileOrigin"/> as
/// the compiled <c>sky.world.json</c> beside the same names does.
/// </summary>
public sealed class WorldPuckNeighbourLawTests : IDisposable {
    private const string Source = "tests/Puck.Counters/sky-still.puck";

    private readonly TemporaryDirectory m_compiled = new(bestEffortDelete: true, prefix: "puck-puck-neighbour-compiled-");
    private readonly TemporaryDirectory m_source = new(bestEffortDelete: true, prefix: "puck-puck-neighbour-source-");

    public WorldPuckNeighbourLawTests() {
        var path = RepositoryPaths.Resolve(relativePath: Source);

        File.Copy(
            destFileName: Path.Combine(
                path1: m_source.RootPath,
                path2: "sky.puck"
            ),
            sourceFileName: path
        );
        File.WriteAllBytes(
            bytes: ShippedWorldDocuments.Read(path: path),
            path: Path.Combine(
                path1: m_compiled.RootPath,
                path2: "sky.world.json"
            )
        );
    }

    private static byte[] Resolved(string directory) {
        var resolution = new WorldFileNeighbourResolver(baseDirectory: () => directory).Resolve(document: "sky");

        Assert.Equal(
            actual: resolution.Kind,
            expected: WorldNeighbourResolutionKind.Resolved
        );

        return WorldDefinitionSerialization.Serialize(definition: resolution.Definition!);
    }

    [Fact]
    public void ADocumentPathWithOnlyASourceCanonicalizesToItself() {
        var path = Path.Combine(
            path1: m_source.RootPath,
            path2: "sky.world.json"
        );

        Assert.True(condition: WorldFileOrigin.TryResolveCanonicalPath(
            path: path,
            resolved: out var resolved
        ));
        Assert.Equal(
            actual: resolved,
            expected: Path.GetFullPath(path: path)
        );
        Assert.False(condition: WorldFileOrigin.TryResolveCanonicalPath(
            path: Path.Combine(
                path1: m_source.RootPath,
                path2: "absent.world.json"
            ),
            resolved: out _
        ));
    }
    [Fact]
    public void ADocumentPathWithOnlyASourceLoadsAsItsCompiledDocumentDoes() {
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                definition: out var fromSource,
                path: Path.Combine(
                    path1: m_source.RootPath,
                    path2: "sky.world.json"
                ),
                reason: out var sourceReason
            ),
            userMessage: sourceReason
        );
        Assert.True(
            condition: WorldDefinitionLoader.TryLoadFile(
                definition: out var fromDocument,
                path: Path.Combine(
                    path1: m_compiled.RootPath,
                    path2: "sky.world.json"
                ),
                reason: out var documentReason
            ),
            userMessage: documentReason
        );
        Assert.True(condition: WorldDefinitionSerialization.Serialize(definition: fromSource!).AsSpan().SequenceEqual(other: WorldDefinitionSerialization.Serialize(definition: fromDocument!)));
    }
    [Fact]
    public void ANeighbourWithOnlyASourceResolvesAsItsCompiledDocumentDoes() => Assert.True(condition: Resolved(directory: m_source.RootPath).AsSpan().SequenceEqual(other: Resolved(directory: m_compiled.RootPath)));
    [Fact]
    public void ANameWithNeitherASourceNorADocumentIsRefusedByName() {
        var resolution = new WorldFileNeighbourResolver(baseDirectory: () => m_source.RootPath).Resolve(document: "absent");

        Assert.Equal(
            actual: resolution.Kind,
            expected: WorldNeighbourResolutionKind.Unavailable
        );
        Assert.Contains(
            actualString: resolution.Reason,
            expectedSubstring: "no local copy"
        );
    }
    /// <inheritdoc/>
    public void Dispose() {
        m_compiled.Dispose();
        m_source.Dispose();
    }
}
