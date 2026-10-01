using Puck.Testing;
using System.Security.Cryptography;
using System.Text.Json;
using Puck.Commands;
using Puck.Launcher;
using Puck.Storage;
using Puck.World.Protocol;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

/// <summary>A silo-hosted crossing recovers from the authority store alone. Two rows of one silo checkpoint a traveler at
/// the source, then cross it; each row's crossing records reach the store's journal under its activation fence. A
/// replacement silo activates both rows from the store: the source's checkpoint still holds the traveler and the
/// destination's holds none, and redoing the journal's crossing records leaves the traveler on the destination alone.
/// The record the replacement redoes is shown to be load-bearing by the checkpoint itself still holding the
/// traveler. A destination whose store cannot reconcile the root compare-and-swap of its arrival record answers the
/// commit uncertain: neither row embodies the traveler, the source keeps its doubt, and the replacement settles the
/// transfer from what the destination's journal holds.</summary>
public sealed class WorldSiloCrossingRecoveryLawTests {
    private const int Slot = WorldBodiesLimits.LocalSeatCount;

    // Fails one journal page write before publication, or loses one row's next root compare-and-swap answer: that
    // write lands or not as armed, then throws, and the store's reconciling read of the root fails too.
    private sealed class FailingJournalStore(IObjectBlobStore inner) : IObjectBlobStore {
        private int m_fail;
        private string? m_loseRootAnswer;
        private bool m_lostRootLands;
        private string? m_failRootRead;

        private static bool IsRoot(ObjectBlobAddress address, string? world) => ((world is not null) && address.Key.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: $"/{world}/authority/root"
        ));

        public void FailNextJournalWrite() => Interlocked.Exchange(location1: ref m_fail, value: 1);
        public void LoseNextRootAnswer(string world, bool lands) {
            m_lostRootLands = lands;
            Volatile.Write(location: ref m_loseRootAnswer, value: world);
        }
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken: cancellationToken, keyPrefix: keyPrefix, objectId: objectId, target: target);
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
            var failing = Volatile.Read(location: ref m_failRootRead);

            if (IsRoot(address: address, world: failing) && (Interlocked.CompareExchange(comparand: failing, location1: ref m_failRootRead, value: null) == failing)) {
                return ValueTask.FromException<ObjectBlobContent?>(exception: new IOException(message: "the root read failed"));
            }
            return inner.ReadAsync(address: address, cancellationToken: cancellationToken, target: target);
        }
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.Contains(comparisonType: StringComparison.Ordinal, value: "/journal/") && (Interlocked.Exchange(location1: ref m_fail, value: 0) == 1)) {
                throw new IOException(message: "journal write failed before publication");
            }
            var losing = Volatile.Read(location: ref m_loseRootAnswer);

            if (IsRoot(address: address, world: losing) && (Interlocked.CompareExchange(comparand: losing, location1: ref m_loseRootAnswer, value: null) == losing)) {
                if (m_lostRootLands) {
                    _ = await inner.WriteAsync(address: address, cancellationToken: cancellationToken, content: content, ifMatchVersion: ifMatchVersion, mode: mode, target: target);
                }
                Volatile.Write(location: ref m_failRootRead, value: losing);
                throw new IOException(message: "the root write's answer was lost");
            }
            return await inner.WriteAsync(address: address, cancellationToken: cancellationToken, content: content, ifMatchVersion: ifMatchVersion, mode: mode, target: target);
        }
    }
    // Two rows of one owner, rowa and rowb, published into one authority store; each silo built over it hosts both.
    private sealed class SiloPair : IDisposable {
        private readonly TemporaryDirectory m_directory = new();
        private readonly BufferedConsoleOutput m_output = new();

        private readonly string m_keyFile;

        private SiloPair() {
            using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

            m_keyFile = Path.Combine(
                path1: m_directory.RootPath,
                path2: "world.key"
            );
            File.WriteAllBytes(
                m_keyFile,
                key.ExportPkcs8PrivateKey()
            );
            var owner = Guid.NewGuid();

            Source = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: "rowa")
            );
            Destination = new WorldAuthorityIdentity(
                Owner: owner,
                World: SafeName.Parse(candidate: "rowb")
            );
            Store = new FailingJournalStore(inner: PuckStorageTestComposition.BuildStore());
            Backend = new WorldAuthorityBlobStore(
                store: Store,
                target: new DirectoryObjectStorageTarget(m_directory.RootPath),
                timeProvider: new VirtualClock()
            );
        }

        public WorldAuthorityBlobStore Backend { get; }
        public WorldAuthorityIdentity Destination { get; }
        public WorldAuthorityIdentity Source { get; }
        public FailingJournalStore Store { get; }

        public static async Task<SiloPair> ArrangeAsync() {
            var pair = new SiloPair();
            var definition = Fixtures.PeerPopulationDocument(networkPlayers: 1) with {
                HostRaw = Fixtures.StandardHost with {
                    Authority = null,
                    Listen = null,
                    Presentation = WorldHostPresentation.None,
                },
            };

            foreach (var identity in new[] { pair.Source, pair.Destination }) {
                Assert.True(condition: (await pair.Backend.PublishDefinitionAsync(
                    identity,
                    definition,
                    TestContext.Current.CancellationToken
                )).Ok);
            }
            return pair;
        }
        public void Dispose() {
            m_output.Dispose();
            m_directory.Dispose();
        }
        public WorldSiloHost NewHost() => Host(
            m_directory.RootPath,
            Store,
            m_output,
            [
                new(
                    Source.Owner,
                    Source.World,
                    new(KeyFile: m_keyFile)
                ),
                new(
                    Destination.Owner,
                    Destination.World,
                    new(KeyFile: m_keyFile)
                ),
            ]
        );
    }

    private static WorldSiloHost Host(string directory, IObjectBlobStore store, BufferedConsoleOutput output, WorldSiloWorldRow[] worlds) {
        var source = new TextCommandSource(new CommandRegistry(modules: []));

        return new(
            new(
                worlds,
                new(Budget: 2),
                new(
                    "directory",
                    JsonElement.Parse("{}")
                ),
                directory,
                new(Kind: "Localhost")
            ),
            store,
            new(
                source: () => source,
                tagging: new SiloConsoleTagging(output: output)
            ),
            new DirectoryObjectStorageTarget(directory),
            timeProvider: new VirtualClock()
        );
    }
    private static async Task PumpAsync(WorldSiloHost host, Task operation) {
        await WorldSiloHost.PumpActivationMailboxesAsync(
            cancellationToken: TestContext.Current.CancellationToken,
            hosts: [host],
            operation: operation
        );
        await operation;
    }
    private static async Task ActivateAsync(WorldSiloHost host, WorldAuthorityIdentity identity) {
        var activation = host.ActivateAsync(
            identity,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: host,
            operation: activation
        );
        Assert.True(condition: await activation);
    }
    private static int CountTraveler(WorldSiloHost host, WorldEntityAddress traveler) {
        var count = 0;

        foreach (var name in new[] { "rowa", "rowb" }) {
            Assert.True(condition: host.Instances.TryGet(
                instance: out var row,
                name: name
            ));
            for (var slot = 0; (slot < row!.Server.Population.Capacity); slot++) {
                if (row.Server.Population.ResolveIncarnation(
                    authority: row.Server.AuthorityIdentity,
                    index: slot
                ) == traveler) {
                    count++;
                }
            }
        }
        return count;
    }

    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Theory]
    public async Task ACrossingBetweenSiloRowsRecoversFromTheAuthorityStore(bool changeSpawnsAfterArrival, bool failPrecedingMutation) {
        var travelerSlot = (changeSpawnsAfterArrival ? 0 : Slot);
        using var pair = await SiloPair.ArrangeAsync();
        var store = pair.Store;
        var backend = pair.Backend;
        var source = pair.Source;
        var destination = pair.Destination;
        var original = pair.NewHost();
        using var originalInstances = original.Instances;

        await ActivateAsync(
            host: original,
            identity: source
        );
        await ActivateAsync(
            host: original,
            identity: destination
        );
        Assert.True(condition: original.Instances.TryGet(
            instance: out var rowA,
            name: "rowa"
        ));
        Assert.True(condition: rowA!.Server.ExecuteAuthorityOperation(operation: () => (changeSpawnsAfterArrival
            ? rowA.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: travelerSlot),
                Slot: travelerSlot,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted
            : rowA.Server.Population.TryAdmitRemotePeerAt(
            slot: Slot,
            source: IntentSource.Live,
            grantTemplates: [],
            identityDomain: "example.test",
            identitySubject: "traveler",
            admitted: out _,
            refusal: out _
        ))));
        var traveler = rowA.Server.Population.ResolveIncarnation(
            authority: rowA.Server.AuthorityIdentity,
            index: travelerSlot
        )!.Value;

        if (changeSpawnsAfterArrival) {
            Assert.True(condition: rowA.Server.ApplySession(request: new SessionRequest.Join(
                IdentityName: null,
                Principal: Principal.Seat(slot: 1),
                Slot: 1,
                WireProtocolKey: WorldProtocol.WireProtocolKey
            )).Accepted);
        }
        var checkpoint = original.CheckpointNowAsync(
            source,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: original,
            operation: checkpoint
        );
        Assert.True(condition: await checkpoint);

        if (failPrecedingMutation) {
            store.FailNextJournalWrite();
            Assert.True(condition: rowA.Server.TryApplyJournalTailMutation(
                mutation: new WorldMutation.SetSpawns(
                    Principal: Principal.Console,
                    Spawns: [.. rowA.Server.Definition.SpawnPoints.Select(selector: point => point with {
                        Position = new System.Numerics.Vector3(x: 100f, y: 0f, z: 0f),
                    })]
                ),
                tick: rowA.CompletedTicks,
                engineTick: rowA.Server.CompletedEngineTicks
            ));
        }

        _ = original.Instances.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "rowb"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "rowa",
            sourceSlot: travelerSlot
        );
        original.Instances.DrainPendingTransfers();
        if (failPrecedingMutation) {
            Assert.True(condition: rowA.Server.Population.IsActive(index: travelerSlot));
            Assert.Equal(expected: 0UL, actual: rowA.Server.CrossingSequence);
            var recovered = await backend.LoadRecoveryAsync(source, TestContext.Current.CancellationToken);

            Assert.Empty(collection: recovered!.Value.Journal.Entries);
            return;
        }
        Assert.Equal(
            expected: 1,
            actual: CountTraveler(
                host: original,
                traveler: traveler
            )
        );
        Assert.False(condition: rowA.Server.Population.IsActive(index: travelerSlot));

        Assert.True(condition: original.Instances.TryGet(instance: out var rowB, name: "rowb"));
        var arrivalPosition = rowB!.Server.Body(index: travelerSlot)!.FixedPosition;

        if (changeSpawnsAfterArrival) {
            // A spawn edit affects the next activation, while the traveler already landed at the previous pose.
            Assert.True(condition: rowB.Server.ExecuteAuthorityOperation(operation: () => rowB.Server.TryApplyJournalTailMutation(
                mutation: new WorldMutation.SetSpawns(
                    Principal: Principal.Console,
                    Spawns: [.. rowB.Server.Definition.SpawnPoints.Select(selector: point => point with {
                        Position = new System.Numerics.Vector3(x: 100f, y: 0f, z: 0f),
                    })]
                ),
                tick: rowB.CompletedTicks,
                engineTick: rowB.Server.CompletedEngineTicks
            )));
            var publish = original.PublishDefinitionAsync(destination, rowB.Server.Definition, TestContext.Current.CancellationToken);

            await PumpAsync(host: original, operation: publish);
            Assert.True(condition: (await publish).Ok);
            Assert.Equal(expected: arrivalPosition, actual: rowB.Server.Body(index: travelerSlot)!.FixedPosition);
        }

        // The source's durable checkpoint still holds the traveler: only the journal says it left.
        var latest = (await backend.LoadLatestAsync(
            source,
            TestContext.Current.CancellationToken
        ))!.Value;

        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: latest.Encoded.Span,
            checkpoint: out var image,
            reason: out var imageReason
        ), userMessage: imageReason);
        Assert.Contains(
            collection: image!.Population.Entries,
            filter: entry => (entry.Index == travelerSlot)
        );

        var replacement = pair.NewHost();
        using var replacementInstances = replacement.Instances;

        await ActivateAsync(
            host: replacement,
            identity: source
        );
        await ActivateAsync(
            host: replacement,
            identity: destination
        );
        replacement.Instances.DrainPendingTransfers();

        Assert.Equal(
            expected: 1,
            actual: CountTraveler(
                host: replacement,
                traveler: traveler
            )
        );
        Assert.True(condition: replacement.Instances.TryGet(
            instance: out var restoredB,
            name: "rowb"
        ));
        Assert.Contains(
            collection: Enumerable.Range(
                count: restoredB!.Server.Population.Capacity,
                start: 0
            ),
            filter: slot => (restoredB.Server.Population.ResolveIncarnation(
                authority: restoredB.Server.AuthorityIdentity,
                index: slot
            ) == traveler)
        );
        if (changeSpawnsAfterArrival) {
            Assert.Equal(expected: arrivalPosition, actual: restoredB.Server.Body(index: travelerSlot)!.FixedPosition);
        }
    }
    [InlineData(true)]
    [InlineData(false)]
    [Theory]
    public async Task AnUnreconciledArrivalPublicationResolvesFromTheDestinationsJournal(bool lands) {
        using var pair = await SiloPair.ArrangeAsync();
        var original = pair.NewHost();
        using var originalInstances = original.Instances;

        await ActivateAsync(
            host: original,
            identity: pair.Source
        );
        await ActivateAsync(
            host: original,
            identity: pair.Destination
        );
        Assert.True(condition: original.Instances.TryGet(
            instance: out var rowA,
            name: "rowa"
        ));
        Assert.True(condition: original.Instances.TryGet(
            instance: out var rowB,
            name: "rowb"
        ));
        Assert.True(condition: rowA!.Server.ExecuteAuthorityOperation(operation: () => rowA.Server.Population.TryAdmitRemotePeerAt(
            admitted: out _,
            grantTemplates: [],
            identityDomain: "example.test",
            identitySubject: "traveler",
            refusal: out _,
            slot: Slot,
            source: IntentSource.Live
        )));
        var traveler = rowA.Server.Population.ResolveIncarnation(
            authority: rowA.Server.AuthorityIdentity,
            index: Slot
        )!.Value;
        var checkpoint = original.CheckpointNowAsync(
            pair.Source,
            TestContext.Current.CancellationToken
        );

        await PumpAsync(
            host: original,
            operation: checkpoint
        );
        Assert.True(condition: await checkpoint);

        pair.Store.LoseNextRootAnswer(
            lands: lands,
            world: "rowb"
        );
        var transferId = original.Instances.EnqueueTransfer(
            actingPrincipal: Principal.Console,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "rowb"),
            scope: WorldInstanceHost.TransferScope.Body,
            sourceInstance: "rowa",
            sourceSlot: Slot
        );

        original.Instances.DrainPendingTransfers();
        original.Instances.DrainPendingTransfers();
        Assert.Equal(
            expected: 0,
            actual: CountTraveler(
                host: original,
                traveler: traveler
            )
        );
        Assert.Equal(
            expected: WorldTransferStatus.Uncertain,
            actual: rowB!.Server.TransferStatus(
                sourceAuthority: rowA.Server.AuthorityIdentity,
                transferId: transferId
            )
        );
        Assert.Single(collection: original.Instances.CaptureRow(row: rowA).InDoubtTransfers);

        var journal = await pair.Backend.LoadRecoveryAsync(
            pair.Destination,
            TestContext.Current.CancellationToken
        );

        Assert.Equal(
            expected: lands,
            actual: journal!.Value.Journal.Entries.Any(predicate: static entry => (entry.Kind == WorldAuthorityJournalEntryKind.Crossing))
        );

        var replacement = pair.NewHost();
        using var replacementInstances = replacement.Instances;

        await ActivateAsync(
            host: replacement,
            identity: pair.Source
        );
        await ActivateAsync(
            host: replacement,
            identity: pair.Destination
        );
        replacement.Instances.DrainPendingTransfers();
        Assert.Equal(
            expected: 1,
            actual: CountTraveler(
                host: replacement,
                traveler: traveler
            )
        );
        Assert.True(condition: replacement.Instances.TryGet(
            instance: out var restoredB,
            name: "rowb"
        ));
        Assert.Equal(
            expected: lands,
            actual: Enumerable.Range(
                count: restoredB!.Server.Population.Capacity,
                start: 0
            ).Any(predicate: slot => (restoredB.Server.Population.ResolveIncarnation(
                authority: restoredB.Server.AuthorityIdentity,
                index: slot
            ) == traveler))
        );
        Assert.True(condition: replacement.Instances.TryGet(
            instance: out var restoredA,
            name: "rowa"
        ));
        Assert.Empty(collection: replacement.Instances.CaptureRow(row: restoredA!).InDoubtTransfers);
    }
}
