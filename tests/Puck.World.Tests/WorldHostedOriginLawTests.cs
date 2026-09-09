using System.Numerics;

using Xunit;

using Puck.Storage;
using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>Proves the hosted-origin read path end to end: five quilt-shaped documents published to a directory
/// store resolve each other's <c>references[]</c> by id through <see cref="WorldStorageNeighbourResolver"/>'s
/// hosted arm, and each one loads and validates its own adjacency claims through
/// <see cref="WorldHostedOrigin.TryLoad"/> against that same resolver.</summary>
public sealed class WorldHostedOriginLawTests {
    [Fact]
    public async Task NeighbourSeamReadsDoNotRequireUnrelatedHostSettingsToValidate() {
        using var directory = new TempWorldDirectory();
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var store = PuckStorageTestComposition.BuildStore();
        var owner = Guid.NewGuid();
        var definition = BuildQuilt()["quilt-island"] with { HostRaw = Fixtures.StandardHost with { Width = -1 } };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(definition: definition, reason: out _));
        var address = WorldOwnedWorldSync.HostedAddressFor(owner, SafeName.Parse(candidate: "quilt-island"), "definition.json");

        await store.WriteAsync(target, address, WorldDefinitionSerialization.Serialize(definition: definition), ObjectBlobWriteMode.Overwrite, cancellationToken: TestContext.Current.CancellationToken);
        var resolver = new WorldStorageNeighbourResolver(containerId: owner, @namespace: WorldStorageNamespace.Hosted, store: store, target: target);

        Assert.Equal(WorldNeighbourResolutionKind.Attested, resolver.Resolve(document: "quilt-island.world.json").Kind);
        var origin = new WorldHostedOrigin(owner, SafeName.Parse(candidate: "quilt-island"), store, target);
        var loaded = await origin.LoadAsync("quilt-island", TestContext.Current.CancellationToken);

        Assert.Null(loaded.Definition);
        Assert.Contains("host.width", loaded.Reason, StringComparison.Ordinal);
    }
    [Fact]
    public async Task AsyncLoaderYieldsWhileNeighbourReadsArePendingAndStillProvesAdjacency() {
        var quilt = BuildQuilt();
        var gate = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;

        async ValueTask<WorldNeighbourResolution> Resolve(string document, CancellationToken token) {
            reads++;
            await gate.Task.WaitAsync(cancellationToken: token);
            Assert.True(condition: WorldCounterpartAttestation.TryCompose(quilt[document[..^WorldOwnedWorldFileName.Suffix.Length]], document, out var attestation, out var reason), userMessage: reason);
            return WorldNeighbourResolution.Attested(attestation: attestation!);
        }
        var load = WorldDefinitionLoader.LoadAsync(WorldDefinitionSerialization.Serialize(definition: quilt["quilt-nw"]), "quilt-nw", "quilt-nw", Resolve, TestContext.Current.CancellationToken);

        Assert.False(load.IsCompleted);
        Assert.Equal(actual: reads, expected: 1);
        gate.SetResult();
        var result = await load;

        Assert.NotNull(result.Definition);
        Assert.Empty(result.Reason);
        Assert.Equal(actual: reads, expected: 3);
    }

    private static WorldAdjacencyBoundary Boundary(float yaw) => new(
        Center: Vector3.Zero,
        Height: 8f,
        OutwardPitchDegrees: 0f,
        OutwardYawDegrees: yaw,
        Width: 8f
    );
    // A five-document ring of corners (nw-ne-se-sw-nw) plus an island spurring off nw — the nexus shape, built
    // in code rather than read from src/Puck.World/Assets (this project's base document is compiler-maintained;
    // see Fixtures' own remarks).
    private static Dictionary<string, WorldDefinition> BuildQuilt() {
        WorldReference Reference(string name, string document) => new(
            Name: SafeName.Parse(candidate: name),
            Document: document
        );
        WorldDestination Destination(string name, string referenceName) => new(
            Name: SafeName.Parse(candidate: name),
            Durability: WorldDestinationDurability.Persisted,
            Reference: referenceName,
            Scope: WorldDestinationScope.Global
        );

        var nw = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "nwNeEdge"), "toNe", "neNwEdge", Boundary(yaw: 90f)),
                new WorldAdjacency(SafeName.Parse(candidate: "nwSwEdge"), "toSw", "swNwEdge", Boundary(yaw: 180f)),
                new WorldAdjacency(SafeName.Parse(candidate: "nwIslandEdge"), "toIsland", "islandNwEdge", Boundary(yaw: -90f)),
            ],
            Destinations = [
                Destination(name: "toNe", referenceName: "toNe"),
                Destination(name: "toSw", referenceName: "toSw"),
                Destination(name: "toIsland", referenceName: "toIsland"),
            ],
            References = [
                Reference(document: "quilt-ne.world.json", name: "toNe"),
                Reference(document: "quilt-sw.world.json", name: "toSw"),
                Reference(document: "quilt-island.world.json", name: "toIsland"),
            ],
        };
        var ne = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "neNwEdge"), "toNw", "nwNeEdge", Boundary(yaw: -90f)),
                new WorldAdjacency(SafeName.Parse(candidate: "neSeEdge"), "toSe", "seNeEdge", Boundary(yaw: 180f)),
            ],
            Destinations = [
                Destination(name: "toNw", referenceName: "toNw"),
                Destination(name: "toSe", referenceName: "toSe"),
            ],
            References = [
                Reference(document: "quilt-nw.world.json", name: "toNw"),
                Reference(document: "quilt-se.world.json", name: "toSe"),
            ],
        };
        var se = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "seNeEdge"), "toNe", "neSeEdge", Boundary(yaw: 0f)),
                new WorldAdjacency(SafeName.Parse(candidate: "seSwEdge"), "toSw", "swSeEdge", Boundary(yaw: 180f)),
            ],
            Destinations = [
                Destination(name: "toNe", referenceName: "toNe"),
                Destination(name: "toSw", referenceName: "toSw"),
            ],
            References = [
                Reference(document: "quilt-ne.world.json", name: "toNe"),
                Reference(document: "quilt-sw.world.json", name: "toSw"),
            ],
        };
        var sw = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "swSeEdge"), "toSe", "seSwEdge", Boundary(yaw: 0f)),
                new WorldAdjacency(SafeName.Parse(candidate: "swNwEdge"), "toNw", "nwSwEdge", Boundary(yaw: 0f)),
            ],
            Destinations = [
                Destination(name: "toSe", referenceName: "toSe"),
                Destination(name: "toNw", referenceName: "toNw"),
            ],
            References = [
                Reference(document: "quilt-se.world.json", name: "toSe"),
                Reference(document: "quilt-nw.world.json", name: "toNw"),
            ],
        };
        var island = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(SafeName.Parse(candidate: "islandNwEdge"), "toNw", "nwIslandEdge", Boundary(yaw: 90f)),
            ],
            Destinations = [
                Destination(name: "toNw", referenceName: "toNw"),
            ],
            References = [
                Reference(document: "quilt-nw.world.json", name: "toNw"),
            ],
        };

        return new Dictionary<string, WorldDefinition>(comparer: StringComparer.Ordinal) {
            ["quilt-nw"] = nw,
            ["quilt-ne"] = ne,
            ["quilt-se"] = se,
            ["quilt-sw"] = sw,
            ["quilt-island"] = island,
        };
    }

    [Fact]
    public async Task FiveQuiltDocuments_PublishedToADirectoryStore_ResolveByIdAndValidate() {
        using var directory = new TempWorldDirectory();

        var target = new DirectoryObjectStorageTarget(rootPath: directory.RootPath);
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target
        );
        var owner = Guid.NewGuid();
        var cancellationToken = TestContext.Current.CancellationToken;
        var quilt = BuildQuilt();

        foreach (var (id, definition) in quilt) {
            var outcome = await backend.PublishDefinitionAsync(
                cancellationToken: cancellationToken,
                composed: definition,
                identity: new WorldAuthorityIdentity(
                    Owner: owner,
                    World: SafeName.Parse(candidate: id)
                )
            );

            Assert.True(condition: outcome.Ok, userMessage: outcome.Detail);
        }

        var resolver = new WorldStorageNeighbourResolver(
            containerId: owner,
            @namespace: WorldStorageNamespace.Hosted,
            store: store,
            target: target
        );

        // Every reference resolves BY ID to exactly the neighbour that was published under it.
        foreach (var (id, definition) in quilt) {
            foreach (var reference in (definition.References ?? [])) {
                var resolution = resolver.Resolve(document: reference.NeighbourKey);

                Assert.Equal(
                    actual: resolution.Kind,
                    expected: WorldNeighbourResolutionKind.Attested
                );

                var expectedId = reference.NeighbourKey[..^WorldOwnedWorldFileName.Suffix.Length];

                Assert.True(
                    condition: quilt.ContainsKey(key: expectedId),
                    userMessage: $"'{id}' names a reference '{reference.NeighbourKey}' outside the built quilt"
                );
            }
        }

        // Every document loads and validates its own adjacency claims through the hosted origin.
        foreach (var (id, _) in quilt) {
            var origin = new WorldHostedOrigin(
                owner: owner,
                store: store,
                target: target,
                world: SafeName.Parse(candidate: id)
            );

            Assert.True(
                condition: origin.TryLoad(
                    definition: out _,
                    instanceIdentity: id,
                    reason: out var reason
                ),
                userMessage: $"'{id}': {reason}"
            );
        }
    }
}
