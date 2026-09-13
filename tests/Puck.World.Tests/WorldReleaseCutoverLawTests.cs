using System.Security.Cryptography;
using System.Text.Json;
using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.Hosting;
using Puck.Launcher;
using Puck.Storage;
using Puck.World.Server;
using Puck.World.Silo;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises release admission through real hosted rows and the production simulation pump.</summary>
public sealed class WorldReleaseCutoverLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task QualificationExportCapturesBothRowsWithoutDrainingAndRetryRetainsTheOriginalBoundary() {
        using var scenario = new Scenario();
        var release = scenario.Manifest('a').Identity;
        await scenario.InitializeAsync(release);
        var host = scenario.Host(release).Host;
        using var instances = host.Instances;
        await ActivateAllAsync(host, scenario.Identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(host));
        Tick(host, 5);
        var expected = Ticks(host, scenario.Identities);
        var before = await scenario.FencesAsync();
        var request = Guid.NewGuid();
        var exporting = host.ExportReleaseFixtureAsync(request, Token);
        await PumpAsync(host, exporting);
        var manifest = await exporting;
        Assert.Equal(expected.Keys.Order(StringComparer.Ordinal), manifest.Worlds.Keys);
        Assert.Equal(release, manifest.Release);
        Assert.False(host.IsDraining);
        Assert.True(host.ReleaseAdmissionOpen);
        foreach (var world in manifest.Worlds) {
            Assert.Equal(expected[world.Key], world.Value.Tick);
            var bytes = await scenario.FixtureArchive.ReadCheckpointAsync(manifest, world.Key, Token);
            Assert.True(WorldAuthorityCheckpointCodec.TryDecode(bytes.Span, out var checkpoint, out var reason), reason);
            Assert.Equal(expected[world.Key], checkpoint!.Server.LastCompletedTick);
        }
        Assert.Equal(before, await scenario.FencesAsync());
        Tick(host, 3);
        Assert.All(Ticks(host, scenario.Identities), row => Assert.True(row.Value > expected[row.Key]));
        var retrying = host.ExportReleaseFixtureAsync(request, Token);
        await PumpAsync(host, retrying);
        Assert.Equal(manifest.Identity, (await retrying).Identity);
        var group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        Required(await scenario.Groups.BeginAsync(group, Guid.NewGuid(), scenario.Manifest('b').Identity, Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.ExportReleaseFixtureAsync(Guid.NewGuid(), Token));
        Assert.False(host.IsDraining);
        await PumpAsync(host, host.DrainAsync(Token));
    }

    [Fact]
    public async Task CoordinatorDeployAndRollbackRetainLatestGameplayAcrossBothRows() {
        using var scenario = new Scenario();
        var a = scenario.Manifest('a');
        var b = scenario.Manifest('b');
        await scenario.InitializeAsync(a.Identity);
        var source = scenario.Host(a.Identity).Host;
        var candidate = scenario.Host(b.Identity).Host;
        var rollback = scenario.Host(a.Identity).Host;
        using var sourceInstances = source.Instances;
        using var candidateInstances = candidate.Instances;
        using var rollbackInstances = rollback.Instances;
        await ActivateAllAsync(source, scenario.Identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(source));
        Tick(source, 5);
        var before = Ticks(source, scenario.Identities);
        var coordinator = new WorldReleaseCoordinator(scenario.Groups);
        var group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        group = Required(await scenario.Groups.BeginAsync(group, Guid.NewGuid(), b.Identity, Token));
        var runtime = new LoopbackRuntime(new WorldSiloReleaseRuntime(source, candidate, scenario.Identities, () => throw new InvalidOperationException("successful deploy must not recover")), source, candidate);
        var deploying = coordinator.ResumeAsync(group, b, runtime, Token);
        await PumpAllAsync([source, candidate], deploying);
        Assert.True((await deploying).Completed, (await deploying).Detail);
        AssertTicks(before, candidate, scenario.Identities);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.CaptureReleaseRootsAsync(group.Record.PendingOperationId!.Value, Token));
        Tick(candidate, 7);
        var latest = Ticks(candidate, scenario.Identities);
        foreach (var row in before) { Assert.True(latest[row.Key] > row.Value); }

        group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        Assert.NotNull(group.Record.PendingOperationId);
        Assert.False(group.Record.HasUnfinishedOperation);
        var captureRequest = Guid.NewGuid();
        var rollbackCapture = ControlAsync(candidate, "POST", $"/release/fixture/{captureRequest:D}");
        await PumpAsync(candidate, rollbackCapture);
        Assert.Equal(200, (await rollbackCapture).Status);
        var rollbackFixture = (await scenario.FixtureArchive.LoadAsync(captureRequest, Token))!;
        Assert.Equal(b.Identity, rollbackFixture.Release);
        foreach (var row in latest) { Assert.Equal(row.Value, rollbackFixture.Worlds[row.Key].Tick); }
        group = Required(await scenario.Groups.BeginRollbackAsync(group, Guid.NewGuid(), Token));
        Assert.True(group.Record.HasUnfinishedOperation);
        Assert.Equal(409, (await ControlAsync(candidate, "POST", $"/release/fixture/{Guid.NewGuid():D}")).Status);
        var rollbackRuntime = new LoopbackRuntime(new WorldSiloReleaseRuntime(candidate, rollback, scenario.Identities, () => throw new InvalidOperationException("successful rollback must not restore an old save")), candidate, rollback);
        var rollingBack = coordinator.ResumeAsync(group, a, rollbackRuntime, Token);
        await PumpAllAsync([candidate, rollback], rollingBack);
        Assert.True((await rollingBack).Completed, (await rollingBack).Detail);
        AssertTicks(latest, rollback, scenario.Identities);
        Assert.True(rollback.ReleaseAdmissionOpen);
        var durable = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        Assert.Equal(a.Identity, durable.Record.ActiveRelease);
        Assert.Equal(b.Identity, durable.Record.PreviousRelease);
        Assert.All(durable.Record.RecoveryRoots.Values, pin => Assert.StartsWith("sha256/", pin));
        Required(await scenario.Groups.FinalizeAsync(durable, Token));
        await PumpAsync(rollback, rollback.DrainAsync(Token));
    }

    [Fact]
    public async Task FailedPrivateCandidateRecoversUnderFreshFencesAndResumesAfterRecoveryRestart() {
        using var scenario = new Scenario();
        var a = scenario.Manifest('a');
        var b = scenario.Manifest('b');
        await scenario.InitializeAsync(a.Identity);
        var source = scenario.Host(a.Identity).Host;
        var candidate = scenario.Host(b.Identity).Host;
        var recovery = scenario.Host(a.Identity).Host;
        using var sourceInstances = source.Instances;
        using var candidateInstances = candidate.Instances;
        using var recoveryInstances = recovery.Instances;
        await ActivateAllAsync(source, scenario.Identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(source));
        Tick(source, 5);
        var before = Ticks(source, scenario.Identities);
        var oldFences = await scenario.FencesAsync();
        var group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        group = Required(await scenario.Groups.BeginAsync(group, Guid.NewGuid(), b.Identity, Token));
        var runtime = new WorldSiloReleaseRuntime(source, candidate, scenario.Identities, () => recovery);
        var failing = new FailureRuntime(runtime);
        var deploying = new WorldReleaseCoordinator(scenario.Groups).ResumeAsync(group, b, failing, Token);
        await PumpAllAsync([source, candidate, recovery], deploying);
        Assert.False((await deploying).Completed);
        Assert.False(candidate.ReleaseAdmissionOpen);
        group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        Assert.Equal(WorldReleaseOperationPhase.RecoverActivate, group.Record.PendingPhase);
        Assert.Equal(WorldReleaseAdmissionState.Closed, group.Record.Admission);

        // A new coordinator and adapter reconstruct recovery from the durable phase, without restoring twice.
        var resumedRuntime = new WorldSiloReleaseRuntime(source, candidate, scenario.Identities, () => recovery);
        var resuming = new WorldReleaseCoordinator(scenario.Groups).ResumeAsync(group, b, resumedRuntime, Token);
        await PumpAllAsync([source, candidate, recovery], resuming);
        var result = await resuming;
        Assert.False(result.Completed);
        Assert.True(result.SourceRecovered, result.Detail);
        AssertTicks(before, recovery, scenario.Identities);
        Assert.True(recovery.ReleaseAdmissionOpen);
        group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        Assert.Null(group.Record.PendingOperationId);
        Assert.Equal(a.Identity, group.Record.ActiveRelease);
        var newFences = await scenario.FencesAsync();
        foreach (var old in oldFences) {
            var fresh = Assert.Single(newFences, row => row.Identity == old.Identity);
            Assert.True(fresh.Fence.Epoch > old.Fence.Epoch);
            Assert.NotEqual(old.Fence.Token, fresh.Fence.Token);
        }
        await PumpAsync(recovery, recovery.DrainAsync(Token));
    }

    private sealed class FailureRuntime(IWorldReleaseRuntime inner) : IWorldReleaseRuntime {
        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.DrainSourceAndCaptureAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartCandidatePrivatelyAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StopCandidateAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Task.FromResult(new WorldReleaseRuntimeResult(false, "injected private probe failure"));
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.ReadFenceCensusAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishCandidateAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.RecoverSourceAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Task.FromResult(new WorldReleaseRuntimeResult(false, "injected recovery worker interruption"));
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishRecoveredSourceAsync(operation, cancellationToken);
    }

    [Fact]
    public async Task ReleaseControlRejectsRemoteAndWrongOperationBeforeWorkerEffects() {
        using var scenario = new Scenario();
        await scenario.InitializeAsync();
        var host = scenario.Host("release-a").Host;
        using var instances = host.Instances;
        var remote = await ControlAsync(host, "GET", "/release/status", IPAddress.Parse("203.0.113.1"));
        Assert.Equal(404, remote.Status);
        var stale = await ControlAsync(host, "POST", $"/release/drain/{Guid.NewGuid():D}");
        Assert.Equal(409, stale.Status);
        Assert.False(host.IsDraining);
        var status = await ControlAsync(host, "GET", "/release/status");
        Assert.Equal(200, status.Status);
        Assert.Equal("release-a", JsonSerializer.Deserialize<WorldReleaseWorkerStatus>(status.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.Release);
    }

    // The worker-side controls execute against actual hosts; only the Azure transport is replaced by loopback HTTP contexts.
    private sealed class LoopbackRuntime(IWorldReleaseRuntime inner, WorldSiloHost source, WorldSiloHost candidate) : IWorldReleaseRuntime {
        private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(source, "POST", $"/release/drain/{operation.PendingOperationId:D}");
            return new(result.Status == 200, result.Body, result.Status == 200 ? JsonSerializer.Deserialize<Dictionary<string, string>>(result.Body, Options) : null);
        }
        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(candidate, "GET", $"/release/fences/{operation.PendingOperationId:D}");
            Assert.Equal(200, result.Status);
            return JsonSerializer.Deserialize<WorldReleaseWorkerFence[]>(result.Body, Options)!.Select(row =>
                (new WorldAuthorityIdentity(row.Owner, SafeName.Parse(row.World)), new WorldAuthorityFence(row.Epoch, row.Token, row.RootVersion))).ToArray();
        }
        public async Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(candidate, "POST", $"/release/publish/{operation.PendingOperationId:D}");
            return result.Status == 200 ? WorldReleaseRuntimePublication.Opened : WorldReleaseRuntimePublication.Refused;
        }
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartCandidatePrivatelyAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StopCandidateAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.VerifyCandidatePrivatelyAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.RecoverSourceAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartRecoveredSourceAsync(operation, cancellationToken);
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishRecoveredSourceAsync(operation, cancellationToken);
    }

    private static async Task<(int Status, string Body)> ControlAsync(WorldSiloHost host, string method, string path, IPAddress? remote = null) {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestServices = services, RequestAborted = Token };
        context.Connection.RemoteIpAddress = remote ?? IPAddress.Loopback;
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = body;
        Assert.True(await new WorldSiloReleaseControl(host).HandleAsync(context));
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(body.ToArray()));
    }

    private static async Task PumpAllAsync(IReadOnlyList<WorldSiloHost> hosts, Task operation) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!operation.IsCompleted) {
            foreach (var host in hosts) { host.DrainActivationMailbox(); }
            await Task.Delay(1, deadline.Token);
        }
        await operation;
    }

    [Fact]
    public async Task TwoRowCutoverKeepsCandidatesFrozenAndCommittedRestartPreservesProgress() {
        using var scenario = new Scenario();
        await scenario.InitializeAsync();
        var (source, sourceRouting) = scenario.Host("release-a");
        using var sourceInstances = source.Instances;
        await ActivateAllAsync(source, scenario.Identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(source));
        Tick(source, 3);
        var sourceTicks = Ticks(source, scenario.Identities);
        Assert.All(sourceTicks.Values, tick => Assert.True(tick > 0));
        await PumpAsync(source, source.DrainAsync(Token));

        var operation = Guid.NewGuid();
        var group = (await scenario.Groups.LoadAsync("primary", Token))!.Value;
        group = Required(await scenario.Groups.BeginAsync(group, operation, "release-b", Token));
        group = await scenario.AdvanceAsync(group, WorldReleaseOperationPhase.Drain);
        group = await scenario.AdvanceAsync(group, WorldReleaseOperationPhase.Activate);

        // A delayed source process must be refused before it can replace any candidate fence.
        var (obsolete, _) = scenario.Host("release-a");
        using var obsoleteInstances = obsolete.Instances;
        var before = await scenario.Authority.LoadRootAsync(scenario.Identities[0], Token);
        var obsoleteActivation = obsolete.ActivateAsync(scenario.Identities[0], Token);
        await PumpAsync(obsolete, obsoleteActivation);
        Assert.False(await obsoleteActivation);
        Assert.Equal(before, await scenario.Authority.LoadRootAsync(scenario.Identities[0], Token));

        var (candidate, candidateRouting) = scenario.Host("release-b");
        using var candidateInstances = candidate.Instances;
        await ActivateAllAsync(candidate, [scenario.Identities[0]]);
        Assert.Equal(WorldReleaseAdmissionPublication.Refused, await PublishAsync(candidate));
        Assert.False(candidate.ReleaseAdmissionOpen);
        Assert.False(candidateRouting.TryGetSession("alpha", out _));
        await ActivateAllAsync(candidate, [scenario.Identities[1]]);
        Assert.Equal(WorldReleaseAdmissionPublication.CandidatePrivate, await PublishAsync(candidate));
        Tick(candidate, 5);
        AssertTicks(sourceTicks, candidate, scenario.Identities);
        Assert.False(candidateRouting.TryGetSession("alpha", out _));
        Assert.False(candidateRouting.TryGetSession("beta", out _));

        group = await scenario.AdvanceAsync(group, WorldReleaseOperationPhase.Verify);
        group = Required(await scenario.Groups.CommitAsync(group, await scenario.FencesAsync(), Token));
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(candidate));
        Assert.True(candidateRouting.TryGetSession("alpha", out _));
        Assert.True(candidateRouting.TryGetSession("beta", out _));
        Tick(candidate, 4);
        var committedTicks = Ticks(candidate, scenario.Identities);
        foreach (var row in sourceTicks) { Assert.True(committedTicks[row.Key] > row.Value); }
        await PumpAsync(candidate, candidate.DrainAsync(Token));

        var (replacement, replacementRouting) = scenario.Host("release-b");
        using var replacementInstances = replacement.Instances;
        await ActivateAllAsync(replacement, scenario.Identities);
        Tick(replacement, 3);
        AssertTicks(committedTicks, replacement, scenario.Identities);
        Assert.Equal(WorldReleaseAdmissionPublication.Opened, await PublishAsync(replacement));
        Assert.True(replacementRouting.TryGetSession("alpha", out _));
        Assert.True(replacementRouting.TryGetSession("beta", out _));
        var resumed = (await scenario.Groups.LoadAsync("primary", Token))!.Value.Record;
        Assert.Equal(operation, resumed.PendingOperationId);
        Assert.Equal("release-b", resumed.ActiveRelease);
        Assert.Equal("release-a", resumed.PreviousRelease);
        Assert.True(resumed.RollbackEligible);
        await PumpAsync(replacement, replacement.DrainAsync(Token));
    }

    [Fact]
    public async Task StaleFenceInOneRowRefusesTheWholePublication() {
        using var scenario = new Scenario();
        await scenario.InitializeAsync();
        var (host, routing) = scenario.Host("release-a");
        using var instances = host.Instances;
        await ActivateAllAsync(host, scenario.Identities);
        Assert.NotNull(await scenario.Authority.AcquireActivationAsync(scenario.Identities[1], Token));
        Assert.Equal(WorldReleaseAdmissionPublication.Refused, await PublishAsync(host));
        Assert.False(host.ReleaseAdmissionOpen);
        Assert.False(routing.TryGetSession("alpha", out _));
        Assert.False(routing.TryGetSession("beta", out _));
        var ticks = Ticks(host, scenario.Identities);
        Tick(host, 3);
        AssertTicks(ticks, host, scenario.Identities);
    }

    private static void Tick(WorldSiloHost host, int count) {
        var simulation = new WorldSiloSimulation(host);
        for (var index = 0; index < count; index++) {
            simulation.Step(new FixedStepContext((ulong)index, Fixtures.StepTicks, Fixtures.StepTicks), default);
        }
    }

    private static Dictionary<string, ulong> Ticks(WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) =>
        identities.ToDictionary(identity => identity.World.Value, identity => {
            Assert.True(host.Instances.TryGet(identity.World.Value, out var instance));
            return instance!.CompletedTicks;
        });

    private static void AssertTicks(IReadOnlyDictionary<string, ulong> expected, WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) {
        foreach (var actual in Ticks(host, identities)) { Assert.Equal(expected[actual.Key], actual.Value); }
    }

    private static async Task ActivateAllAsync(WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) {
        foreach (var identity in identities) {
            var activation = host.ActivateAsync(identity, Token);
            await PumpAsync(host, activation);
            Assert.True(await activation);
        }
    }

    private static async Task<WorldReleaseAdmissionPublication> PublishAsync(WorldSiloHost host) {
        var publication = host.PublishManagedReleaseAdmissionAsync(Token);
        await PumpAsync(host, publication);
        return await publication;
    }

    private static async Task PumpAsync(WorldSiloHost host, Task operation) {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        while (!operation.IsCompleted) {
            host.DrainActivationMailbox();
            await Task.Delay(1, deadline.Token);
        }
        await operation;
        host.DrainActivationMailbox();
    }

    private static WorldReleaseGroupSnapshot Required(WorldReleaseGroupOutcome outcome) {
        Assert.True(outcome.Ok, outcome.Detail);
        return outcome.Snapshot!.Value;
    }

    private sealed class Scenario : IDisposable {
        private readonly TempWorldDirectory m_directory = new();
        private readonly BufferedConsoleOutput m_output = new();
        private readonly IObjectBlobStore m_store = PuckStorageTestComposition.BuildStore();
        private readonly string m_keyFile;
        private readonly Guid m_owner = Guid.NewGuid();
        private readonly DirectoryObjectStorageTarget m_target;
        public WorldAuthorityIdentity[] Identities { get; }
        public WorldAuthorityBlobStore Authority { get; }
        public WorldReleaseGroupStore Groups { get; }
        public WorldReleaseFixtureArchive FixtureArchive { get; }

        public Scenario() {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            m_keyFile = m_directory.WriteBytes("world.key", key.ExportPkcs8PrivateKey());
            m_target = new(m_directory.RootPath);
            Identities = [new(m_owner, SafeName.Parse("alpha")), new(m_owner, SafeName.Parse("beta"))];
            Authority = new(m_store, m_target);
            Groups = new(m_store, m_target, m_owner);
            FixtureArchive = new(m_store, m_target, m_owner);
        }

        // These laws test orchestration and persistence in one compiled engine. Packaged-pair qualification
        // is a separate prerequisite and is deliberately not replaced by a fake qualification receipt here.
        public WorldReleaseManifest Manifest(char image) => new() {
            Label = "cutover-test", SourceRevision = new string(image, 40), EngineImageDigest = "sha256:" + new string(image, 64),
            Definitions = Identities.ToDictionary(identity => $"{identity.Owner:D}/{identity.World}", _ => "sha256/" + new string('d', 64)),
            DefinitionFiles = Identities.ToDictionary(identity => $"{identity.Owner:D}/{identity.World}", identity => $"{identity.World}.world.json"),
            Artifacts = new Dictionary<string, string>(), PersistenceContract = "puck.world.persistence.v1", PeerProtocolContract = "puck.world.peer.v1"
        };

        public async Task InitializeAsync(string activeRelease = "release-a") {
            var document = Fixtures.BuildDocument() with {
                HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None }
            };
            foreach (var identity in Identities) { Assert.True((await Authority.PublishDefinitionAsync(identity, document, Token)).Ok); }
            Required(await Groups.CreateAsync("primary", activeRelease, Token));
        }

        public (WorldSiloHost Host, SiloConsoleRouting Routing) Host(string release) {
            var source = new TextCommandSource(new CommandRegistry(modules: []));
            var routing = new SiloConsoleRouting(() => source, new SiloConsoleTagging(m_output));
            var rows = Identities.Select(identity => new WorldSiloWorldRow(m_owner, identity.World, new(m_keyFile), Pinned: true)).ToArray();
            var definition = new WorldSiloDefinition(rows, new(2), new("directory", JsonElement.Parse("{}")), m_directory.RootPath, new("Localhost"), Release: new("primary", m_owner, release));
            return (new WorldSiloHost(definition, m_store, routing, m_target), routing);
        }

        public async Task<WorldReleaseGroupSnapshot> AdvanceAsync(WorldReleaseGroupSnapshot current, WorldReleaseOperationPhase phase) {
            var roots = current.Record.RecoveryRoots;
            if (phase == WorldReleaseOperationPhase.Drain) {
                var captured = new Dictionary<string, string>();
                foreach (var identity in Identities) {
                    var root = (await Authority.LoadRootAsync(identity, Token))!.Value;
                    captured[$"{identity.Owner:D}/{identity.World}"] = root.VersionToken;
                }
                roots = captured;
            }
            return Required(await Groups.AdvanceAsync(current, current.Record with {
                PendingPhase = phase, Admission = WorldReleaseAdmissionState.Closed, RecoveryRoots = roots, Revision = current.Record.Revision + 1
            }, Token));
        }

        public async Task<List<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> FencesAsync() {
            var result = new List<(WorldAuthorityIdentity, WorldAuthorityFence)>();
            foreach (var identity in Identities) {
                var root = (await Authority.LoadRootAsync(identity, Token))!.Value;
                result.Add((identity, new(root.Root.Epoch, root.Root.FenceToken, root.VersionToken)));
            }
            return result;
        }

        public void Dispose() { m_output.Dispose(); m_directory.Dispose(); }
    }
}
