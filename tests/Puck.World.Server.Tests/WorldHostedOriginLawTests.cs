using Puck.World.Protocol;
using Puck.Testing;
using System.Numerics;

using Xunit;

namespace Puck.World.Server.Tests;

/// <summary>Proves the hosted-origin read path end to end: five quilt-shaped documents published to a directory
/// store resolve each other's <c>references[]</c> by id through <see cref="WorldStorageNeighbourResolver"/>'s
/// hosted arm, and each one loads and validates its own adjacency claims through
/// <see cref="WorldHostedOrigin.TryLoad"/> against that same resolver.</summary>
public sealed class WorldHostedOriginLawTests {
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
                new WorldAdjacency(
                SafeName.Parse(candidate: "nwNeEdge"),
                "toNe",
                "neNwEdge",
                Boundary(yaw: 90f)
            ),
                new WorldAdjacency(
                SafeName.Parse(candidate: "nwSwEdge"),
                "toSw",
                "swNwEdge",
                Boundary(yaw: 180f)
            ),
                new WorldAdjacency(
                SafeName.Parse(candidate: "nwIslandEdge"),
                "toIsland",
                "islandNwEdge",
                Boundary(yaw: -90f)
            ),
            ],
            Destinations = [
                Destination(
                name: "toNe",
                referenceName: "toNe"
            ),
                Destination(
                name: "toSw",
                referenceName: "toSw"
            ),
                Destination(
                name: "toIsland",
                referenceName: "toIsland"
            ),
            ],
            References = [
                Reference(
                document: "quilt-ne",
                name: "toNe"
            ),
                Reference(
                document: "quilt-sw",
                name: "toSw"
            ),
                Reference(
                document: "quilt-island",
                name: "toIsland"
            ),
            ],
        };
        var ne = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(
                SafeName.Parse(candidate: "neNwEdge"),
                "toNw",
                "nwNeEdge",
                Boundary(yaw: -90f)
            ),
                new WorldAdjacency(
                SafeName.Parse(candidate: "neSeEdge"),
                "toSe",
                "seNeEdge",
                Boundary(yaw: 180f)
            ),
            ],
            Destinations = [
                Destination(
                name: "toNw",
                referenceName: "toNw"
            ),
                Destination(
                name: "toSe",
                referenceName: "toSe"
            ),
            ],
            References = [
                Reference(
                document: "quilt-nw",
                name: "toNw"
            ),
                Reference(
                document: "quilt-se",
                name: "toSe"
            ),
            ],
        };
        var se = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(
                SafeName.Parse(candidate: "seNeEdge"),
                "toNe",
                "neSeEdge",
                Boundary(yaw: 0f)
            ),
                new WorldAdjacency(
                SafeName.Parse(candidate: "seSwEdge"),
                "toSw",
                "swSeEdge",
                Boundary(yaw: 180f)
            ),
            ],
            Destinations = [
                Destination(
                name: "toNe",
                referenceName: "toNe"
            ),
                Destination(
                name: "toSw",
                referenceName: "toSw"
            ),
            ],
            References = [
                Reference(
                document: "quilt-ne",
                name: "toNe"
            ),
                Reference(
                document: "quilt-sw",
                name: "toSw"
            ),
            ],
        };
        var sw = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(
                SafeName.Parse(candidate: "swSeEdge"),
                "toSe",
                "seSwEdge",
                Boundary(yaw: 0f)
            ),
                new WorldAdjacency(
                SafeName.Parse(candidate: "swNwEdge"),
                "toNw",
                "nwSwEdge",
                Boundary(yaw: 0f)
            ),
            ],
            Destinations = [
                Destination(
                name: "toSe",
                referenceName: "toSe"
            ),
                Destination(
                name: "toNw",
                referenceName: "toNw"
            ),
            ],
            References = [
                Reference(
                document: "quilt-se",
                name: "toSe"
            ),
                Reference(
                document: "quilt-nw",
                name: "toNw"
            ),
            ],
        };
        var island = Fixtures.BuildDocument() with {
            Adjacencies = [
                new WorldAdjacency(
                SafeName.Parse(candidate: "islandNwEdge"),
                "toNw",
                "nwIslandEdge",
                Boundary(yaw: 90f)
            ),
            ],
            Destinations = [
                Destination(
                name: "toNw",
                referenceName: "toNw"
            ),
            ],
            References = [
                Reference(
                document: "quilt-nw",
                name: "toNw"
            ),
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
    public async Task AsyncLoaderYieldsWhileNeighbourReadsArePendingAndStillProvesAdjacency() {
        var quilt = BuildQuilt();
        var gate = new TaskCompletionSource(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;

        async ValueTask<WorldNeighbourResolution> Resolve(string document, CancellationToken token) {
            reads++;
            await gate.Task.WaitAsync(cancellationToken: token);
            Assert.True(
                condition: WorldCounterpartAttestation.TryCompose(
                    quilt[document],
                    document,
                    out var attestation,
                    out var reason
                ),
                userMessage: reason
            );
            return WorldNeighbourResolution.Attested(attestation: attestation!);
        }
        var load = WorldDefinitionLoader.LoadAsync(
            WorldDefinitionSerialization.Serialize(definition: quilt["quilt-nw"]),
            "quilt-nw",
            "quilt-nw",
            Resolve,
            TestContext.Current.CancellationToken
        );

        Assert.False(condition: load.IsCompleted);
        Assert.Equal(
            actual: reads,
            expected: 1
        );
        gate.SetResult();
        var result = await load;

        Assert.NotNull(@object: result.Admission);
        Assert.Empty(value: result.Reason);
        Assert.Equal(
            actual: reads,
            expected: 3
        );
    }
    [Fact]
    public async Task UncheckpointedReactivationKeepsItsSeedAndRetiresItsOpeningStream() {
        using var directory = new TemporaryDirectory();
        var target = WorldAuthorityBlobStore.DirectoryTarget(directory.RootPath);
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("private-world"));
        var token = TestContext.Current.CancellationToken;
        WorldAuthorityBlobStore Open() => new(PuckStorageTestComposition.BuildStore(), target, timeProvider: new VirtualClock());
        var first = Open();
        var row = new WorldStateRow(CellName.Parse("dice"), CellKind.Int, Draw: new Draw(PrivateStream: true,
            Timing: DrawTiming.Event, Generator: new StateGenerator(Source: GeneratorSource.StreamDraw)));
        var document = Fixtures.BuildDocument().WithWorldState([row]) with { Rules = [] };
        Assert.True((await first.PublishDefinitionAsync(identity, document, token)).Ok);
        var missing = await Assert.ThrowsAsync<InvalidDataException>(async () => await first.LoadRecoveryAsync(identity, token));
        Assert.Contains("PrivateDrawSeedMissing", missing.Message);
        Assert.NotNull(await first.AcquireActivationAsync(identity, token));
        var live = (await first.LoadRecoveryAsync(identity, token))!.Value;
        var second = Open();
        Assert.NotNull(await second.AcquireActivationAsync(identity, token));
        var recovered = (await second.LoadRecoveryAsync(identity, token))!.Value;
        var stream = live.Definition!.AuthoredState[0].Draw!.Secret!;
        var resumed = recovered.Definition!.AuthoredState[0].Draw!.Secret!;
        Assert.NotNull(live.DrawKeys!.SerializeAuthorityState());
        Assert.Equal(live.DrawKeys.SerializeAuthorityState(), recovered.DrawKeys!.SerializeAuthorityState());
        Assert.NotEqual(live.DrawKeys.SerializeAuthorityState(), WorldPrivateDrawKeys.Create().SerializeAuthorityState());
        Assert.True(recovered.DrawKeys.RootGeneration > live.DrawKeys.RootGeneration);
        Assert.NotEqual(Enumerable.Range(0, 8).Select(i => stream.Sample(3, 5, i)), Enumerable.Range(0, 8).Select(i => resumed.Sample(3, 5, i)));
    }

    [Fact]
    public async Task RecoveryRekeysAndQualificationCannotStepProductionDraws() {
        using var directory = new TemporaryDirectory();
        var identity = new WorldAuthorityIdentity(Guid.NewGuid(), SafeName.Parse("private-world"));
        var token = TestContext.Current.CancellationToken;
        var blobs = PuckStorageTestComposition.BuildStore();
        WorldAuthorityBlobStore Store(string path) => new(blobs, WorldAuthorityBlobStore.DirectoryTarget(directory.PathOf(path)));
        var source = Store("source");
        var qualification = Store("qualification");
        var fence = (await source.AcquireActivationAsync(identity, token))!.Value;
        var document = Fixtures.BuildDocument().WithWorldState([new WorldStateRow(CellName.Parse("dice"), CellKind.Int,
            Draw: new Draw(PrivateStream: true, Timing: DrawTiming.Event, Generator: new StateGenerator(Source: GeneratorSource.StreamDraw)))]) with { Rules = [] };
        Assert.True((await source.PublishDefinitionAsync(identity, document, token, fence)).Ok);
        var live = (await source.LoadRecoveryAsync(identity, token))!.Value;
        using var running = Fixtures.FreshServer(live.Definition!, drawKeys: live.DrawKeys);
        Assert.True(running.Server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var checkpoint, out var reason), reason);
        var bytes = WorldAuthorityCheckpointCodec.Encode(checkpoint!);
        Assert.True((await source.WriteCheckpointAsync(identity, bytes, checkpoint!.Server.LastCompletedTick, token, fence)).Ok);
        var operation = Guid.NewGuid();
        var point = (await source.CaptureRecoveryRootAsync(identity, operation, token))!.Value;
        var receipts = await source.CaptureReceiptSnapshotAsync(identity, (await source.LoadRootAsync(identity, token))!.Value, token);
        Assert.True((await qualification.CreateReleaseFixtureAsync(identity, WorldDefinitionSerialization.Serialize(document), bytes, receipts, token)).Ok);
        var qualified = (await qualification.LoadRecoveryAsync(identity, token))!.Value;
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(qualified.Checkpoint!.Value.Encoded.Span, out var detached, out reason), reason);
        Assert.Equal(qualified.DrawKeys!.Identity, detached!.Server.DrawAuthority);
        Assert.NotEqual(live.DrawKeys!.Identity, qualified.DrawKeys.Identity);
        var mismatch = Assert.Throws<InvalidDataException>(() => running.Server.RestoreCheckpoint(detached));
        Assert.Contains("PrivateDrawAuthorityMismatch", mismatch.Message);
        Assert.True((await source.RestoreRecoveryRootAsync(identity, point.Pin, operation, fence, token)).Ok);
        var restored = (await source.LoadRecoveryAsync(identity, token))!.Value;
        Assert.Equal(live.DrawKeys.Identity, restored.DrawKeys!.Identity);
        Assert.True(restored.DrawKeys.RootGeneration > live.DrawKeys.RootGeneration);
        static uint[] Future(WorldDefinition d) => Enumerable.Range(0, 4).Select(i => d.AuthoredState[0].Draw!.Secret!.Sample(3, 5, i)).ToArray();
        Assert.NotEqual(Future(live.Definition!), Future(restored.Definition!));
        Assert.NotEqual(Future(live.Definition!), Future(qualified.Definition!));

        // The durable mutation tail covers one draw. The next draw was visible before the host crashed.
        var activeFence = (await source.AcquireActivationAsync(identity, token))!.Value;
        var active = (await source.LoadRecoveryAsync(identity, token))!.Value;
        Assert.True(WorldAuthorityCheckpointCodec.TryDecode(active.Checkpoint!.Value.Encoded.Span, out var start, out reason), reason);
        using var prior = Fixtures.FreshServer(active.Definition!, drawKeys: active.DrawKeys);
        prior.Server.RestoreCheckpoint(start!);
        static long Deal(WorldFixture f) {
            f.Server.EnqueueMutation(new WorldMutation.Generate(Puck.Commands.Principal.Console, "dice"));
            f.Step();
            return f.Server.ExportDefinition().AuthoredState[0].Cells![0].Value.Raw;
        }
        WorldJournalEntry journaled = default;
        prior.MutationJournalTap = entry => journaled = entry;
        Assert.Throws<InvalidOperationException>(() => prior.Server.SetMutationJournalTap(WorldPrivateDrawKeys.Create(), null));
        var committed = Deal(prior);
        Assert.True(WorldAuthorityCheckpointCodec.TryEncodeJournalMutation(journaled, out var tail, out reason), reason);
        Assert.Equal(WorldAuthorityJournalEntryKind.PrivateMutation, tail.Kind);
        Assert.True((await source.AppendJournalAsync(identity, tail, token, activeFence)).Ok);
        var discarded = Deal(prior);
        Assert.NotNull(await source.AcquireActivationAsync(identity, token));
        var restarted = (await source.LoadRecoveryAsync(identity, token))!.Value;
        Assert.True(restarted.DrawKeys!.RootGeneration > active.DrawKeys!.RootGeneration);
        using var successor = Fixtures.FreshServer(restarted.Definition!, drawKeys: restarted.DrawKeys);
        successor.Server.RestoreCheckpoint(start!);
        Assert.True(WorldAuthorityCheckpointCodec.TryDecodeJournalMutation(Assert.Single(restarted.Journal.Entries), out var recorded, out reason), reason);
        Assert.True(successor.Server.TryRecoverJournalMutation(recorded, restarted.DrawKeys));
        Assert.Equal(committed, successor.Server.ExportDefinition().AuthoredState[0].Cells![0].Value.Raw);
        Assert.NotEqual(discarded, Deal(successor));
    }

    [Fact]
    public async Task FiveQuiltDocuments_PublishedToADirectoryStore_ResolveByIdAndValidate() {
        using var directory = new TemporaryDirectory();

        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath);
        var store = PuckStorageTestComposition.BuildStore();
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: target,
            timeProvider: new VirtualClock()
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

            Assert.True(
                condition: outcome.Ok,
                userMessage: outcome.Detail
            );
        }

        var resolver = new WorldStorageNeighbourResolver(
            containerId: owner,
            @namespace: WorldStorageNamespace.Hosted,
            store: store,
            target: target,
            timeProvider: new VirtualClock()
        );

        // Every reference resolves BY ID to exactly the neighbour that was published under it.
        foreach (var (id, definition) in quilt) {
            foreach (var reference in (definition.References ?? [])) {
                var resolution = resolver.Resolve(document: reference.NeighbourKey);

                Assert.Equal(
                    actual: resolution.Kind,
                    expected: WorldNeighbourResolutionKind.Attested
                );

                var expectedId = reference.NeighbourKey;

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
                world: SafeName.Parse(candidate: id),
                timeProvider: new VirtualClock()
            );

            Assert.True(
                condition: origin.TryLoad(
                    definition: out var loaded,
                    instanceIdentity: id,
                    reason: out var reason
                ),
                userMessage: $"'{id}': {reason}"
            );
            using var hosted = Fixtures.FreshServer(loaded);
            var recipient = new WorldRecipientView(() => hosted.Server.Definition, () => hosted.Server.Arena,
                Puck.Commands.Principal.Seat(0), WorldDisclosureTier.Replica, () => true, new WorldDisclosureImages(),
                hosted.Server.AuthorityIdentity, () => hosted.Server.DeliveryTime, () => false);
            Assert.Equal(loaded!.DocumentId, recipient.Definition.DocumentId);
        }
    }
    [Fact]
    public async Task NeighbourSeamReadsDoNotRequireUnrelatedHostSettingsToValidate() {
        using var directory = new TemporaryDirectory();
        var target = WorldAuthorityBlobStore.DirectoryTarget(rootPath: directory.RootPath);
        var store = PuckStorageTestComposition.BuildStore();
        var owner = Guid.NewGuid();
        var definition = BuildQuilt()["quilt-island"] with { HostRaw = Fixtures.StandardHost with { Width = -1 } };

        Assert.False(condition: WorldDefinitionValidator.TryValidateLocally(
            definition: definition,
            reason: out _
        ));
        Assert.True(condition: (await new WorldAuthorityBlobStore(
            store: store,
            target: target,
            timeProvider: new VirtualClock()
        ).PublishDefinitionAsync(
            new(
                Owner: owner,
                World: SafeName.Parse(candidate: "quilt-island")
            ),
            definition,
            TestContext.Current.CancellationToken
        )).Ok);
        var resolver = new WorldStorageNeighbourResolver(
            containerId: owner,
            @namespace: WorldStorageNamespace.Hosted,
            store: store,
            target: target,
            timeProvider: new VirtualClock()
        );

        Assert.Equal(
            WorldNeighbourResolutionKind.Attested,
            resolver.Resolve(document: "quilt-island").Kind
        );
        var origin = new WorldHostedOrigin(
            owner,
            SafeName.Parse(candidate: "quilt-island"),
            store,
            target,
            timeProvider: new VirtualClock()
        );
        var loaded = await origin.LoadAsync(
            "quilt-island",
            TestContext.Current.CancellationToken
        );

        Assert.Null(@object: loaded.Definition);
        Assert.Contains(
            actualString: loaded.Reason,
            comparisonType: StringComparison.Ordinal,
            expectedSubstring: "host.width"
        );
    }
}
