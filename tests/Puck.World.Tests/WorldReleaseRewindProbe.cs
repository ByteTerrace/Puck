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
    [Fact(Timeout = 60000)]
    public async Task LocalDeployRollbackRewindAndInterruptedResumePreserveTheirDistinctStateContracts() {
        using var directory = new TempWorldDirectory();
        using var output = new BufferedConsoleOutput();
        var owner = Guid.NewGuid();
        var blobs = new RewindInterruptionStore(PuckStorageTestComposition.BuildStore());
        var target = new DirectoryObjectStorageTarget(directory.RootPath);
        var groups = new WorldReleaseGroupStore(blobs, target, owner);
        var authority = new WorldAuthorityBlobStore(blobs, target);
        var archive = new WorldReleaseArchive(blobs, target, owner);
        var points = new WorldReleaseFixtureArchive(blobs, target, owner);
        var restores = new WorldReleaseRestore(blobs, target, owner);
        var identities = new[] { new WorldAuthorityIdentity(owner, SafeName.Parse("alpha")), new WorldAuthorityIdentity(owner, SafeName.Parse("beta")) };
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyFile = directory.WriteBytes("world.pk8", key.ExportPkcs8PrivateKey());
        var initial = Fixtures.BuildDocument();
        var document = initial with {
            HostRaw = Fixtures.StandardHost with { Authority = null, Listen = null, Presentation = WorldHostPresentation.None },
            PopulationRaw = initial.Population with { CapacityRaw = WorldBodiesLimits.LocalSeatCount + 1, NetworkPlayers = 1 },
            Admission = [Fixtures.AnyAuthorityArrivals()],
        };
        var bytes = WorldDefinitionSerialization.Serialize(document);
        var definitionPin = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(bytes));
        var package = Path.Combine(directory.RootPath, "package");
        Directory.CreateDirectory(package);
        foreach (var identity in identities) {
            File.WriteAllBytes(Path.Combine(package, identity.World.Value + ".world.json"), bytes);
            Assert.True((await authority.PublishDefinitionAsync(identity, document, Token)).Ok);
        }
        var release = new WorldReleaseManifest {
            Label = "rewind-probe", SourceRevision = new string('a', 40), EngineImageDigest = "sha256:" + new string('a', 64),
            Definitions = identities.ToDictionary(row => $"{owner:D}/{row.World}", _ => definitionPin),
            DefinitionFiles = identities.ToDictionary(row => $"{owner:D}/{row.World}", row => row.World.Value + ".world.json"),
            PersistenceContract = "puck.world.persistence.v1", PeerProtocolContract = "puck.world.peer.v1",
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
        };
        await archive.SaveAsync(release, package, Token);
        var nextDocument = document with { Metadata = new(Title: "Release B") };
        var nextBytes = WorldDefinitionSerialization.Serialize(nextDocument);
        var nextPin = "sha256/" + Convert.ToHexStringLower(SHA256.HashData(nextBytes));
        var nextPackage = Directory.CreateDirectory(Path.Combine(directory.RootPath, "package-b")).FullName;
        foreach (var identity in identities) { File.WriteAllBytes(Path.Combine(nextPackage, identity.World.Value + ".world.json"), nextBytes); }
        var nextRelease = release with { Label = "release-b", EngineImageDigest = "sha256:" + new string('b', 64),
            Definitions = identities.ToDictionary(row => $"{owner:D}/{row.World}", _ => nextPin) };
        await archive.SaveAsync(nextRelease, nextPackage, Token);
        Required(await groups.CreateAsync("primary", release.Identity, Token));
        WorldSiloHost Host(string? releaseId = null, bool closed = true) {
            var routing = new SiloConsoleRouting(() => new TextCommandSource(new CommandRegistry([])), new SiloConsoleTagging(output));
            var rows = identities.Select(row => new WorldSiloWorldRow(owner, row.World, new(keyFile), true)).ToArray();
            return new(new WorldSiloDefinition(rows, new(2), new("directory", JsonElement.Parse("{}")),
                directory.RootPath, new("Localhost"), Release: new("primary", owner, releaseId ?? release.Identity) { ClosedGroupRewind = closed }), blobs, routing, target);
        }
        var source = Host();
        using var sourceInstances = source.Instances;
        await ActivateAllAsync(source, identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(source));
        Tick(source, 3);
        var beforeDeploy = Ticks(source, identities);
        var enforcingCapture = source.ExportReleaseFixtureAsync(Guid.NewGuid(), Token);
        await PumpAsync(source, enforcingCapture);
        Assert.NotNull((await enforcingCapture).RewindBoundary);
        var deployed = Host(nextRelease.Identity);
        using var deployedInstances = deployed.Instances;
        async Task PrepareMetadataAsync(WorldReleaseGroupRecord state, WorldReleaseManifest from, WorldReleaseManifest to) {
            foreach (var identity in identities) {
                var publication = await authority.PrepareReleaseMetadataAsync(identity, state, from, to, archive, Token);
                Assert.True(publication.Ok, publication.Detail);
            }
        }
        var group = (await groups.LoadAsync("primary", Token))!.Value;
        var deploy = new WorldReleaseCoordinator(groups).ResumeAsync(
            Required(await groups.BeginAsync(group, Guid.NewGuid(), nextRelease.Identity, Token)), nextRelease,
            new LoopbackRuntime(new WorldSiloReleaseRuntime(source, deployed, identities, () => throw new InvalidOperationException()),
                source, deployed, state => PrepareMetadataAsync(state, release, nextRelease)), Token);
        await PumpAllAsync([source, deployed], deploy);
        Assert.True((await deploy).Completed, (await deploy).Detail);
        AssertTicks(beforeDeploy, deployed, identities);
        foreach (var identity in identities) {
            Assert.True(deployed.Instances.TryGet(identity.World.Value, out var row));
            Assert.Equal("Release B", row!.Server.Definition.Metadata!.Title);
        }
        Tick(deployed, 7);
        var beforeRollback = Ticks(deployed, identities);
        var rolledBack = Host();
        using var rolledBackInstances = rolledBack.Instances;
        group = (await groups.LoadAsync("primary", Token))!.Value;
        var rollback = new WorldReleaseCoordinator(groups).ResumeAsync(
            Required(await groups.BeginRollbackAsync(group, Guid.NewGuid(), Token)), release,
            new LoopbackRuntime(new WorldSiloReleaseRuntime(deployed, rolledBack, identities, () => throw new InvalidOperationException()),
                deployed, rolledBack, state => PrepareMetadataAsync(state, nextRelease, release)), Token);
        await PumpAllAsync([deployed, rolledBack], rollback);
        Assert.True((await rollback).Completed, (await rollback).Detail);
        AssertTicks(beforeRollback, rolledBack, identities);
        foreach (var identity in identities) {
            Assert.True(rolledBack.Instances.TryGet(identity.World.Value, out var row));
            Assert.Equal(document.Metadata, row!.Server.Definition.Metadata);
        }
        source = rolledBack;
        Assert.True(source.Instances.TryGet("alpha", out var alpha));
        Assert.True(source.Instances.TryGet("beta", out var beta));
        const int peerSlot = WorldBodiesLimits.LocalSeatCount;
        Assert.True(alpha!.Server.ExecuteAuthorityOperation(() => alpha.Server.Population.TryAdmitRemotePeerAt(peerSlot,
            IntentSource.Live, [], "probe", "player", out _, out _)));
        Tick(source, 5);
        var capturing = source.ExportReleaseFixtureAsync(Guid.NewGuid(), Token);
        await PumpAsync(source, capturing);
        var point = await capturing;
        Assert.NotNull(point.RewindBoundary);
        Assert.NotNull(point.CapturedAt);
        var expected = Ticks(source, identities);
        var rootsAtPoint = new Dictionary<string, WorldAuthorityRoot>();
        foreach (var identity in identities) { rootsAtPoint[identity.World.Value] = (await authority.LoadRootAsync(identity, Token))!.Value.Root; }
        var preview = await restores.InspectAsync("primary", point.RequestId, Token);
        Assert.Equal(point.Identity, preview.Point.Identity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => restores.BeginAsync("primary", point.RequestId, point.Identity, Guid.NewGuid(), false, Token));
        Tick(source, 8);
        Assert.All(Ticks(source, identities), row => Assert.True(row.Value > expected[row.Key]));
        var externalReservation = beta!.Server.ReserveTransfer(new(99, "external", 60, 0, 60, string.Empty, null, true, true, []));
        Assert.False(externalReservation.Accepted);
        Assert.Contains("closed rewind group", externalReservation.Reason);
        Assert.False(beta.Server.CommitTransfer("external", 99, [], out var externalReason));
        Assert.Contains("closed rewind group", externalReason);
        using (var closedNetwork = new WorldPeerNetwork(allowOutbound: false)) {
            await Assert.ThrowsAsync<InvalidOperationException>(async () => await closedNetwork.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 4433), Token));
        }
        _ = source.Instances.EnqueueTransfer(sourceInstance: "alpha", scope: WorldInstanceHost.TransferScope.Body,
            sourceSlot: peerSlot, destination: WorldInstanceHost.TransferDestination.Remote("beta", "beta.world.json", "external"), actingPrincipal: WorldPrincipal.Console);
        source.Instances.DrainPendingTransfers();
        Assert.True(alpha.Server.Population.IsActive(peerSlot));
        Assert.False(beta.Server.Population.IsActive(peerSlot));
        _ = source.Instances.EnqueueTransfer(sourceInstance: "alpha", scope: WorldInstanceHost.TransferScope.Body,
            sourceSlot: peerSlot, destination: WorldInstanceHost.TransferDestination.Existing("beta"), actingPrincipal: WorldPrincipal.Console);
        source.Instances.DrainPendingTransfers();
        Assert.False(alpha.Server.Population.IsActive(peerSlot));
        Assert.True(beta!.Server.Population.IsActive(peerSlot));

        // A receipt created after the point must still reject duplicate/conflicting operation IDs after rewind.
        var identity0 = identities[0];
        var root = (await authority.LoadRootAsync(identity0, Token))!.Value;
        var lateReceipt = new WorldAuthorityOperationReceipt(Guid.NewGuid(), "probe", "payload", "refused", false, root.Root.Sequence + 1, null);
        Assert.True((await authority.RecordReceiptAsync(identity0, lateReceipt, Token, new(root.Root.Epoch, root.Root.FenceToken, root.VersionToken))).Ok);
        var operation = Guid.NewGuid();
        var begun = Required(await restores.BeginAsync("primary", point.RequestId, point.Identity, operation, true, Token));
        var candidate = Host();
        using var candidateInstances = candidate.Instances;
        var runtime = new LoopbackRuntime(new WorldSiloReleaseRuntime(source, candidate, identities,
            () => throw new InvalidOperationException("successful restore must not compensate")), source, candidate);
        blobs.InterruptAtTick = expected["alpha"];
        var restoring = new WorldReleaseCoordinator(groups).ResumeAsync(begun, release, runtime, Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PumpAllAsync([source, candidate], restoring));
        var interrupted = (await groups.LoadAsync("primary", Token))!.Value;
        Assert.Equal(WorldReleaseOperationPhase.Activate, interrupted.Record.PendingPhase);
        Assert.Equal(WorldReleaseAdmissionState.Closed, interrupted.Record.Admission);
        Assert.False(candidate.ReleaseAdmissionOpen);
        Assert.Equal(expected["alpha"], (await authority.LoadRootAsync(identities[0], Token))!.Value.Root.CheckpointTick);
        Assert.Equal(WorldDefinitionFileSource.ComputeContentHash((await points.ReadCheckpointAsync(point, "alpha", Token)).Span),
            (await authority.LoadRootAsync(identities[0], Token))!.Value.Root.CheckpointHash);
        Assert.True((await authority.LoadRootAsync(identities[1], Token))!.Value.Root.CheckpointTick > expected["beta"]);
        var resumedCandidate = Host();
        using var resumedCandidateInstances = resumedCandidate.Instances;
        runtime = new LoopbackRuntime(new WorldSiloReleaseRuntime(source, resumedCandidate, identities,
            () => throw new InvalidOperationException("resumed restore must not compensate")), source, resumedCandidate);
        restoring = new WorldReleaseCoordinator(groups).ResumeAsync(interrupted, release, runtime, Token);
        await PumpAllAsync([source, resumedCandidate], restoring);
        var result = await restoring;
        Assert.True(result.Completed, result.Detail);
        candidate = resumedCandidate;
        AssertTicks(expected, candidate, identities);
        foreach (var identity in identities) {
            Assert.True(candidate.Instances.TryGet(identity.World.Value, out var restoredRow));
            Assert.True(restoredRow!.Server.TryCaptureCheckpoint(candidate.Instances.CaptureRow(restoredRow), out var checkpoint, out var reason), reason);
            var expectedBytes = (await points.ReadCheckpointAsync(point, identity.World.Value, Token)).ToArray();
            Assert.True(WorldAuthorityCheckpointCodec.TryDecode(expectedBytes, out var expectedCheckpoint, out reason), reason);
            // Activation deliberately parks disconnected humans. Check that exact, documented change, then
            // compare the entire checkpoint: no other population, topology or gameplay difference is allowed.
            if (identity.World.Value == "alpha") {
                var entries = expectedCheckpoint!.Population.Entries.ToArray();
                Assert.Single(entries);
                Assert.False(entries[0].Parked);
                Assert.Null(entries[0].ParkedUntilTick);
                entries[0] = entries[0] with { Parked = true, ParkedUntilTick = checked((long)expected["alpha"] + document.PopulationReconnectGraceTicks.Ticks) };
                expectedCheckpoint = expectedCheckpoint with { Population = expectedCheckpoint.Population with { Entries = entries } };
            }
            Assert.Equal(WorldAuthorityCheckpointCodec.Encode(expectedCheckpoint!), WorldAuthorityCheckpointCodec.Encode(checkpoint!));
        }
        Assert.True(candidate.Instances.TryGet("alpha", out var restoredAlpha));
        Assert.True(candidate.Instances.TryGet("beta", out var restoredBeta));
        Assert.True(restoredAlpha!.Server.Population.IsActive(peerSlot));
        Assert.False(restoredBeta!.Server.Population.IsActive(peerSlot));
        Assert.True(restoredAlpha.Server.Population.IsParked(peerSlot));
        Assert.True(restoredAlpha.Server.ExecuteAuthorityOperation(() => restoredAlpha.Server.Population.TryResumeParkedPeer("probe", "player", out _)));
        Assert.False(restoredAlpha.Server.Population.IsParked(peerSlot));
        Assert.Equal(lateReceipt, await authority.FindOperationReceiptAsync(identity0, lateReceipt.OperationId, Token));
        Assert.True(candidate.ReleaseAdmissionOpen);
        foreach (var identity in identities) {
            var after = (await authority.LoadRootAsync(identity, Token))!.Value.Root;
            Assert.True(after.Epoch > rootsAtPoint[identity.World.Value].Epoch);
            Assert.Equal(point.RewindBoundary, after.RewindBoundary);
            Assert.True(after.CheckpointOrdinal > rootsAtPoint[identity.World.Value].CheckpointOrdinal);
            Assert.Equal(expected[identity.World.Value], after.CheckpointTick);
        }
        var durable = (await groups.LoadAsync("primary", Token))!.Value;
        Assert.Equal(point.Identity, durable.Record.RestorePoint!.Identity);
        Assert.Equal(release.Identity, durable.Record.ActiveRelease);
        Assert.Equal(nextRelease.Identity, durable.Record.PreviousRelease);
        Assert.True(durable.Record.RollbackEligible);
        Tick(candidate, 3);
        var continued = Ticks(candidate, identities);
        await PumpAsync(candidate, candidate.DrainAsync(Token));
        var unsafeHost = Host(closed: false);
        using var unsafeInstances = unsafeHost.Instances;
        var unsafeActivation = unsafeHost.ActivateAsync(identity0, Token);
        await Assert.ThrowsAsync<InvalidOperationException>(() => PumpAsync(unsafeHost, unsafeActivation));
        var restarted = Host();
        using var restartedInstances = restarted.Instances;
        await ActivateAllAsync(restarted, identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(restarted));
        AssertTicks(continued, restarted, identities);
        Assert.Equal(lateReceipt, await authority.FindOperationReceiptAsync(identity0, lateReceipt.OperationId, Token));
        // A completed-operation resume cannot apply the old selected checkpoint a second time.
        var resume = new WorldReleaseCoordinator(groups).ResumeAsync(durable, release,
            new LoopbackRuntime(new WorldSiloReleaseRuntime(source, restarted, identities, () => throw new InvalidOperationException()), source, restarted), Token);
        await PumpAllAsync([restarted], resume);
        Assert.True((await resume).Completed, (await resume).Detail);
        AssertTicks(continued, restarted, identities);
        await PumpAsync(restarted, restarted.DrainAsync(Token));
        Console.WriteLine($"rewind probe: saved={string.Join(',', expected.Values)}, resumed={string.Join(',', continued.Values)}, point={point.Identity}");
    }

    private sealed class RewindInterruptionStore(IObjectBlobStore inner) : IObjectBlobStore {
        public ulong? InterruptAtTick;
        public ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) => inner.ReadAsync(target, address, cancellationToken);
        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid owner, string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(target, owner, prefix, cancellationToken);
        public async ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content,
            ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) {
            var result = await inner.WriteAsync(target, address, content, mode, ifMatchVersion, cancellationToken);
            if (result.Succeeded && InterruptAtTick is { } tick && address.Key.EndsWith("/alpha/authority/root", StringComparison.Ordinal)) {
                using var root = JsonDocument.Parse(content);
                if (root.RootElement.GetProperty("checkpointTick").GetUInt64() == tick && root.RootElement.GetProperty("fence").GetGuid() == Guid.Empty) {
                    InterruptAtTick = null;
                    throw new OperationCanceledException("focused probe: controller interrupted after the first world restore");
                }
            }
            return result;
        }
    }
}
