using System.Numerics;
using System.Text.Json;
using Puck.Abstractions.Gpu;
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
    public void TwoDisplaysShareOneNamedProducerAndOneAdvance() {
        var engine = new CounterEngine(withOutputs: true);
        using var host = Host(engine);
        var screens = new[] { Panel() with { Source = new WorldScreenSource.Machine("clock", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("clock", "video") } };
        var document = Document(Row("clock")) with { ScreensRaw = screens };
        Install(host, null, document);

        var first = host.VideoOutput("clock", "video");
        var second = host.VideoOutput("clock", "video");
        Assert.NotNull(first);
        Assert.Same(first, second);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(1, host.InstanceState("clock")!.Value.FramesStepped);
        Assert.Single(engine.Created);
    }

    [Fact]
    public void PassiveFirstDisplayDoesNotMaskAnEngagedSecondDisplay_AndRemovalReleasesInput() {
        var engine = new InputEngine(portCount: 1);
        using var host = Host(engine);
        var first = Panel() with { Source = new WorldScreenSource.Machine("controls", "video") };
        var second = first with { Index = 1 };
        var document = Document(InputRow("controls")) with { ScreensRaw = [first, second] };
        Install(host, null, document);
        var runtime = Assert.Single(engine.Created);
        var pressed = MachinePadState.Neutral with { Buttons = MachineButtons.South };

        host.Advance(840, new ScreenPadSnapshot[] { new(1, pressed) });
        Assert.Equal(MachineButtons.South, runtime.LastInput.Buttons);

        _ = host.ReconcileScreens([first]);
        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(MachineButtons.None, runtime.LastInput.Buttons);
        Assert.False(runtime.Disposed);
    }

    [Fact]
    public void HeadlessMultiPortMachineAdvancesWithNeutralInputs_WhileDisplayedRouteIsRefused() {
        var engine = new InputEngine(portCount: 2);
        using var host = Host(engine);
        var headless = Document(InputRow("headless"));
        Install(host, null, headless);
        var runtime = Assert.Single(engine.Created);

        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(840UL, runtime.Ticks);
        Assert.All(runtime.Ports.Values, port => Assert.Equal(MachinePadState.Neutral, port.State));

        var displayed = headless with {
            ScreensRaw = [Panel() with { Source = new WorldScreenSource.Machine("headless", "video") }]
        };
        Assert.False(host.TryPrepare(headless, displayed, out var refused, out var reason));
        Assert.Null(refused);
        Assert.Contains("multiple input ports", reason, StringComparison.Ordinal);
        Assert.False(runtime.Disposed);
    }

    [Fact]
    public void NamedLinkMembersAdvanceOnce_AndRefusedRelinkPreservesTopology() {
        var engine = new CounterEngine(withOutputs: true, withLinking: true);
        using var host = Host(engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("right", "video") },
        };
        var document = Document(Row("left"), Row("right")) with { ScreensRaw = screens };
        Install(host, null, document);
        Assert.True(host.TryLink("cable", [0, 1]).Ok);
        var before = host.DescribeLinks();
        Assert.False(host.TryLink("other", [0, 1]).Ok);
        Assert.Equal(before, host.DescribeLinks());

        host.Advance(840, ReadOnlyMemory<ScreenPadSnapshot>.Empty);
        Assert.Equal(840UL, engine.Created[0].Ticks);
        Assert.Equal(840UL, engine.Created[1].Ticks);
        Assert.Equal(1, engine.Created[0].AdvanceCalls);
        Assert.Equal(1, engine.Created[1].AdvanceCalls);
    }

    [Fact]
    public void LinkedNamedReplacementAndOperationRefuseBeforeRetiringRuntime_AndUnlinkAllowsReplacement() {
        var engine = new CounterEngine(withOutputs: true, withLinking: true, withOperations: true);
        using var host = Host(engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("right", "video") },
        };
        var document = Document(Row("left"), Row("right")) with { ScreensRaw = screens };
        Install(host, null, document);
        Assert.True(host.TryLink("cable", [0, 1]).Ok);
        var beforeGeneration = host.InstanceState("left")!.Value.Generation;
        var beforeTopology = host.DescribeLinks();

        Assert.False(host.TryPrepare(document, Document(Row("left", 1), Row("right")) with { ScreensRaw = screens }, out var refusedPlan, out var refusal));
        Assert.Null(refusedPlan);
        Assert.Contains("unlink", refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeGeneration, host.InstanceState("left")!.Value.Generation);
        Assert.Equal(beforeTopology, host.DescribeLinks());
        Assert.False(engine.Created[0].Disposed);

        var operation = new MachineOperationRequest("counter.noop", Json("{}"));
        Assert.False(host.TryPrepareOperation("left", beforeGeneration, operation, out var operationPlan, out var operationRefusal));
        Assert.Null(operationPlan);
        Assert.Equal(MachineOperationStatus.Refused, operationRefusal.Status);
        Assert.Contains("unlink", operationRefusal.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(beforeGeneration, host.InstanceState("left")!.Value.Generation);
        Assert.Equal(beforeTopology, host.DescribeLinks());
        Assert.False(engine.Created[0].Disposed);

        Assert.True(host.TryUnlink("cable").Ok);
        Install(host, document, Document(Row("left", 1), Row("right")) with { ScreensRaw = screens });
        Assert.True(engine.Created[0].Disposed);
        Assert.True(host.InstanceState("left")!.Value.Generation > beforeGeneration);
        Assert.Equal("none", host.DescribeLinks());
    }

    [Fact]
    public void PreparedOperationRefusesIfALiveCableAppearsBeforeCommit() {
        var engine = new CounterEngine(withOutputs: true, withLinking: true, withOperations: true);
        using var host = Host(engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("right", "video") },
        };
        var document = Document(Row("left"), Row("right")) with { ScreensRaw = screens };
        Install(host, null, document);
        var generation = host.InstanceState("left")!.Value.Generation;
        var operation = new MachineOperationRequest("counter.noop", Json("{}"));
        Assert.True(host.TryPrepareOperation("left", generation, operation, out var plan, out var preparationRefusal), preparationRefusal.Reason);
        using (plan!) {
            Assert.True(host.TryLink("cable", [0, 1]).Ok);
            var result = host.TryCommitOperation(plan!);
            Assert.Equal(MachineOperationStatus.Refused, result.Status);
            Assert.Contains("unlink", result.Reason, StringComparison.OrdinalIgnoreCase);
        }
        Assert.Equal(generation, host.InstanceState("left")!.Value.Generation);
        Assert.False(engine.Created[0].Disposed);
        Assert.True(host.TryUnlink("cable").Ok);
    }
    [Fact]
    public void NamedScreenRetargetTearsDownCableBeforePublishingTheNewConsumer() {
        var engine = new CounterEngine(withOutputs: true, withLinking: true);
        using var host = Host(engine);
        var screens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("right", "video") },
        };
        var document = Document(Row("left"), Row("right")) with { ScreensRaw = screens };
        Install(host, null, document);
        Assert.True(host.TryLink("cable", [0, 1]).Ok);

        var retargeted = document with {
            ScreensRaw = [
                screens[0] with { Source = new WorldScreenSource.Machine("right", "video") },
                screens[1],
            ]
        };
        Install(host, document, retargeted);

        Assert.Equal("none", host.DescribeLinks());
        Assert.Null(host.LinkOf(0));
        Assert.Null(host.LinkOf(1));
        Assert.False(engine.Created[0].Disposed);
        Assert.False(engine.Created[1].Disposed);
        Assert.Same(host.VideoOutput("right", "video"), host.VideoOutput(0));
    }
    [Fact]
    public void LinkRejectsDuplicateNamedMemberAndStoppedNamedMemberBeforeTopologyChange() {
        var engine = new CounterEngine(withOutputs: true, withLinking: true);
        using var host = Host(engine);
        var duplicateScreens = new[] {
            Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
            Panel() with { Index = 1, Source = new WorldScreenSource.Machine("left", "video") },
        };
        Install(host, null, Document(Row("left")) with { ScreensRaw = duplicateScreens });
        var duplicate = host.TryLink("duplicate", [0, 1]);
        Assert.False(duplicate.Ok);
        Assert.Contains("named more than once", duplicate.Message, StringComparison.Ordinal);

        var stopped = Document(Row("left") with { Running = false }, Row("right")) with {
            ScreensRaw = [Panel() with { Source = new WorldScreenSource.Machine("left", "video") },
                Panel() with { Index = 1, Source = new WorldScreenSource.Machine("right", "video") }]
        };
        Install(host, Document(Row("left")) with { ScreensRaw = duplicateScreens }, stopped);
        var stoppedResult = host.TryLink("stopped", [0, 1]);
        Assert.False(stoppedResult.Ok);
        Assert.Contains("stopped", stoppedResult.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void NamedReadHelpersUseTheAuthoredOutputAndLiveRuntime() {
        var engine = new CounterEngine(withOutputs: true, output: "wide");
        using var host = Host(engine);
        var screen = Panel() with { Source = new WorldScreenSource.Machine("clock", "wide") };
        Install(host, null, Document(Row("clock")) with { ScreensRaw = [screen] });

        Assert.Same(host.VideoOutput("clock", "wide"), host.VideoOutput(0));
        Assert.Single(host.MachineScreenIndices);
        Assert.Equal(0, host.MachineScreenIndices.Single());
        Assert.True(host.TryPeekMessage(0, 7, out var before).Ok);
        Assert.Equal(0, before);
        Assert.True(host.TryPokeMessage(0, 7, 42).Ok);
        Assert.True(host.TryPeekMessage(0, 7, out var after).Ok);
        Assert.Equal(42, after);
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

    [Fact]
    public void RefusedMachineBatchDoesNotPublishCandidateSolidField() {
        var engine = new CounterEngine();
        var authored = Fixtures.BuildDocument();
        using var fixture = Fixtures.FreshServer(definition: authored, engines: [engine]);

        var beforeBytes = fixture.DefinitionBytes();
        var beforeField = fixture.Server.SolidField;
        var beforeSolidRevision = fixture.Server.SolidRevision;
        var beforePopulationRevision = fixture.Server.Population.Revision;
        var beforeJournalLength = fixture.Server.JournalLength;
        WorldEditEcho? refusal = null;
        fixture.Server.EchoTap = echo => { if (echo.Rejected) { refusal = echo; } };
        var candidateCollision = authored.Collision with {
            Requirements = [WorldContactRequirement.SmoothUnionContact]
        };

        fixture.Server.EnqueueMutation(new WorldMutation.Batch(
            Principal: WorldPrincipal.Console,
            Mutations: [
                new WorldMutation.SetCollision(Principal: WorldPrincipal.Console, Collision: candidateCollision),
                new WorldMutation.UpsertMachine(Principal: WorldPrincipal.Console, Machine: Row(name: "broken", seed: -1)),
            ]
        ));
        fixture.Step();

        Assert.Equal(expected: beforeBytes, actual: fixture.DefinitionBytes());
        Assert.Same(expected: beforeField, actual: fixture.Server.SolidField);
        Assert.Equal(expected: beforeSolidRevision, actual: fixture.Server.SolidRevision);
        Assert.Equal(expected: beforePopulationRevision, actual: fixture.Server.Population.Revision);
        Assert.Equal(expected: beforeJournalLength, actual: fixture.Server.JournalLength);
        Assert.True(condition: refusal.HasValue, userMessage: "the machine preparation refusal was not observed");
    }

    private static WorldMachineHost Host(IMachineEngine engine) => new([], new WorldMachineCatalog([engine]));
    private static WorldDefinition Document(params WorldMachine[] machines) => Fixtures.BuildDocument() with { MachinesRaw = machines, ScreensRaw = null };
    private static WorldMachine Row(string name, int seed = 0) => new(name, "counter", Json($"{{\"schema\":\"puck.counter.config.v1\",\"seed\":{seed}}}"));
    private static WorldMachine InputRow(string name) => new(name, "input-counter", Json("{\"schema\":\"puck.input-counter.v1\"}"));
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

    private sealed class CounterEngine(bool withOutputs = false, bool withLinking = false, bool withOperations = false, string output = "video") : IMachineEngine, IMachineLinkingEngine, IMachineOperationProvider {
        public string Id => "counter";
        public List<CounterRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new("counter", "Counter test device",
            new("puck.counter.config.v1", [new("seed", MachineFieldKind.Integer, "Initial counter")]),
            withOutputs ? [new(output, "puck.machine.video.v1", "Counter frame")] : [], [], [],
            withOperations ? [new("counter.noop", "No-op test operation", new("puck.counter.operation.v1", []))] : [], []);
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            if (request.Configuration.GetProperty("seed").GetInt32() < 0) {
                throw new ArgumentException("This device refuses a negative seed.");
            }
            var runtime = new CounterRuntime(withOutputs, output);
            Created.Add(runtime);
            return runtime;
        }
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            throw new InvalidOperationException("The device accepts structured construction only.");

        public MachineOperationPreparation PrepareOperation(MachineCreationRequest current, MachineOperationRequest request) =>
            new MachineOperationPreparation.Runtime(new NoopOperation());

        public bool TryLink(IReadOnlyList<IMachineRuntime> machines, out IMachineLink? link, out string reason) {
            if (!withLinking || machines.Count < 2 || machines.Any(machine => machine is not CounterRuntime)) {
                link = null;
                reason = "counter linking is unavailable";
                return false;
            }
            link = new CounterLink(machines);
            reason = string.Empty;
            return true;
        }
    }

    private sealed class NoopOperation : IMachinePreparedOperation {
        public MachineOperationResult Apply(IMachineRuntime runtime) => new(MachineOperationStatus.Applied);
    }
    private sealed class InputEngine(int portCount) : IMachineEngine {
        public string Id => "input-counter";
        public List<InputRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new("input-counter", "Input counter", new("puck.input-counter.v1", []), [new("video", "puck.machine.video.v1", "Input frame")], [],
            [.. Enumerable.Range(0, portCount).Select(index => new MachinePortDescriptor($"port{index}", "puck.machine.pad.v1", "Controller"))], [], []);
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new InputRuntime(portCount);
            Created.Add(runtime);
            return runtime;
        }
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) =>
            throw new InvalidOperationException("The device accepts structured construction only.");
    }

    private sealed class InputRuntime(int portCount) : IMachineRuntime, IMachineInputPorts, IMachineVideoOutputs {
        public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; } = new Dictionary<string, IMachineVideoOutput> { ["video"] = new CounterOutput() };
        public Dictionary<string, InputPort> Ports { get; } = Enumerable.Range(0, portCount).ToDictionary(index => $"port{index}", _ => new InputPort(), StringComparer.Ordinal);
        IReadOnlyDictionary<string, IMachineInputPort> IMachineInputPorts.InputPorts => Ports.ToDictionary(pair => pair.Key, pair => (IMachineInputPort)pair.Value, StringComparer.Ordinal);
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Ticks { get; private set; }
        public bool Disposed { get; private set; }
        public MachinePadState LastInput => Ports.Values.First().State;
        public bool Advance(ulong deltaTicks) { Assert.False(Disposed); Ticks += deltaTicks; return true; }
        public void Dispose() { Assert.False(Disposed); Disposed = true; }
    }

    private sealed class InputPort : IMachineInputPort {
        public MachinePadState State { get; private set; }
        public void SetState(in MachinePadState state) => State = state;
    }

    private sealed class CounterLink(IReadOnlyList<IMachineRuntime> machines) : IMachineLink {
        public IReadOnlyList<IMachineRuntime> Machines { get; } = machines;
        public long CompletedTransfers { get; private set; }
        public void Step(ulong deltaTicks, ReadOnlySpan<MachinePadState> inputs) {
            foreach (var machine in Machines) { _ = machine.Advance(deltaTicks); }
            CompletedTransfers++;
        }
        public void Dispose() { }
    }

    private sealed class CounterRuntime(bool withOutputs, string output) : IMachineRuntime, IMachineVideoOutputs, IMachineAudioOutputs, IMachineMemoryPeek {
        public IReadOnlyDictionary<string, IMachineVideoOutput> VideoOutputs { get; } = withOutputs
            ? new Dictionary<string, IMachineVideoOutput> { [output] = new CounterOutput() }
            : new Dictionary<string, IMachineVideoOutput>();
        public IReadOnlyDictionary<string, IAudioMachine> AudioOutputs { get; } = new Dictionary<string, IAudioMachine>();
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Ticks { get; private set; }
        public int AdvanceCalls { get; private set; }
        public bool Disposed { get; private set; }
        public bool Advance(ulong deltaTicks) {
            Assert.False(Disposed);
            Ticks += deltaTicks;
            AdvanceCalls++;
            return true;
        }
        public byte PeekByte(int address) => address == 7 ? MemoryValue : (byte)0;
        public void PokeByte(int address, byte value) { if (address == 7) { MemoryValue = value; } }
        public byte MemoryValue { get; private set; }
        public void Dispose() {
            Assert.False(Disposed);
            Disposed = true;
        }
    }

    private sealed class CounterOutput : IMachineVideoOutput {
        public nint NativeImageViewHandle => 0;
        public Vector3 EmittedLight => Vector3.Zero;
        public void PublishFrame(IGpuDeviceContext deviceContext, IGpuComputeServices gpu) { }
        public void NotifyDeviceLost() { }
    }
}
