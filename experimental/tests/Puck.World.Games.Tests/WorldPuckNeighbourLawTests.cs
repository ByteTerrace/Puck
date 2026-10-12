using Puck.Abstractions;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Games.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a document named by file or by name resolves to its <c>.puck</c> source when no
/// <c>.world.json</c> stands beside, and reads exactly as the document that source compiles to. A source wins over
/// a document file beside it; both file-form locators canonicalize to that source, so a return adopts the source
/// boot's authority. A neighbour, a crossing's destination and a session screen's destination name a document,
/// never a source format: the same document authored as <c>sky.puck</c> resolves through
/// <see cref="WorldFileNeighbourResolver"/> and loads through <see cref="WorldDefinitionLoader"/> as its
/// compiled <c>sky.world.json</c> does.
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

    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [Theory]
    public void ADocumentAndItsSourceNameOneCanonicalOrigin(bool sourceExists, bool documentExists) {
        var directory = (sourceExists
            ? m_source.RootPath
            : m_compiled.RootPath
        );
        var path = Path.Combine(
            path1: directory,
            path2: "sky.world.json"
        );
        var source = Path.Combine(
            path1: directory,
            path2: "sky.puck"
        );

        if (sourceExists && documentExists) {
            File.Copy(
                destFileName: path,
                sourceFileName: Path.Combine(
                    path1: m_compiled.RootPath,
                    path2: "sky.world.json"
                )
            );
        }

        Assert.True(condition: WorldFileOrigin.TryResolveCanonicalPath(
            path: path,
            resolved: out var resolved
        ));
        Assert.Equal(
            actual: PuckPaths.Normalize(path: resolved),
            expected: PuckPaths.Normalize(path: Path.GetFullPath(path: (sourceExists
                ? source
                : path
            )))
        );

        if (sourceExists) {
            Assert.True(condition: WorldFileOrigin.TryResolveCanonicalPath(
                path: source,
                resolved: out var sourceOrigin
            ));
            Assert.True(condition: WorldFileOrigin.IdentityEquals(
                left: resolved,
                right: sourceOrigin
            ));
        }
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
