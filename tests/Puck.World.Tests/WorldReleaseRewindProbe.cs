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

public sealed partial class WorldReleaseCutoverLawTests {
    // A focused development probe through actual hosted rows, capture, controller, storage and restart.
    // It does not stand in for running the packaged release pair or the complete official inventory.
    [Fact]
    public async Task LocalDeployRollbackRewindAndInterruptedResumePreserveTheirDistinctStateContracts() {
        using var directory = new TemporaryDirectory();
        using var output = new BufferedConsoleOutput();
        var owner = Guid.NewGuid();
        var blobs = new RewindInterruptionStore(inner: PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var groups = new WorldReleaseGroupStore(
            owner: owner,
            store: blobs,
            target: target
        );
        var authority = new WorldAuthorityBlobStore(
            store: blobs,
            target: target
        );
        var archive = new WorldReleaseArchive(
            blobs,
            target,
            owner
        );
        var points = new WorldReleaseFixtureArchive(
            owner: owner,
            store: blobs,
            target: target
        );
        var restores = new WorldReleaseRestore(
            blobs: blobs,
            owner: owner,
            target: target
        );
        var identities = new[] { new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "alpha")
        ), new WorldAuthorityIdentity(
            Owner: owner,
            World: SafeName.Parse(candidate: "beta")
        ) };
        using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes(
            "world.pk8",
            key.ExportPkcs8PrivateKey()
        );
        var initial = Fixtures.BuildDocument();
        var document = initial with {
            HostRaw = Fixtures.StandardHost with { Authority = null, Listen = null, Presentation = WorldHostPresentation.None },
            PopulationRaw = initial.Population with { CapacityRaw = (WorldBodiesLimits.LocalSeatCount + 1), NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        var bytes = WorldDefinitionSerialization.Serialize(definition: document);
        var definitionPin = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: bytes)));
        var package = Path.Combine(
            path1: directory.RootPath,
            path2: "package"
        );

        Directory.CreateDirectory(path: package);
        foreach (var identity in identities) {
            File.WriteAllBytes(
                Path.Combine(
                    path1: package,
                    path2: (identity.World.Value + ".world.json")
                ),
                bytes
            );
            Assert.True(condition: (await authority.PublishDefinitionAsync(
                identity,
                document,
                Token
            )).Ok);
        }
        var release = new WorldReleaseManifest {
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            Label = "rewind-probe",
            SourceRevision = new string(
            c: 'a',
            count: 40
        ),
            EngineImageDigest = ("sha256:" + new string(
            c: 'a',
            count: 64
        )),
            Definitions = identities.ToDictionary(
            row => $"{owner:D}/{row.World}",
            _ => definitionPin
        ),
            DefinitionFiles = identities.ToDictionary(
            row => $"{owner:D}/{row.World}",
            row => (row.World.Value + ".world.json")
        ),
            PersistenceContract = "puck.world.persistence.v1",
            PeerProtocolContract = "puck.world.peer.v1",
        };

        await archive.SaveAsync(
            release,
            package,
            Token
        );
        var nextDocument = document with { Metadata = new(Title: "Release B") };
        var nextBytes = WorldDefinitionSerialization.Serialize(definition: nextDocument);
        var nextPin = ("sha256/" + Convert.ToHexStringLower(inArray: SHA256.HashData(source: nextBytes)));
        var nextPackage = Directory.CreateDirectory(path: Path.Combine(
            path1: directory.RootPath,
            path2: "package-b"
        )).FullName;

        foreach (var identity in identities) {
            File.WriteAllBytes(
            Path.Combine(
                path1: nextPackage,
                path2: (identity.World.Value + ".world.json")
            ),
            nextBytes
        );
        }
        var nextRelease = release with {
            Label = "release-b",
            EngineImageDigest = ("sha256:" + new string(
            c: 'b',
            count: 64
        )),
            Definitions = identities.ToDictionary(
            row => $"{owner:D}/{row.World}",
            _ => nextPin
        ),
        };

        await archive.SaveAsync(
            nextRelease,
            nextPackage,
            Token
        );
        Required(outcome: await groups.CreateAsync(
            "primary",
            release.Identity,
            Token
        ));
        WorldSiloHost Host(string? releaseId = null, bool closed = true) {
            var routing = new SiloConsoleRouting(
                source: () => new TextCommandSource(new CommandRegistry([])),
                tagging: new SiloConsoleTagging(output: output)
            );
            var rows = identities.Select(selector: row => new WorldSiloWorldRow(
                owner,
                row.World,
                new(KeyFile: keyFile),
                true
            )).ToArray();

            return new(
                new WorldSiloDefinition(
                    rows,
                    new(Budget: 2),
                    new(
                        "directory",
                        JsonElement.Parse("{}")
                    ),
                    directory.RootPath,
                    new(Kind: "Localhost"),
                    Release: new(
                        "primary",
                        owner,
                        (releaseId ?? release.Identity)
                    ) { ClosedGroupRewind = closed }
                ),
                blobs,
                routing,
                target
            );
        }
        var source = Host();
        using var sourceInstances = source.Instances;

        await ActivateAllAsync(
            host: source,
            identities: identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: source)
        );
        var republishing = source.PublishManagedReleaseAdmissionAsync(Token);

        source.DrainActivationMailbox();
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await republishing.WaitAsync(cancellationToken: Token)
        );
        Tick(
            count: 3,
            host: source
        );
        var beforeDeploy = Ticks(
            host: source,
            identities: identities
        );
        var enforcingCapture = source.ExportReleaseFixtureAsync(
            Guid.NewGuid(),
            Token
        );

        await PumpAsync(
            host: source,
            operation: enforcingCapture
        );
        Assert.NotNull(@object: (await enforcingCapture).RewindBoundary);
        var deployed = Host(nextRelease.Identity);
        using var deployedInstances = deployed.Instances;

        async Task PrepareMetadataAsync(WorldReleaseGroupRecord state, WorldReleaseManifest from, WorldReleaseManifest to) {
            foreach (var identity in identities) {
                var publication = await authority.PrepareReleaseMetadataAsync(
                    identity,
                    state,
                    from,
                    to,
                    archive,
                    Token
                );

                Assert.True(
                    condition: publication.Ok,
                    userMessage: publication.Detail
                );
            }
        }
        var group = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        var deploy = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            Required(outcome: await groups.BeginAsync(
                group,
                Guid.NewGuid(),
                nextRelease.Identity,
                Token
            )),
            nextRelease,
            new LoopbackRuntime(
                new WorldSiloReleaseRuntime(
                    source,
                    deployed,
                    identities,
                    () => throw new InvalidOperationException()
                ),
                source,
                deployed,
                state => PrepareMetadataAsync(
                    from: release,
                    state: state,
                    to: nextRelease
                )
            ),
            Token
        );

        await PumpAllAsync(
            hosts: [source, deployed],
            operation: deploy
        );
        Assert.True(
            condition: (await deploy).Completed,
            userMessage: (await deploy).Detail
        );
        AssertTicks(
            expected: beforeDeploy,
            host: deployed,
            identities: identities
        );
        foreach (var identity in identities) {
            Assert.True(condition: deployed.Instances.TryGet(
                identity.World.Value,
                out var row
            ));
            Assert.Equal(
                "Release B",
                row!.Server.Definition.Metadata!.Title
            );
        }
        Tick(
            count: 7,
            host: deployed
        );
        var beforeRollback = Ticks(
            host: deployed,
            identities: identities
        );
        var rolledBack = Host();
        using var rolledBackInstances = rolledBack.Instances;

        group = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        var rollback = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            Required(outcome: await groups.BeginRollbackAsync(
                group,
                Guid.NewGuid(),
                Token
            )),
            release,
            new LoopbackRuntime(
                new WorldSiloReleaseRuntime(
                    deployed,
                    rolledBack,
                    identities,
                    () => throw new InvalidOperationException()
                ),
                deployed,
                rolledBack,
                state => PrepareMetadataAsync(
                    from: nextRelease,
                    state: state,
                    to: release
                )
            ),
            Token
        );

        await PumpAllAsync(
            hosts: [deployed, rolledBack],
            operation: rollback
        );
        Assert.True(
            condition: (await rollback).Completed,
            userMessage: (await rollback).Detail
        );
        AssertTicks(
            expected: beforeRollback,
            host: rolledBack,
            identities: identities
        );
        foreach (var identity in identities) {
            Assert.True(condition: rolledBack.Instances.TryGet(
                identity.World.Value,
                out var row
            ));
            Assert.Equal(
                document.Metadata,
                row!.Server.Definition.Metadata
            );
        }
        source = rolledBack;
        Assert.True(condition: source.Instances.TryGet(
            instance: out var alpha,
            name: "alpha"
        ));
        Assert.True(condition: source.Instances.TryGet(
            instance: out var beta,
            name: "beta"
        ));
        const int PeerSlot = WorldBodiesLimits.LocalSeatCount;

        Assert.True(condition: alpha!.Server.ExecuteAuthorityOperation(operation: () => alpha.Server.Population.TryAdmitRemotePeerAt(
            PeerSlot,
            IntentSource.Live,
            [],
            "probe",
            "player",
            out _,
            out _
        )));
        Tick(
            count: 5,
            host: source
        );
        var capturing = source.ExportReleaseFixtureAsync(
            Guid.NewGuid(),
            Token
        );

        await PumpAsync(
            host: source,
            operation: capturing
        );
        var point = await capturing;

        Assert.NotNull(@object: point.RewindBoundary);
        Assert.NotNull(value: point.CapturedAt);
        var expected = Ticks(
            host: source,
            identities: identities
        );
        var rootsAtPoint = new Dictionary<string, WorldAuthorityRoot>();

        foreach (var identity in identities) {
            rootsAtPoint[identity.World.Value] = (await authority.LoadRootAsync(
            identity,
            Token
        ))!.Value.Root;
        }
        var preview = await restores.InspectAsync(
            group: "primary",
            pointId: point.RequestId,
            token: Token
        );

        Assert.Equal(
            point.Identity,
            preview.Point.Identity
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => restores.BeginAsync(
            "primary",
            point.RequestId,
            point.Identity,
            Guid.NewGuid(),
            false,
            Token
        ));
        Tick(
            count: 8,
            host: source
        );
        Assert.All(
            Ticks(
                host: source,
                identities: identities
            ),
            row => Assert.True(condition: (row.Value > expected[row.Key]))
        );
        var externalReservation = beta!.Server.ReserveTransfer(request: new(
            Border: string.Empty,
            BorderCapacity: null,
            DeadlineSourceTick: 60,
            Members: [],
            PartyAllOrNothing: true,
            PeerAdmission: true,
            SourceAuthority: "external",
            SourceRateHz: 60,
            SourceTick: 0,
            TransferId: 99
        ));

        Assert.False(condition: externalReservation.Accepted);
        Assert.Contains(
            "closed rewind group",
            externalReservation.Reason
        );
        Assert.False(condition: beta.Server.CommitTransfer(
            members: [],
            reason: out var externalReason,
            sourceAuthority: "external",
            transferId: 99
        ));
        Assert.Contains(
            actualString: externalReason,
            expectedSubstring: "closed rewind group"
        );
        using (var closedNetwork = new WorldPeerNetwork(allowOutbound: false)) {
            await Assert.ThrowsAsync<InvalidOperationException>(testCode: async () => await closedNetwork.ConnectAsync(
                new System.Net.IPEndPoint(
                    address: System.Net.IPAddress.Loopback,
                    port: 4433
                ),
                Token
            ));
        }
        _ = source.Instances.EnqueueTransfer(
            sourceInstance: "alpha",
            scope: WorldInstanceHost.TransferScope.Body,
            sourceSlot: PeerSlot,
            destination: WorldInstanceHost.TransferDestination.Remote(
                authority: "external",
                documentPath: "beta.world.json",
                name: "beta"
            ),
            actingPrincipal: Principal.Console
        );
        source.Instances.DrainPendingTransfers();
        Assert.True(condition: alpha.Server.Population.IsActive(index: PeerSlot));
        Assert.False(condition: beta.Server.Population.IsActive(index: PeerSlot));
        _ = source.Instances.EnqueueTransfer(
            sourceInstance: "alpha",
            scope: WorldInstanceHost.TransferScope.Body,
            sourceSlot: PeerSlot,
            destination: WorldInstanceHost.TransferDestination.Existing(name: "beta"),
            actingPrincipal: Principal.Console
        );
        source.Instances.DrainPendingTransfers();
        Assert.False(condition: alpha.Server.Population.IsActive(index: PeerSlot));
        Assert.True(condition: beta!.Server.Population.IsActive(index: PeerSlot));

        // A receipt created after the point must still reject duplicate/conflicting operation IDs after rewind.
        var identity0 = identities[0];
        var root = (await authority.LoadRootAsync(
            identity0,
            Token
        ))!.Value;
        var lateReceipt = new WorldAuthorityOperationReceipt(
            Guid.NewGuid(),
            "probe",
            "payload",
            "refused",
            false,
            (root.Root.Sequence + 1),
            null
        );

        Assert.True(condition: (await authority.RecordReceiptAsync(
            identity0,
            lateReceipt,
            Token,
            new(
                root.Root.Epoch,
                root.Root.FenceToken,
                root.VersionToken
            )
        )).Ok);
        var operation = Guid.NewGuid();
        var begun = Required(outcome: await restores.BeginAsync(
            "primary",
            point.RequestId,
            point.Identity,
            operation,
            true,
            Token
        ));
        var candidate = Host();
        using var candidateInstances = candidate.Instances;
        var runtime = new LoopbackRuntime(
            new WorldSiloReleaseRuntime(
                source,
                candidate,
                identities,
                () => throw new InvalidOperationException(message: "successful restore must not compensate")
            ),
            source,
            candidate
        );

        blobs.InterruptAtTick = expected["alpha"];
        var restoring = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            begun,
            release,
            runtime,
            Token
        );

        await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => PumpAllAsync(
            hosts: [source, candidate],
            operation: restoring
        ));
        var interrupted = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Assert.Equal(
            WorldReleaseOperationPhase.Activate,
            interrupted.Record.PendingPhase
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Closed,
            interrupted.Record.Admission
        );
        Assert.False(condition: candidate.ReleaseAdmissionOpen);
        Assert.Equal(
            expected["alpha"],
            (await authority.LoadRootAsync(
                identities[0],
                Token
            ))!.Value.Root.CheckpointTick
        );
        Assert.Equal(
            WorldDefinitionFileSource.ComputeContentHash(content: (await points.ReadCheckpointAsync(
                point,
                "alpha",
                Token
            )).Span),
            (await authority.LoadRootAsync(
                identities[0],
                Token
            ))!.Value.Root.CheckpointHash
        );
        Assert.True(condition: ((await authority.LoadRootAsync(
            identities[1],
            Token
        ))!.Value.Root.CheckpointTick > expected["beta"]));
        var resumedCandidate = Host();
        using var resumedCandidateInstances = resumedCandidate.Instances;

        runtime = new LoopbackRuntime(
            new WorldSiloReleaseRuntime(
                source,
                resumedCandidate,
                identities,
                () => throw new InvalidOperationException(message: "resumed restore must not compensate")
            ),
            source,
            resumedCandidate
        );
        restoring = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            interrupted,
            release,
            runtime,
            Token
        );
        await PumpAllAsync(
            hosts: [source, resumedCandidate],
            operation: restoring
        );
        var result = await restoring;

        Assert.True(
            condition: result.Completed,
            userMessage: result.Detail
        );
        candidate = resumedCandidate;
        AssertTicks(
            expected: expected,
            host: candidate,
            identities: identities
        );
        foreach (var identity in identities) {
            Assert.True(condition: candidate.Instances.TryGet(
                identity.World.Value,
                out var restoredRow
            ));
            Assert.True(
                condition: restoredRow!.Server.TryCaptureCheckpoint(
                    candidate.Instances.CaptureRow(row: restoredRow),
                    out var checkpoint,
                    out var reason
                ),
                userMessage: reason
            );
            var expectedBytes = (await points.ReadCheckpointAsync(
                point,
                identity.World.Value,
                Token
            )).ToArray();

            Assert.True(
                condition: WorldAuthorityCheckpointCodec.TryDecode(
                    bytes: expectedBytes,
                    checkpoint: out var expectedCheckpoint,
                    reason: out reason
                ),
                userMessage: reason
            );
            // Activation deliberately parks disconnected humans. Check that exact, documented change, then
            // compare the entire checkpoint: no other population, topology or gameplay difference is allowed.
            if (identity.World.Value == "alpha") {
                var entries = expectedCheckpoint!.Population.Entries.ToArray();

                Assert.Single(collection: entries);
                Assert.False(condition: entries[0].Parked);
                Assert.Null(value: entries[0].ParkedUntilTick);
                entries[0] = entries[0] with { Parked = true, ParkedUntilTick = checked((((long)expected["alpha"]) + document.PopulationReconnectGraceTicks.Ticks)) };
                expectedCheckpoint = expectedCheckpoint with { Population = expectedCheckpoint.Population with { Entries = entries } };
            }
            Assert.Equal(
                WorldAuthorityCheckpointCodec.Encode(checkpoint: expectedCheckpoint!),
                WorldAuthorityCheckpointCodec.Encode(checkpoint: checkpoint!)
            );
        }
        Assert.True(condition: candidate.Instances.TryGet(
            instance: out var restoredAlpha,
            name: "alpha"
        ));
        Assert.True(condition: candidate.Instances.TryGet(
            instance: out var restoredBeta,
            name: "beta"
        ));
        Assert.True(condition: restoredAlpha!.Server.Population.IsActive(index: PeerSlot));
        Assert.False(condition: restoredBeta!.Server.Population.IsActive(index: PeerSlot));
        Assert.True(condition: restoredAlpha.Server.Population.IsParked(index: PeerSlot));
        Assert.True(condition: restoredAlpha.Server.ExecuteAuthorityOperation(operation: () => restoredAlpha.Server.Population.TryResumeParkedPeer(
            admitted: out _,
            identityDomain: "probe",
            identitySubject: "player"
        )));
        Assert.False(condition: restoredAlpha.Server.Population.IsParked(index: PeerSlot));
        Assert.Equal(
            lateReceipt,
            await authority.FindOperationReceiptAsync(
                identity0,
                lateReceipt.OperationId,
                Token
            )
        );
        Assert.True(condition: candidate.ReleaseAdmissionOpen);
        foreach (var identity in identities) {
            var after = (await authority.LoadRootAsync(
                identity,
                Token
            ))!.Value.Root;

            Assert.True(condition: (after.Epoch > rootsAtPoint[identity.World.Value].Epoch));
            Assert.Equal(
                point.RewindBoundary,
                after.RewindBoundary
            );
            Assert.True(condition: (after.CheckpointOrdinal > rootsAtPoint[identity.World.Value].CheckpointOrdinal));
            Assert.Equal(
                expected[identity.World.Value],
                after.CheckpointTick
            );
        }
        var durable = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Assert.Equal(
            point.Identity,
            durable.Record.RestorePoint!.Identity
        );
        Assert.Equal(
            release.Identity,
            durable.Record.ActiveRelease
        );
        Assert.Equal(
            nextRelease.Identity,
            durable.Record.PreviousRelease
        );
        Assert.True(condition: durable.Record.RollbackEligible);
        Tick(
            count: 3,
            host: candidate
        );
        var continued = Ticks(
            host: candidate,
            identities: identities
        );

        await PumpAsync(
            host: candidate,
            operation: candidate.DrainAsync(ct: Token)
        );
        var unsafeHost = Host(closed: false);
        using var unsafeInstances = unsafeHost.Instances;
        var unsafeActivation = unsafeHost.ActivateAsync(
            identity0,
            Token
        );

        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => PumpAsync(
            host: unsafeHost,
            operation: unsafeActivation
        ));
        var restarted = Host();
        using var restartedInstances = restarted.Instances;

        await ActivateAllAsync(
            host: restarted,
            identities: identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: restarted)
        );
        AssertTicks(
            expected: continued,
            host: restarted,
            identities: identities
        );
        Assert.Equal(
            lateReceipt,
            await authority.FindOperationReceiptAsync(
                identity0,
                lateReceipt.OperationId,
                Token
            )
        );
        // A completed-operation resume cannot apply the old selected checkpoint a second time.
        var resume = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            durable,
            release,
            new LoopbackRuntime(
                new WorldSiloReleaseRuntime(
                    source,
                    restarted,
                    identities,
                    () => throw new InvalidOperationException()
                ),
                source,
                restarted
            ),
            Token
        );

        await PumpAllAsync(
            hosts: [restarted],
            operation: resume
        );
        Assert.True(
            condition: (await resume).Completed,
            userMessage: (await resume).Detail
        );
        AssertTicks(
            expected: continued,
            host: restarted,
            identities: identities
        );

        // A rejected private rewind must recover the fresh pre-restore drain, never leave the selected old
        // point installed. Interrupt that recovery before activation, then reconstruct it from durable state.
        var rejectedCandidate = Host();
        using var rejectedCandidateInstances = rejectedCandidate.Instances;
        var recoveredSource = Host();
        using var recoveredSourceInstances = recoveredSource.Instances;
        var secondRestore = Required(outcome: await restores.BeginAsync(
            "primary",
            point.RequestId,
            point.Identity,
            Guid.NewGuid(),
            true,
            Token
        ));
        var rejecting = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            secondRestore,
            release,
            new FailureRuntime(inner: new WorldSiloReleaseRuntime(
                candidate: rejectedCandidate,
                identities: identities,
                recoveryFactory: () => recoveredSource,
                source: restarted
            )),
            Token
        );

        await PumpAllAsync(
            hosts: [restarted, rejectedCandidate, recoveredSource],
            operation: rejecting
        );
        Assert.False(condition: (await rejecting).Completed);
        Assert.False(condition: rejectedCandidate.ReleaseAdmissionOpen);
        var recovering = (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Assert.Equal(
            WorldReleaseOperationPhase.RecoverActivate,
            recovering.Record.PendingPhase
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Closed,
            recovering.Record.Admission
        );
        var recoveringAfterRestart = new WorldReleaseCoordinator(groups: groups).ResumeAsync(
            recovering,
            release,
            new WorldSiloReleaseRuntime(
                candidate: rejectedCandidate,
                identities: identities,
                recoveryFactory: () => recoveredSource,
                source: restarted
            ),
            Token
        );

        await PumpAllAsync(
            hosts: [recoveredSource],
            operation: recoveringAfterRestart
        );
        Assert.True(
            condition: (await recoveringAfterRestart).SourceRecovered,
            userMessage: (await recoveringAfterRestart).Detail
        );
        AssertTicks(
            expected: continued,
            host: recoveredSource,
            identities: identities
        );
        Assert.True(condition: recoveredSource.ReleaseAdmissionOpen);
        Assert.Null(@object: (await groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record.RestorePoint);
        Assert.Equal(
            lateReceipt,
            await authority.FindOperationReceiptAsync(
                identity0,
                lateReceipt.OperationId,
                Token
            )
        );
        await PumpAsync(
            host: recoveredSource,
            operation: recoveredSource.DrainAsync(ct: Token)
        );
        Console.WriteLine(value: $"rewind probe: saved={string.Join(
            separator: ',',
            values: expected.Values
        )}, resumed={string.Join(
            separator: ',',
            values: continued.Values
        )}, point={point.Identity}");
    }

    private sealed class RewindInterruptionStore(IObjectBlobStore inner) : IObjectBlobStore {
        public ulong? InterruptAtTick;

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid owner, string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: prefix,
            objectId: owner,
            target: target
        );
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(
            address: address,
            cancellationToken: cancellationToken,
            target: target
        );
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            var result = await inner.WriteAsync(
                address: address,
                cancellationToken: cancellationToken,
                content: content,
                ifMatchVersion: ifMatchVersion,
                mode: mode,
                target: target
            );

            if (
                result.Succeeded &&
                (InterruptAtTick is { } tick) &&
                address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/alpha/authority/root"
            )
            ) {
                using var root = JsonDocument.Parse(content);

                if (
                    (root.RootElement.GetProperty(propertyName: "checkpointTick").GetUInt64() == tick) &&
                    (root.RootElement.GetProperty(propertyName: "fence").GetGuid() == Guid.Empty)
                ) {
                    InterruptAtTick = null;
                    throw new OperationCanceledException(message: "focused probe: controller interrupted after the first world restore");
                }
            }
            return result;
        }
    }
}
