using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Independent instance lifetime and candidate preparation, without emulator or renderer assumptions.</summary>
public sealed class NamedMachineLifetimeLawTests {
    [Fact]
    public void OrdinaryMutationAndUndoPrepareDevicesAtTheAuthorityBoundary() {
        var engine = new CounterEngine();
        using var fixture = Fixtures.FreshServer(Document(), engines: [engine]);
        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(WorldPrincipal.Console, Row("clock")));
        fixture.Step();
        var original = Assert.Single(engine.Created);
        Assert.NotNull(fixture.Server.Machines.InstanceState("clock"));

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(WorldPrincipal.Console, Row("clock", -1)));
        fixture.Step();
        Assert.Single(engine.Created);
        Assert.False(original.Disposed);
        Assert.Equal(0, fixture.Server.Definition.Machines[0].Configuration.GetProperty("seed").GetInt32());

        fixture.Server.EnqueueMutation(new WorldMutation.RemoveMachine(WorldPrincipal.Console, "clock"));
        fixture.Step();
        Assert.True(original.Disposed);
        Assert.Null(fixture.Server.Machines.InstanceState("clock"));

        fixture.Server.EnqueueUndo(count: 1, principal: WorldPrincipal.Console);
        fixture.Step();
        Assert.NotNull(fixture.Server.Machines.InstanceState("clock"));
        Assert.Equal(2, engine.Created.Count);
        Assert.False(engine.Created[1].Disposed);
    }
    [Fact]
    public void WorldBootsAndAdvancesADeviceWithoutContentOrScreens() {
        var engine = new CounterEngine();
        using var fixture = Fixtures.FreshServer(Document(Row("clock")), engines: [engine]);
        var runtime = Assert.Single(engine.Created);
        fixture.Step(840);
        fixture.Step(420);
        Assert.Equal(1260UL, runtime.Ticks);
        Assert.Equal("clock", Assert.Single(fixture.Server.Machines.InstanceNames));
        Assert.Equal(2, fixture.Server.Machines.InstanceState("clock")!.Value.FramesStepped);
        Assert.Null(fixture.Server.Machines.VideoOutput("clock", "video"));
        Assert.Null(fixture.Server.Machines.AudioOutput("clock", "audio"));
        Assert.Equal(MachineAccessStatus.Unsupported, fixture.Server.Machines.Inspect("clock", new("bus", 0, 1)).Status);
        Assert.Equal(MachineAccessStatus.Unavailable, fixture.Server.Machines.Inspect("absent", new("bus", 0, 1)).Status);
    }

    [Fact]
    public void RemovingADisplayPreservesTheMachineAndItsGeneration() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        var document = Document(Row("clock")) with { ScreensRaw = [Panel()] };
        Install(host, null, document);
        var before = host.InstanceState("clock")!.Value.Generation;
        var runtime = Assert.Single(engine.Created);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Install(host, document, document with { ScreensRaw = null });
        host.Advance(420, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(before, host.InstanceState("clock")!.Value.Generation);
        Assert.Single(engine.Created);
        Assert.False(runtime.Disposed);
        Assert.Equal(1260UL, runtime.Ticks);
    }

    [Fact]
    public void AStoppedInstanceRetainsStateAndResumesWithoutReplacement() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        var running = Document(Row("clock"));
        Install(host, null, running);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        var stopped = Document(Row("clock") with { Running = false });
        Install(host, running, stopped);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.False(host.InstanceState("clock")!.Value.Running);
        var runtime = Assert.Single(engine.Created);
        Assert.Equal(840UL, runtime.Ticks);
        Install(host, stopped, running);
        host.Advance(420, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Single(engine.Created);
        Assert.Equal(1260UL, runtime.Ticks);
    }

    [Fact]
    public void AFaultLaterInPreparationDisposesOnlyStagedDevices() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        var live = Document(Row("clock"));
        Install(host, null, live);
        var before = host.InstanceState("clock");
        var replacement = Document(Row("clock", 1), Row("broken", -1));
        Assert.False(host.TryPrepare(live, replacement, out var plan, out var reason));
        Assert.Null(plan);
        Assert.Contains("broken", reason, StringComparison.Ordinal);
        Assert.Equal(before, host.InstanceState("clock"));
        Assert.Equal(2, engine.Created.Count);
        Assert.False(engine.Created[0].Disposed);
        Assert.True(engine.Created[1].Disposed);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(840UL, engine.Created[0].Ticks);
    }

    [Fact]
    public void AbandoningAValidReplacementLeavesTheLiveDeviceUsable() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        var live = Document(Row("clock"));
        Install(host, null, live);
        Assert.True(host.TryPrepare(live, Document(Row("clock", 1)), out var plan, out var reason), reason);
        plan!.Dispose();
        Assert.False(engine.Created[0].Disposed);
        Assert.True(engine.Created[1].Disposed);
        Assert.Throws<InvalidOperationException>(() => host.Commit(plan));
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(840UL, engine.Created[0].Ticks);
    }

    [Fact]
    public void StalePlansCannotOverwriteANewerIncarnation() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        var live = Document(Row("clock"));
        Install(host, null, live);
        var generation = host.InstanceState("clock")!.Value.Generation;
        Assert.True(host.TryPrepare(live, Document(Row("clock", 1)), out var stale, out var reason), reason);
        using (stale) {
            Install(host, live, Document(Row("clock", 2)));
            Assert.Throws<InvalidOperationException>(() => host.Commit(stale!));
        }
        Assert.True(host.InstanceState("clock")!.Value.Generation > generation);
        Assert.True(engine.Created[0].Disposed);
        Assert.True(engine.Created[1].Disposed);
        Assert.False(engine.Created[2].Disposed);
    }

    [Fact]
    public void UnknownConfigurationAndDuplicateNamesRefuseBeforeConstruction() {
        var engine = new CounterEngine();
        using var host = Host(engine);
        Assert.False(host.TryPrepare(null, Document(Row("clock"), Row("clock")), out _, out var reason));
        Assert.Contains("duplicate instance", reason, StringComparison.Ordinal);
        var invalid = Row("clock") with { Configuration = Json("{\"schema\":\"puck.counter.config.v1\",\"typo\":1}") };
        Assert.False(host.TryPrepare(null, Document(invalid), out _, out reason));
        Assert.Contains("configuration.typo", reason, StringComparison.Ordinal);
        Assert.Empty(engine.Created);
    }

    private static WorldMachineHost Host(CounterEngine engine) => new([], new WorldMachineCatalog([engine]));
    private static WorldDefinition Document(params WorldMachine[] machines) => Fixtures.BuildDocument() with { MachinesRaw = machines, ScreensRaw = null };
    private static WorldMachine Row(string name, int seed = 0) => new(name, "counter", Json($"{{\"schema\":\"puck.counter.config.v1\",\"seed\":{seed}}}"));
    private static JsonElement Json(string json) {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
    private static void Install(WorldMachineHost host, WorldDefinition? current, WorldDefinition next) {
        Assert.True(host.TryPrepare(current, next, out var plan, out var reason), reason);
        using (plan) {
            host.Commit(plan!);
            host.Finish(plan!);
        }
    }
    private static WorldScreen Panel() => new(0, Vector3.Zero, Vector3.UnitX, Vector3.UnitY, 1, 1, 0.01f, 0,
        new WorldScreenSource.None(), WorldScreenRoute.Passive);

    private sealed class CounterEngine : IMachineEngine {
        public string Id => "counter";
        public List<CounterRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new("counter", "Counter test device",
            new("puck.counter.config.v1", [new("seed", MachineFieldKind.Integer, "Initial counter")]), [], [], [], [], []);
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            if (request.Configuration.GetProperty("seed").GetInt32() < 0) {
                throw new ArgumentException("This device refuses a negative seed.");
            }
            var runtime = new CounterRuntime();
            Created.Add(runtime);
            return runtime;
        }
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            throw new InvalidOperationException("The device accepts structured construction only.");
    }

    private sealed class CounterRuntime : IMachineRuntime {
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Ticks { get; private set; }
        public bool Disposed { get; private set; }
        public bool Advance(ulong deltaTicks) {
            Assert.False(Disposed);
            Ticks += deltaTicks;
            return true;
        }
        public void Dispose() {
            Assert.False(Disposed);
            Disposed = true;
        }
    }
}
