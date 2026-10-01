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
/// traveler.</summary>
public sealed class WorldSiloCrossingRecoveryLawTests {
    private const int Slot = WorldBodiesLimits.LocalSeatCount;

    private sealed class FailingJournalStore(IObjectBlobStore inner) : IObjectBlobStore {
        private int m_fail;

        public void FailNextJournalWrite() => Interlocked.Exchange(location1: ref m_fail, value: 1);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid objectId, string keyPrefix, CancellationToken cancellationToken = default) =>
            inner.ListAsync(cancellationToken: cancellationToken, keyPrefix: keyPrefix, objectId: objectId, target: target);
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(address: address, cancellationToken: cancellationToken, target: target);
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            if (address.Key.Contains(comparisonType: StringComparison.Ordinal, value: "/journal/") && (Interlocked.Exchange(location1: ref m_fail, value: 0) == 1)) {
                return ValueTask.FromException<ObjectBlobWriteResult>(exception: new IOException(message: "journal write failed before publication"));
            }
            return inner.WriteAsync(address: address, cancellationToken: cancellationToken, content: content, ifMatchVersion: ifMatchVersion, mode: mode, target: target);
        }
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

    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    [Theory]
    public async Task ACrossingBetweenSiloRowsRecoversFromTheAuthorityStore(bool changeSpawnsAfterArrival, bool crossDuringCapture, bool failPrecedingMutation) {
        var travelerSlot = (changeSpawnsAfterArrival ? 0 : Slot);
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = Path.Combine(
            path1: directory.RootPath,
            path2: "world.key"
        );

        File.WriteAllBytes(
            keyFile,
            key.ExportPkcs8PrivateKey()
        );
        var owner = Guid.NewGuid();
        var source = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "rowa")
        );
        var destination = new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "rowb")
        );
        var store = new FailingJournalStore(inner: PuckStorageTestComposition.BuildStore());
        var backend = new WorldAuthorityBlobStore(
            store: store,
            target: new DirectoryObjectStorageTarget(directory.RootPath),
            timeProvider: new VirtualClock()
        );
        var definition = Fixtures.PeerPopulationDocument(networkPlayers: 1) with {
            HostRaw = Fixtures.StandardHost with {
                Authority = null,
                Listen = null,
                Presentation = WorldHostPresentation.None,
            },
        };

        foreach (var identity in new[] { source, destination }) {
            Assert.True(condition: (await backend.PublishDefinitionAsync(
                identity,
                definition,
                TestContext.Current.CancellationToken
            )).Ok);
        }

        WorldSiloWorldRow[] Rows() => [
            new(
                source.Owner,
                source.World,
                new(KeyFile: keyFile)
            ),
            new(
                destination.Owner,
                destination.World,
                new(KeyFile: keyFile)
            ),
        ];
        var original = Host(
            directory.RootPath,
            store,
            output,
            Rows()
        );
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
        if (crossDuringCapture) {
            // Order the race explicitly: the destination snapshot is empty, then ingress commits, then the
            // snapshot is queued for publication. That later arrival must remain in its journal suffix.
            original.CheckpointCaptureTap = row => {
                if (row.Name == "rowb") {
                    original.CheckpointCaptureTap = null;
                    original.Instances.DrainPendingTransfers();
                }
            };
            var destinationCheckpoint = original.CheckpointNowAsync(destination, TestContext.Current.CancellationToken);

            await PumpAsync(host: original, operation: destinationCheckpoint);
            Assert.True(condition: await destinationCheckpoint);
            var tail = await backend.LoadRecoveryAsync(destination, TestContext.Current.CancellationToken);

            Assert.Contains(collection: tail!.Value.Journal.Entries, filter: entry => (entry.Kind == WorldAuthorityJournalEntryKind.Crossing));
        } else {
            original.Instances.DrainPendingTransfers();
        }
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

        var replacement = Host(
            directory.RootPath,
            store,
            output,
            Rows()
        );
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
}
