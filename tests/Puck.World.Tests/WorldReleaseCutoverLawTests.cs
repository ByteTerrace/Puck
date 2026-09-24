using Puck.Testing;
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
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises release admission through real hosted rows and the production simulation pump.</summary>
public sealed partial class WorldReleaseCutoverLawTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public async Task FixtureRootReadExcludesLaterMutationsAndCancellationDoesNotPoisonTheQueue(bool cancel) {
        var hooked = new FixtureReadStore(inner: PuckStorageTestComposition.BuildStore());
        using var scenario = new Scenario(store: hooked);
        var release = scenario.Manifest(image: 'a').Identity;

        await scenario.InitializeAsync(activeRelease: release);
        var host = scenario.Host(release: release).Host;
        using var instances = host.Instances;

        await ActivateAllAsync(
            host: host,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: host)
        );
        Tick(
            count: 3,
            host: host
        );
        var identity = scenario.Identities[0];
        var before = (await scenario.Authority.LoadRootAsync(
            identity,
            Token
        ))!.Value;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token: Token);

        hooked.Armed = true;
        var exporting = host.ExportReleaseFixtureAsync(
            Guid.NewGuid(),
            cancellation.Token
        );

        await PumpAsync(
            host: host,
            operation: hooked.Entered.Task
        );
        WorldReleaseFixtureManifest manifest;

        if (cancel) {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(testCode: () => exporting);
            var retry = host.ExportReleaseFixtureAsync(
                Guid.NewGuid(),
                Token
            );

            await PumpAsync(
                host: host,
                operation: retry
            );
            manifest = await retry;
        } else {
            Assert.True(condition: host.Instances.TryGet(
                identity.World.Value,
                out var row
            ));
            row!.Server.EnqueueMutation(new WorldMutation.SetRenderDefaults(
                Principal: Principal.Console,
                Render: row.Server.Definition.Render with { AmbientOcclusion = !row.Server.Definition.Render.AmbientOcclusion }
            ));
            Tick(
                count: 1,
                host: host
            );
            hooked.Continue.TrySetResult();
            await PumpAsync(
                host: host,
                operation: exporting
            );
            manifest = await exporting;
        }
        var history = await scenario.FixtureArchive.ReadReceiptsAsync(
            manifest,
            identity.World.Value,
            Token
        );

        Assert.Equal(
            before.Root.JournalSequence,
            history.Source.Root.JournalSequence
        );
        Assert.Equal(
            before.Root.ReceiptIndexHash,
            history.Source.Root.ReceiptIndexHash
        );
        Assert.True(condition: host.ReleaseAdmissionOpen);
        await PumpAsync(
            host: host,
            operation: host.DrainAsync(ct: Token)
        );
        var final = (await scenario.Authority.LoadRootAsync(
            identity,
            Token
        ))!.Value;

        if (!cancel) { Assert.True(condition: (final.Root.JournalSequence > history.Source.Root.JournalSequence)); }
    }

    private sealed class FixtureReadStore(IObjectBlobStore inner) : IObjectBlobStore {
        public bool Armed;

        public TaskCompletionSource Entered { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<IReadOnlyList<string>> ListAsync(ObjectStorageTarget target, Guid owner, string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(
            cancellationToken: cancellationToken,
            keyPrefix: prefix,
            objectId: owner,
            target: target
        );
        public async ValueTask<ObjectBlobContent?> ReadAsync(ObjectStorageTarget target, ObjectBlobAddress address, CancellationToken cancellationToken = default) {
            if (
                Armed &&
                address.Key.EndsWith(
                comparisonType: StringComparison.Ordinal,
                value: "/alpha/authority/root"
            )
            ) {
                Armed = false;
                Entered.TrySetResult();
                await Continue.Task.WaitAsync(cancellationToken: cancellationToken);
            }
            return await inner.ReadAsync(
                address: address,
                cancellationToken: cancellationToken,
                target: target
            );
        }
        public ValueTask<ObjectBlobWriteResult> WriteAsync(ObjectStorageTarget target, ObjectBlobAddress address, ReadOnlyMemory<byte> content, ObjectBlobWriteMode mode, string? ifMatchVersion = null, CancellationToken cancellationToken = default) => inner.WriteAsync(
            address: address,
            cancellationToken: cancellationToken,
            content: content,
            ifMatchVersion: ifMatchVersion,
            mode: mode,
            target: target
        );
    }

    [Fact]
    public async Task QualificationExportCapturesBothRowsWithoutDrainingAndRetryRetainsTheOriginalBoundary() {
        using var scenario = new Scenario();
        var release = scenario.Manifest(image: 'a').Identity;

        await scenario.InitializeAsync(activeRelease: release);
        var host = scenario.Host(release: release).Host;
        using var instances = host.Instances;

        await ActivateAllAsync(
            host: host,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: host)
        );
        Tick(
            count: 5,
            host: host
        );
        var expected = Ticks(
            host: host,
            identities: scenario.Identities
        );
        var before = await scenario.FencesAsync();
        var request = Guid.NewGuid();
        var exporting = host.ExportReleaseFixtureAsync(
            request,
            Token
        );

        await PumpAsync(
            host: host,
            operation: exporting
        );
        var manifest = await exporting;

        Assert.Equal(
            expected.Keys.Order(comparer: StringComparer.Ordinal),
            manifest.Worlds.Keys
        );
        Assert.Equal(
            release,
            manifest.Release
        );
        Assert.False(condition: host.IsDraining);
        Assert.True(condition: host.ReleaseAdmissionOpen);
        foreach (var world in manifest.Worlds) {
            Assert.Equal(
                expected[world.Key],
                world.Value.Tick
            );
            var bytes = await scenario.FixtureArchive.ReadCheckpointAsync(
                manifest,
                world.Key,
                Token
            );

            Assert.True(
                condition: WorldAuthorityCheckpointCodec.TryDecode(
                    bytes: bytes.Span,
                    checkpoint: out var checkpoint,
                    reason: out var reason
                ),
                userMessage: reason
            );
            Assert.Equal(
                expected[world.Key],
                checkpoint!.Server.LastCompletedTick
            );
        }
        Assert.Equal(
            before,
            await scenario.FencesAsync()
        );
        Tick(
            count: 3,
            host: host
        );
        Assert.All(
            Ticks(
                host: host,
                identities: scenario.Identities
            ),
            row => Assert.True(condition: (row.Value > expected[row.Key]))
        );
        var retrying = host.ExportReleaseFixtureAsync(
            request,
            Token
        );

        await PumpAsync(
            host: host,
            operation: retrying
        );
        Assert.Equal(
            manifest.Identity,
            (await retrying).Identity
        );
        var group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Required(outcome: await scenario.Groups.BeginAsync(
            group,
            Guid.NewGuid(),
            scenario.Manifest(image: 'b').Identity,
            Token
        ));
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => host.ExportReleaseFixtureAsync(
            Guid.NewGuid(),
            Token
        ));
        Assert.False(condition: host.IsDraining);
        await PumpAsync(
            host: host,
            operation: host.DrainAsync(ct: Token)
        );
    }
    [Fact]
    public async Task CoordinatorDeployAndRollbackRetainLatestGameplayAcrossBothRows() {
        using var scenario = new Scenario();
        var a = scenario.Manifest(image: 'a');
        var b = scenario.Manifest(image: 'b');

        await scenario.InitializeAsync(activeRelease: a.Identity);
        var source = scenario.Host(release: a.Identity).Host;
        var candidate = scenario.Host(release: b.Identity).Host;
        var rollback = scenario.Host(release: a.Identity).Host;
        using var sourceInstances = source.Instances;
        using var candidateInstances = candidate.Instances;
        using var rollbackInstances = rollback.Instances;

        await ActivateAllAsync(
            host: source,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: source)
        );
        Tick(
            count: 5,
            host: source
        );
        var before = Ticks(
            host: source,
            identities: scenario.Identities
        );
        var coordinator = new WorldReleaseCoordinator(groups: scenario.Groups);
        var group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        group = Required(outcome: await scenario.Groups.BeginAsync(
            group,
            Guid.NewGuid(),
            b.Identity,
            Token
        ));
        var runtime = new LoopbackRuntime(
            new WorldSiloReleaseRuntime(
                source,
                candidate,
                scenario.Identities,
                () => throw new InvalidOperationException(message: "successful deploy must not recover")
            ),
            source,
            candidate
        );
        var deploying = coordinator.ResumeAsync(
            group,
            b,
            runtime,
            Token
        );

        await PumpAllAsync(
            hosts: [source, candidate],
            operation: deploying
        );
        Assert.True(
            condition: (await deploying).Completed,
            userMessage: (await deploying).Detail
        );
        AssertTicks(
            expected: before,
            host: candidate,
            identities: scenario.Identities
        );
        await Assert.ThrowsAsync<InvalidOperationException>(testCode: () => source.CaptureReleaseRootsAsync(
            group.Record.PendingOperationId!.Value,
            Token
        ));
        Tick(
            count: 7,
            host: candidate
        );
        var latest = Ticks(
            host: candidate,
            identities: scenario.Identities
        );

        foreach (var row in before) { Assert.True(condition: (latest[row.Key] > row.Value)); }

        group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        Assert.NotNull(value: group.Record.PendingOperationId);
        Assert.False(condition: group.Record.HasUnfinishedOperation);
        var captureRequest = Guid.NewGuid();
        var rollbackCapture = ControlAsync(
            candidate,
            "POST",
            $"/release/fixture/{captureRequest:D}"
        );

        await PumpAsync(
            host: candidate,
            operation: rollbackCapture
        );
        Assert.Equal(
            200,
            (await rollbackCapture).Status
        );
        var rollbackFixture = (await scenario.FixtureArchive.LoadAsync(
            captureRequest,
            Token
        ))!;

        Assert.Equal(
            b.Identity,
            rollbackFixture.Release
        );
        foreach (var row in latest) {
            Assert.Equal(
            row.Value,
            rollbackFixture.Worlds[row.Key].Tick
        );
        }
        group = Required(outcome: await scenario.Groups.BeginRollbackAsync(
            group,
            Guid.NewGuid(),
            Token
        ));
        Assert.True(condition: group.Record.HasUnfinishedOperation);
        Assert.Equal(
            409,
            (await ControlAsync(
                candidate,
                "POST",
                $"/release/fixture/{Guid.NewGuid():D}"
            )).Status
        );
        var rollbackRuntime = new LoopbackRuntime(
            new WorldSiloReleaseRuntime(
                candidate,
                rollback,
                scenario.Identities,
                () => throw new InvalidOperationException(message: "successful rollback must not restore an old save")
            ),
            candidate,
            rollback
        );
        var rollingBack = coordinator.ResumeAsync(
            group,
            a,
            rollbackRuntime,
            Token
        );

        await PumpAllAsync(
            hosts: [candidate, rollback],
            operation: rollingBack
        );
        Assert.True(
            condition: (await rollingBack).Completed,
            userMessage: (await rollingBack).Detail
        );
        AssertTicks(
            expected: latest,
            host: rollback,
            identities: scenario.Identities
        );
        Assert.True(condition: rollback.ReleaseAdmissionOpen);
        var durable = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        Assert.Equal(
            a.Identity,
            durable.Record.ActiveRelease
        );
        Assert.Equal(
            b.Identity,
            durable.Record.PreviousRelease
        );
        Assert.All(
            durable.Record.RecoveryRoots.Values,
            pin => Assert.StartsWith(
                actualString: pin,
                expectedStartString: "sha256/"
            )
        );
        Required(outcome: await scenario.Groups.FinalizeAsync(
            durable,
            Token
        ));
        await PumpAsync(
            host: rollback,
            operation: rollback.DrainAsync(ct: Token)
        );
    }
    [Fact]
    public async Task FailedPrivateCandidateRecoversUnderFreshFencesAndResumesAfterRecoveryRestart() {
        using var scenario = new Scenario();
        var a = scenario.Manifest(image: 'a');
        var b = scenario.Manifest(image: 'b');

        await scenario.InitializeAsync(activeRelease: a.Identity);
        var source = scenario.Host(release: a.Identity).Host;
        var candidate = scenario.Host(release: b.Identity).Host;
        var recovery = scenario.Host(release: a.Identity).Host;
        using var sourceInstances = source.Instances;
        using var candidateInstances = candidate.Instances;
        using var recoveryInstances = recovery.Instances;

        await ActivateAllAsync(
            host: source,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: source)
        );
        Tick(
            count: 5,
            host: source
        );
        var before = Ticks(
            host: source,
            identities: scenario.Identities
        );
        var oldFences = await scenario.FencesAsync();
        var group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        group = Required(outcome: await scenario.Groups.BeginAsync(
            group,
            Guid.NewGuid(),
            b.Identity,
            Token
        ));
        var runtime = new WorldSiloReleaseRuntime(
            source,
            candidate,
            scenario.Identities,
            () => recovery
        );
        var failing = new FailureRuntime(inner: runtime);
        var deploying = new WorldReleaseCoordinator(groups: scenario.Groups).ResumeAsync(
            group,
            b,
            failing,
            Token
        );

        await PumpAllAsync(
            hosts: [source, candidate, recovery],
            operation: deploying
        );
        Assert.False(condition: (await deploying).Completed);
        Assert.False(condition: candidate.ReleaseAdmissionOpen);
        group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        Assert.Equal(
            WorldReleaseOperationPhase.RecoverActivate,
            group.Record.PendingPhase
        );
        Assert.Equal(
            WorldReleaseAdmissionState.Closed,
            group.Record.Admission
        );

        // A new coordinator and adapter reconstruct recovery from the durable phase, without restoring twice.
        var resumedRuntime = new WorldSiloReleaseRuntime(
            source,
            candidate,
            scenario.Identities,
            () => recovery
        );
        var resuming = new WorldReleaseCoordinator(groups: scenario.Groups).ResumeAsync(
            group,
            b,
            resumedRuntime,
            Token
        );

        await PumpAllAsync(
            hosts: [source, candidate, recovery],
            operation: resuming
        );
        var result = await resuming;

        Assert.False(condition: result.Completed);
        Assert.True(
            condition: result.SourceRecovered,
            userMessage: result.Detail
        );
        AssertTicks(
            expected: before,
            host: recovery,
            identities: scenario.Identities
        );
        Assert.True(condition: recovery.ReleaseAdmissionOpen);
        group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;
        Assert.Null(value: group.Record.PendingOperationId);
        Assert.Equal(
            a.Identity,
            group.Record.ActiveRelease
        );
        var newFences = await scenario.FencesAsync();

        foreach (var old in oldFences) {
            var fresh = Assert.Single(
                collection: newFences,
                predicate: row => (row.Identity == old.Identity)
            );

            Assert.True(condition: (fresh.Fence.Epoch > old.Fence.Epoch));
            Assert.NotEqual(
                old.Fence.Token,
                fresh.Fence.Token
            );
        }
        await PumpAsync(
            host: recovery,
            operation: recovery.DrainAsync(ct: Token)
        );
    }

    private sealed class FailureRuntime(IWorldReleaseRuntime inner) : IWorldReleaseRuntime {
        public Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.DrainSourceAndCaptureAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishCandidateAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishRecoveredSourceAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.ReadFenceCensusAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.RecoverSourceAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartCandidatePrivatelyAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Task.FromResult(result: new WorldReleaseRuntimeResult(
            false,
            "injected recovery worker interruption"
        ));
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StopCandidateAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => Task.FromResult(result: new WorldReleaseRuntimeResult(
            false,
            "injected private probe failure"
        ));
    }

    [Fact]
    public async Task ReleaseControlRejectsRemoteAndWrongOperationBeforeWorkerEffects() {
        using var scenario = new Scenario();

        await scenario.InitializeAsync();
        var host = scenario.Host(release: "release-a").Host;
        using var instances = host.Instances;
        var remote = await ControlAsync(
            host: host,
            method: "GET",
            path: "/release/status",
            remote: IPAddress.Parse(ipString: "203.0.113.1")
        );

        Assert.Equal(
            actual: remote.Status,
            expected: 404
        );
        var stale = await ControlAsync(
            host,
            "POST",
            $"/release/drain/{Guid.NewGuid():D}"
        );

        Assert.Equal(
            actual: stale.Status,
            expected: 409
        );
        Assert.False(condition: host.IsDraining);
        var status = await ControlAsync(
            host,
            "GET",
            "/release/status"
        );

        Assert.Equal(
            actual: status.Status,
            expected: 200
        );
        Assert.Equal(
            "release-a",
            JsonSerializer.Deserialize<WorldReleaseWorkerStatus>(
                json: status.Body,
                options: new JsonSerializerOptions(defaults: JsonSerializerDefaults.Web)
            )!.Release
        );
    }

    // The worker-side controls execute against actual hosts; only the Azure transport is replaced by loopback HTTP contexts.
    private sealed class LoopbackRuntime(IWorldReleaseRuntime inner, WorldSiloHost source, WorldSiloHost candidate,
        Func<WorldReleaseGroupRecord, Task>? prepare = null) : IWorldReleaseRuntime {
        private static readonly JsonSerializerOptions Options = new(defaults: JsonSerializerDefaults.Web);

        public async Task<WorldReleaseRuntimeResult> DrainSourceAndCaptureAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(
                source,
                "POST",
                $"/release/drain/{operation.PendingOperationId:D}"
            );

            return new(
                (result.Status == 200),
                result.Body,
                ((result.Status == 200)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(
                        json: result.Body,
                        options: Options
                    )
                : null)
            );
        }
        public async Task<WorldReleaseRuntimePublication> PublishCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(
                candidate,
                "POST",
                $"/release/publish/{operation.PendingOperationId:D}"
            );

            return ((result.Status == 200)
                ? WorldReleaseRuntimePublication.Opened
                : WorldReleaseRuntimePublication.Refused
            );
        }
        public Task<WorldReleaseRuntimePublication> PublishRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.PublishRecoveredSourceAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public async Task<IReadOnlyList<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> ReadFenceCensusAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            var result = await ControlAsync(
                candidate,
                "GET",
                $"/release/fences/{operation.PendingOperationId:D}"
            );

            Assert.Equal(
                actual: result.Status,
                expected: 200
            );
            return JsonSerializer.Deserialize<WorldReleaseWorkerFence[]>(
                json: result.Body,
                options: Options
            )!.Select(selector: row =>
                (new WorldAuthorityIdentity(
                Owner: row.Owner,
                World: SafeName.Parse(candidate: row.World)
            ), new WorldAuthorityFence(
                row.Epoch,
                row.Token,
                row.RootVersion
            ))).ToArray();
        }
        public Task<WorldReleaseRuntimeResult> RecoverSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.RecoverSourceAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public async Task<WorldReleaseRuntimeResult> StartCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) {
            if (
                (prepare is not null) &&
                (operation.PendingPhase == WorldReleaseOperationPhase.Activate)
            ) { await prepare(operation); }
            return await inner.StartCandidatePrivatelyAsync(
                cancellationToken: cancellationToken,
                operation: operation
            );
        }
        public Task<WorldReleaseRuntimeResult> StartRecoveredSourceAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StartRecoveredSourceAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> StopCandidateAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.StopCandidateAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
        public Task<WorldReleaseRuntimeResult> VerifyCandidatePrivatelyAsync(WorldReleaseGroupRecord operation, CancellationToken cancellationToken = default) => inner.VerifyCandidatePrivatelyAsync(
            cancellationToken: cancellationToken,
            operation: operation
        );
    }

    private static async Task<(int Status, string Body)> ControlAsync(WorldSiloHost host, string method, string path, IPAddress? remote = null) {
        using var services = new ServiceCollection().BuildServiceProvider();
        using var body = new MemoryStream();
        var context = new DefaultHttpContext { RequestAborted = Token, RequestServices = services };

        context.Connection.RemoteIpAddress = (remote ?? IPAddress.Loopback);
        context.Request.Method = method;
        context.Request.Path = path;
        context.Response.Body = body;
        Assert.True(condition: await new WorldSiloReleaseControl(silo: host).HandleAsync(context: context));
        return (context.Response.StatusCode, System.Text.Encoding.UTF8.GetString(bytes: body.ToArray()));
    }
    private static Task PumpAllAsync(IReadOnlyList<WorldSiloHost> hosts, Task operation) => WorldSiloHost.PumpActivationMailboxesAsync(
        cancellationToken: Token,
        hosts: hosts,
        operation: operation
    );

    [Fact]
    public async Task TwoRowCutoverKeepsCandidatesFrozenAndCommittedRestartPreservesProgress() {
        using var scenario = new Scenario();

        await scenario.InitializeAsync();
        var (source, sourceRouting) = scenario.Host(release: "release-a");
        using var sourceInstances = source.Instances;

        await ActivateAllAsync(
            host: source,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: source)
        );
        Tick(
            count: 3,
            host: source
        );
        var sourceTicks = Ticks(
            host: source,
            identities: scenario.Identities
        );

        Assert.All(
            sourceTicks.Values,
            tick => Assert.True(condition: (tick > 0))
        );
        await PumpAsync(
            host: source,
            operation: source.DrainAsync(ct: Token)
        );

        var operation = Guid.NewGuid();
        var group = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value;

        group = Required(outcome: await scenario.Groups.BeginAsync(
            group,
            operation,
            "release-b",
            Token
        ));
        group = await scenario.AdvanceAsync(
            current: group,
            phase: WorldReleaseOperationPhase.Drain
        );
        group = await scenario.AdvanceAsync(
            current: group,
            phase: WorldReleaseOperationPhase.Activate
        );

        // A delayed source process must be refused before it can replace any candidate fence.
        var (obsolete, _) = scenario.Host(release: "release-a");
        using var obsoleteInstances = obsolete.Instances;
        var before = await scenario.Authority.LoadRootAsync(
            scenario.Identities[0],
            Token
        );
        var obsoleteActivation = obsolete.ActivateAsync(
            scenario.Identities[0],
            Token
        );

        await PumpAsync(
            host: obsolete,
            operation: obsoleteActivation
        );
        Assert.False(condition: await obsoleteActivation);
        Assert.Equal(
            before,
            await scenario.Authority.LoadRootAsync(
                scenario.Identities[0],
                Token
            )
        );

        var (candidate, candidateRouting) = scenario.Host(release: "release-b");
        using var candidateInstances = candidate.Instances;

        await ActivateAllAsync(
            host: candidate,
            identities: [scenario.Identities[0]]
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Refused,
            await PublishAsync(host: candidate)
        );
        Assert.False(condition: candidate.ReleaseAdmissionOpen);
        Assert.False(condition: candidateRouting.TryGetSession(
            session: out _,
            worldId: "alpha"
        ));
        await ActivateAllAsync(
            host: candidate,
            identities: [scenario.Identities[1]]
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.CandidatePrivate,
            await PublishAsync(host: candidate)
        );
        Tick(
            count: 5,
            host: candidate
        );
        AssertTicks(
            expected: sourceTicks,
            host: candidate,
            identities: scenario.Identities
        );
        Assert.False(condition: candidateRouting.TryGetSession(
            session: out _,
            worldId: "alpha"
        ));
        Assert.False(condition: candidateRouting.TryGetSession(
            session: out _,
            worldId: "beta"
        ));

        group = await scenario.AdvanceAsync(
            current: group,
            phase: WorldReleaseOperationPhase.Verify
        );
        group = Required(outcome: await scenario.Groups.CommitAsync(
            group,
            await scenario.FencesAsync(),
            Token
        ));
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: candidate)
        );
        Assert.True(condition: candidateRouting.TryGetSession(
            session: out _,
            worldId: "alpha"
        ));
        Assert.True(condition: candidateRouting.TryGetSession(
            session: out _,
            worldId: "beta"
        ));
        Tick(
            count: 4,
            host: candidate
        );
        var committedTicks = Ticks(
            host: candidate,
            identities: scenario.Identities
        );

        foreach (var row in sourceTicks) { Assert.True(condition: (committedTicks[row.Key] > row.Value)); }
        await PumpAsync(
            host: candidate,
            operation: candidate.DrainAsync(ct: Token)
        );

        var (replacement, replacementRouting) = scenario.Host(release: "release-b");
        using var replacementInstances = replacement.Instances;

        await ActivateAllAsync(
            host: replacement,
            identities: scenario.Identities
        );
        Tick(
            count: 3,
            host: replacement
        );
        AssertTicks(
            expected: committedTicks,
            host: replacement,
            identities: scenario.Identities
        );
        Assert.Equal(
            WorldReleaseAdmissionPublication.Opened,
            await PublishAsync(host: replacement)
        );
        Assert.True(condition: replacementRouting.TryGetSession(
            session: out _,
            worldId: "alpha"
        ));
        Assert.True(condition: replacementRouting.TryGetSession(
            session: out _,
            worldId: "beta"
        ));
        var resumed = (await scenario.Groups.LoadAsync(
            "primary",
            Token
        ))!.Value.Record;

        Assert.Equal(
            operation,
            resumed.PendingOperationId
        );
        Assert.Equal(
            "release-b",
            resumed.ActiveRelease
        );
        Assert.Equal(
            "release-a",
            resumed.PreviousRelease
        );
        Assert.True(condition: resumed.RollbackEligible);
        await PumpAsync(
            host: replacement,
            operation: replacement.DrainAsync(ct: Token)
        );
    }
    [Fact]
    public async Task StaleFenceInOneRowRefusesTheWholePublication() {
        using var scenario = new Scenario();

        await scenario.InitializeAsync();
        var (host, routing) = scenario.Host(release: "release-a");
        using var instances = host.Instances;

        await ActivateAllAsync(
            host: host,
            identities: scenario.Identities
        );
        Assert.NotNull(value: await scenario.Authority.AcquireActivationAsync(
            scenario.Identities[1],
            Token
        ));
        Assert.Equal(
            WorldReleaseAdmissionPublication.Refused,
            await PublishAsync(host: host)
        );
        Assert.False(condition: host.ReleaseAdmissionOpen);
        Assert.False(condition: routing.TryGetSession(
            session: out _,
            worldId: "alpha"
        ));
        Assert.False(condition: routing.TryGetSession(
            session: out _,
            worldId: "beta"
        ));
        var ticks = Ticks(
            host: host,
            identities: scenario.Identities
        );

        Tick(
            count: 3,
            host: host
        );
        AssertTicks(
            expected: ticks,
            host: host,
            identities: scenario.Identities
        );
    }

    private static void Tick(WorldSiloHost host, int count) {
        var simulation = new WorldSiloSimulation(host: host);

        for (var index = 0; (index < count); index++) {
            simulation.Step(
                new FixedStepContext(
                    ((ulong)index),
                    Fixtures.StepTicks,
                    Fixtures.StepTicks
                ),
                default
            );
        }
    }
    private static Dictionary<string, ulong> Ticks(WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) =>
        identities.ToDictionary(
            identity => identity.World.Value,
            identity => {
                Assert.True(condition: host.Instances.TryGet(
                    identity.World.Value,
                    out var instance
                ));
                return instance!.CompletedTicks;
            }
        );
    private static void AssertTicks(IReadOnlyDictionary<string, ulong> expected, WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) {
        foreach (var actual in Ticks(
            host: host,
            identities: identities
        )) {
            Assert.Equal(
            expected[actual.Key],
            actual.Value
        );
        }
    }
    private static async Task ActivateAllAsync(WorldSiloHost host, IEnumerable<WorldAuthorityIdentity> identities) {
        foreach (var identity in identities) {
            var activation = host.ActivateAsync(
                identity,
                Token
            );

            await PumpAsync(
                host: host,
                operation: activation
            );
            Assert.True(condition: await activation);
        }
    }
    private static async Task<WorldReleaseAdmissionPublication> PublishAsync(WorldSiloHost host) {
        var publication = host.PublishManagedReleaseAdmissionAsync(Token);

        await PumpAsync(
            host: host,
            operation: publication
        );
        return await publication;
    }
    private static Task PumpAsync(WorldSiloHost host, Task operation) => PumpAllAsync(
        hosts: [host],
        operation: operation
    );
    private static WorldReleaseGroupSnapshot Required(WorldReleaseGroupOutcome outcome) {
        Assert.True(
            condition: outcome.Ok,
            userMessage: outcome.Detail
        );
        return outcome.Snapshot!.Value;
    }

    private sealed class Scenario : IDisposable {
        private readonly TemporaryDirectory m_directory = new();
        private readonly BufferedConsoleOutput m_output = new();
        private readonly Guid m_owner = Guid.NewGuid();

        private readonly string m_keyFile;
        private readonly IObjectBlobStore m_store;
        private readonly DirectoryObjectStorageTarget m_target;

        public WorldAuthorityBlobStore Authority { get; }
        public WorldReleaseFixtureArchive FixtureArchive { get; }
        public WorldReleaseGroupStore Groups { get; }
        public WorldAuthorityIdentity[] Identities { get; }

        public Scenario(IObjectBlobStore? store = null) {
            m_store = (store ?? PuckStorageTestComposition.BuildStore());
            using var key = ECDsa.Create(curve: ECCurve.NamedCurves.nistP256);

            m_keyFile = m_directory.WriteBytes(
                "world.key",
                key.ExportPkcs8PrivateKey()
            );
            m_target = new(m_directory.RootPath);
            Identities = [new(
                    Owner: m_owner,
                    World: SafeName.Parse(candidate: "alpha")
                ), new(
                    Owner: m_owner,
                    World: SafeName.Parse(candidate: "beta")
                )];
            Authority = new(
                store: m_store,
                target: m_target
            );
            Groups = new(
                owner: m_owner,
                store: m_store,
                target: m_target
            );
            FixtureArchive = new(
                owner: m_owner,
                store: m_store,
                target: m_target
            );
        }

        public async Task<WorldReleaseGroupSnapshot> AdvanceAsync(WorldReleaseGroupSnapshot current, WorldReleaseOperationPhase phase) {
            var roots = current.Record.RecoveryRoots;

            if (phase == WorldReleaseOperationPhase.Drain) {
                var captured = new Dictionary<string, string>();

                foreach (var identity in Identities) {
                    var root = (await Authority.LoadRootAsync(
                        identity,
                        Token
                    ))!.Value;

                    captured[$"{identity.Owner:D}/{identity.World}"] = root.VersionToken;
                }
                roots = captured;
            }
            return Required(outcome: await Groups.AdvanceAsync(
                current,
                current.Record with {
                    PendingPhase = phase,
                    Admission = WorldReleaseAdmissionState.Closed,
                    RecoveryRoots = roots,
                    Revision = (current.Record.Revision + 1),
                },
                Token
            ));
        }
        public void Dispose() { m_output.Dispose(); m_directory.Dispose(); }
        public async Task<List<(WorldAuthorityIdentity Identity, WorldAuthorityFence Fence)>> FencesAsync() {
            var result = new List<(WorldAuthorityIdentity, WorldAuthorityFence)>();

            foreach (var identity in Identities) {
                var root = (await Authority.LoadRootAsync(
                    identity,
                    Token
                ))!.Value;

                result.Add(item: (identity, new(
                    root.Root.Epoch,
                    root.Root.FenceToken,
                    root.VersionToken
                )));
            }
            return result;
        }
        public (WorldSiloHost Host, SiloConsoleRouting Routing) Host(string release) {
            var source = new TextCommandSource(new CommandRegistry(modules: []));
            var routing = new SiloConsoleRouting(
                source: () => source,
                tagging: new SiloConsoleTagging(output: m_output)
            );
            var rows = Identities.Select(selector: identity => new WorldSiloWorldRow(
                m_owner,
                identity.World,
                new(KeyFile: m_keyFile),
                Pinned: true
            )).ToArray();
            var definition = new WorldSiloDefinition(
                rows,
                new(Budget: 2),
                new(
                    "directory",
                    JsonElement.Parse("{}")
                ),
                m_directory.RootPath,
                new(Kind: "Localhost"),
                Release: new(
                    ExpectedRelease: release,
                    Group: "primary",
                    Owner: m_owner
                )
            );

            return (new WorldSiloHost(
                definition,
                m_store,
                routing,
                m_target
            ), routing);
        }
        public async Task InitializeAsync(string activeRelease = "release-a") {
            var document = Fixtures.BuildDocument() with {
                HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None },
            };

            foreach (var identity in Identities) {
                Assert.True(condition: (await Authority.PublishDefinitionAsync(
                identity,
                document,
                Token
            )).Ok);
            }
            Required(outcome: await Groups.CreateAsync(
                "primary",
                activeRelease,
                Token
            ));
        }
        // These laws test orchestration and persistence in one compiled engine. Packaged-pair qualification
        // is a separate prerequisite and is deliberately not replaced by a fake qualification receipt here.
        public WorldReleaseManifest Manifest(char image) => new() {
            CoordinatorContract = WorldReleaseManifest.CurrentCoordinatorContract,
            Label = "cutover-test",
            SourceRevision = new string(
            c: image,
            count: 40
        ),
            EngineImageDigest = ("sha256:" + new string(
            c: image,
            count: 64
        )),
            Definitions = Identities.ToDictionary(
            identity => $"{identity.Owner:D}/{identity.World}",
            _ => ("sha256/" + new string(
                c: 'd',
                count: 64
            ))
        ),
            DefinitionFiles = Identities.ToDictionary(
            identity => $"{identity.Owner:D}/{identity.World}",
            identity => $"{identity.World}.world.json"
        ),
            Artifacts = new Dictionary<string, string>(),
            PersistenceContract = "puck.world.persistence.v1",
            PeerProtocolContract = "puck.world.peer.v1",
        };
    }
}
