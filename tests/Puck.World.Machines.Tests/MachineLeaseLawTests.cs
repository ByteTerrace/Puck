using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a machine host's lease is either the provider's running runtime or a preparation refusal.
/// A refused lease is prepared again by the next rebuild, so a machine whose cause of refusal was fixed recovers without
/// a restart. A checkpoint restore applies completely or changes nothing: a row whose kind differs from the live lease's,
/// or whose runtime image its machine refuses, leaves every machine, generation and revision as it was; and refused rows
/// match by kind, whatever their refusal says.
/// </summary>
public sealed class MachineLeaseLawTests {
    private static WorldDefinition Document() => (Fixtures.BuildDocument() with {
        ScreensRaw = null,
        MachinesRaw = [Machine(name: "a", seed: 0), Machine(name: "b", seed: 1)],
    });
    private static WorldMachine Machine(string name, int seed) => new(
        name,
        LeaseEngine.EngineId,
        JsonSerializer.SerializeToElement(value: new { schema = LeaseEngine.Schema, seed })
    );
    private static void Prepare(WorldMachineHost host, WorldDefinition? current, WorldDefinition candidate) {
        Assert.True(condition: host.TryPrepare(candidate: candidate, current: current, plan: out var plan, reason: out var reason), userMessage: reason);
        host.Commit(plan: plan!);
        host.Finish(plan: plan!);
    }
    private static WorldMachineHost Boot(LeaseEngine engine, WorldDefinition definition) {
        var host = new WorldMachineHost(engines: [engine], screens: []);

        Prepare(current: null, candidate: definition, host: host);
        return host;
    }
    // Everything a checkpoint of the host records, as text: its bookkeeping and each machine's generation, steps and
    // image.
    private static string Describe(WorldMachineHost host) {
        var checkpoint = host.CaptureCheckpoint();

        return $"next={checkpoint.NextGeneration} revision={checkpoint.Revision} pumped={checkpoint.AnyEverPumped} " + string.Join(
            separator: "; ",
            values: checkpoint.Instances.Select(selector: static row => $"{row.Name} generation={row.Generation} steps={row.CompletedSteps} image={Convert.ToHexString(inArray: row.RuntimeState)}")
        );
    }

    [Fact]
    public void ARefusedMachineRecoversOnTheNextRebuildOnceItsCauseIsFixed() {
        var definition = Document();
        var engine = new LeaseEngine();

        engine.Refuse.Add(item: 1);
        using var host = Boot(definition: definition, engine: engine);
        var refused = host.InstanceState(name: "b")!.Value;

        Assert.Equal(expected: MachineRuntimeStatus.Refused, actual: refused.Status);
        Assert.Contains(expectedSubstring: engine.RefusalMessage, actualString: refused.Fault);

        // The control: a rebuild while the cause remains keeps the machine refused.
        Prepare(current: definition, candidate: (definition with { }), host: host);
        Assert.Equal(expected: MachineRuntimeStatus.Refused, actual: host.InstanceState(name: "b")!.Value.Status);

        engine.Refuse.Clear();
        Prepare(current: definition, candidate: (definition with { }), host: host);

        var recovered = host.InstanceState(name: "b")!.Value;

        Assert.Equal(expected: MachineRuntimeStatus.Running, actual: recovered.Status);
        Assert.Null(recovered.Fault);
        Assert.True(condition: (recovered.Generation > refused.Generation), userMessage: $"generation {recovered.Generation} after {refused.Generation}");
        // The running machine beside it kept its runtime through both rebuilds.
        _ = Assert.Single(engine.Created, static runtime => (runtime.Seed == 0));
    }

    [InlineData(false)]
    [InlineData(true)]
    [Theory]
    public void ARestoreOneMachineRefusesChangesNothing(bool refusedLive) {
        var definition = Document();
        var source = new LeaseEngine();
        using var running = Boot(definition: definition, engine: source);

        source.Runtime(seed: 0).Value = 5;
        source.Runtime(seed: 1).Value = 6;

        var checkpoint = running.CaptureCheckpoint();
        var target = new LeaseEngine();

        if (refusedLive) {
            // Machine b is refused here while the checkpoint records it running.
            target.Refuse.Add(item: 1);
        } else {
            // Both run here, and b's recorded image is one its machine refuses.
            checkpoint = checkpoint with {
                Instances = [.. checkpoint.Instances.Select(selector: static row => ((row.Name == "b") ? (row with { RuntimeState = [0xFF] }) : row))],
            };
        }
        using var host = Boot(definition: definition, engine: target);

        // A rebuild moves this host's revision past the recorded one, so restored bookkeeping would show as well as a
        // restored image; machine a restores before b in name order, so a partial restore shows in a's image.
        Prepare(current: definition, candidate: (definition with { }), host: host);
        target.Runtime(seed: 0).Value = 9;

        var before = Describe(host: host);

        _ = Assert.ThrowsAny<InvalidDataException>(testCode: () => host.RestoreCheckpoint(checkpoint: checkpoint));
        Assert.Equal(expected: before, actual: Describe(host: host));
        Assert.Equal(expected: 9L, actual: target.Runtime(seed: 0).Value);
    }

    [Fact]
    public void RefusedRowsRestoreByKindWhateverTheirRefusalSays() {
        var definition = Document();
        var source = new LeaseEngine { RefusalMessage = "the toolchain is missing" };
        var target = new LeaseEngine { RefusalMessage = "the content file is locked" };

        source.Refuse.Add(item: 1);
        target.Refuse.Add(item: 1);
        using var recorded = Boot(definition: definition, engine: source);
        using var host = Boot(definition: definition, engine: target);

        source.Runtime(seed: 0).Value = 5;
        target.Runtime(seed: 0).Value = 9;
        host.RestoreCheckpoint(checkpoint: recorded.CaptureCheckpoint());

        Assert.Equal(expected: 5L, actual: target.Runtime(seed: 0).Value);
        Assert.Equal(expected: Describe(host: recorded), actual: Describe(host: host));
        Assert.Equal(expected: MachineRuntimeStatus.Refused, actual: host.InstanceState(name: "b")!.Value.Status);
    }

    // Creates one runtime per machine, told apart by its configured seed; a seed in Refuse fails creation.
    private sealed class LeaseEngine : IMachineEngine {
        public const string EngineId = "lease";
        public const string Schema = "puck.lease.config.v1";

        public List<LeaseRuntime> Created { get; } = [];
        public HashSet<long> Refuse { get; } = [];
        public string RefusalMessage { get; init; } = "the toolchain is missing";
        public string Id => EngineId;
        public MachineEngineDescriptor Descriptor { get; } = new(
            EngineId,
            "Synthetic checkpointable machine",
            new(Schema, [new("seed", MachineFieldKind.Integer, "Which machine")]),
            [],
            [],
            [],
            [],
            []
        );

        public LeaseRuntime Runtime(long seed) => Created.Last(predicate: runtime => (runtime.Seed == seed));
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) => throw new NotSupportedException();
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var seed = request.Configuration.GetProperty(propertyName: "seed").GetInt64();

            if (Refuse.Contains(item: seed)) {
                throw new InvalidOperationException(message: RefusalMessage);
            }
            var runtime = new LeaseRuntime(seed: seed);

            Created.Add(item: runtime);
            return runtime;
        }
    }
    // A machine whose whole state is one value, checkpointed as its eight bytes.
    private sealed class LeaseRuntime(long seed) : IMachineRuntime, IMachineCheckpointRuntime {
        public long Seed { get; } = seed;
        public long Value { get; set; }
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;

        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() { }
        public byte[] CaptureCheckpoint() {
            var bytes = new byte[sizeof(long)];

            BinaryPrimitives.WriteInt64LittleEndian(destination: bytes, value: Value);
            return bytes;
        }
        public bool TryValidateCheckpoint(ReadOnlyMemory<byte> checkpoint, out string reason) {
            reason = ((checkpoint.Length == sizeof(long)) ? string.Empty : $"image is {checkpoint.Length} bytes, not {sizeof(long)}");
            return (reason.Length == 0);
        }
        public void RestoreCheckpoint(ReadOnlyMemory<byte> checkpoint) {
            if (!TryValidateCheckpoint(checkpoint: checkpoint, reason: out var reason)) {
                throw new InvalidDataException(message: reason);
            }
            Value = BinaryPrimitives.ReadInt64LittleEndian(source: checkpoint.Span);
        }
    }
}
