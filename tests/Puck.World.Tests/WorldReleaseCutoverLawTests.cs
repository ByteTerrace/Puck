using System.Security.Cryptography;
using System.Text.Json;
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

        public Scenario() {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            m_keyFile = m_directory.WriteBytes("world.key", key.ExportPkcs8PrivateKey());
            m_target = new(m_directory.RootPath);
            Identities = [new(m_owner, SafeName.Parse("alpha")), new(m_owner, SafeName.Parse("beta"))];
            Authority = new(m_store, m_target);
            Groups = new(m_store, m_target, m_owner);
        }

        public async Task InitializeAsync() {
            var document = Fixtures.BuildDocument() with {
                HostRaw = Fixtures.StandardHost with { Authority = "localhost:7825", Listen = null, Presentation = WorldHostPresentation.None }
            };
            foreach (var identity in Identities) { Assert.True((await Authority.PublishDefinitionAsync(identity, document, Token)).Ok); }
            Required(await Groups.CreateAsync("primary", "release-a", Token));
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
