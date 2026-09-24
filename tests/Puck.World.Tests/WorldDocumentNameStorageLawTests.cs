using Puck.Storage;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Every place a document name becomes a stored location derives it from <see cref="WorldDocumentName"/>: an
/// owned world's cloud key and a basis link's key are the id's document file under their namespaces, and a hosted
/// reference names the id, never a file.</summary>
public sealed class WorldDocumentNameStorageLawTests {
    private static readonly Guid Container = Guid.Parse(input: "11111111-2222-3333-4444-555555555555");

    [Fact]
    public void AnOwnedWorldAndABasisLinkAreKeyedByTheIdsDocumentFile() {
        var id = SafeName.Parse(candidate: "amber");

        Assert.Equal(
            actual: WorldOwnedWorldSync.AddressFor(
                containerId: Container,
                id: id
            ).Key,
            expected: $"puck/worlds/{WorldDocumentName.For(id: id)}"
        );
        Assert.Equal(
            actual: WorldOwnedWorldSync.BasisAddressFor(
                containerId: Container,
                id: id
            ).Key,
            expected: $"puck/worlds/basis/{WorldDocumentName.For(id: id)}"
        );
        Assert.Equal(
            actual: WorldDocumentName.For(id: id),
            expected: "amber.world.json"
        );
    }
    [Fact]
    public void AHostedReferenceResolvesAnIdAndRefusesAFileByName() {
        var origin = new WorldHostedOrigin(
            owner: Container,
            store: new FakeObjectBlobStore(),
            target: AzureBlobObjectStorageTarget.FromConnectionStringOrServiceUri(value: "UseDevelopmentStorage=true"),
            world: SafeName.Parse(candidate: "hub")
        );

        Assert.True(
            condition: origin.TryResolveReference(
                document: "amber",
                reason: out var reason,
                sibling: out var sibling
            ),
            userMessage: reason
        );
        Assert.Equal(
            actual: sibling!.Identity,
            expected: $"owner/{Container:D}/amber"
        );
        Assert.False(condition: origin.TryResolveReference(
            document: "amber.world.json",
            reason: out reason,
            sibling: out _
        ));
        Assert.Contains(
            actualString: reason,
            expectedSubstring: "names a file"
        );
    }
}
