using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST (acceptance law 3): a machine checkpoint restored under unchanged on-change bindings runs on as
/// if never restored. Two machines each carry two bindings, and every key part is shared with another binding: both
/// machines name their bindings <c>value</c> and <c>other</c>, at the same ordinals. Server A runs; each guest then
/// edits each bound value differently; A's checkpoint is restored into server B, which booted from the same document;
/// and both run three more ticks. For write bindings the guests, the runtimes' write counts, the runtime state bytes
/// and the document bytes match; for read bindings the mutations each journals after the restore match; and the
/// authoritative hash matches for both. A memo restored under the wrong key, or not restored, writes or mirrors a value
/// the uninterrupted run never does.
/// </summary>
public sealed class MachineRestoreContinuityLawTests {
    private const ulong OtherAddress = 0x20UL;
    private const ulong ValueAddress = 0x10UL;

    private static readonly string[] Machines = ["left", "right"];

    // The world value each binding's row starts at, and the value each guest then rewrites it to: all eight distinct,
    // so a memo carried under another binding's key never holds the value its own binding last saw.
    private static long WorldValue(string machine, ulong address) => (((machine == "left") ? 11L : 13L) + ((address == ValueAddress) ? 0L : 1L));
    private static ulong GuestEdit(string machine, ulong address) => (((machine == "left") ? 7UL : 5UL) + ((address == ValueAddress) ? 0UL : 1UL));
    private static string Row(string machine, string binding) => $"{machine}{char.ToUpperInvariant(c: binding[0])}{binding[1..]}";
    private static WorldDefinition Document(WorldMachineMemoryDirection direction) {
        var access = ((direction == WorldMachineMemoryDirection.Write) ? "patch" : "inspect");
        var machines = Machines.Select(selector: (machine, seed) => new WorldMachine(
            machine,
            GuestEngine.EngineId,
            JsonSerializer.SerializeToElement(value: new { schema = GuestEngine.Schema, seed }),
            Memory: [
                new WorldMachineMemory("value", direction, "bus", "u8", Row(binding: "value", machine: machine), Address: ValueAddress, Access: access),
                new WorldMachineMemory("other", direction, "bus", "u8", Row(binding: "other", machine: machine), Address: OtherAddress, Access: access),
            ]
        )).ToArray();

        return (Fixtures.BuildDocument() with {
            ScreensRaw = null,
            MachinesRaw = machines,
        }).WithWorldState(rows: [.. machines.SelectMany(selector: machine => machine.Memory!.Select(selector: binding => new WorldStateRow(
            Name: CellName.Parse(candidate: binding.Row),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: ((direction == WorldMachineMemoryDirection.Write) ? WorldValue(address: binding.Address!.Value, machine: machine.Name) : 0L))
            )]
        )))]);
    }
    private static WorldAuthorityCheckpoint ThroughTheCodec(WorldServer server) {
        Assert.True(condition: server.TryCaptureCheckpoint(WorldAuthorityHostRowCheckpoint.Empty, out var captured, out var reason), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!), checkpoint: out var decoded, reason: out reason), userMessage: reason);

        return decoded!;
    }
    // Everything a guest holds, as one line per machine: its two bound values and its write count.
    private static string Guests(GuestEngine engine) => string.Join(
        separator: "; ",
        values: Machines.Select(selector: machine => {
            var runtime = engine.Runtime(machine: machine);

            return $"{machine} value={runtime.ValueAt(address: ValueAddress)} other={runtime.ValueAt(address: OtherAddress)} writes={runtime.Writes}";
        })
    );
    private static ulong Completed(WorldServer server) => (server.NextInputTick - 1UL);

    [InlineData(WorldMachineMemoryDirection.Write)]
    [InlineData(WorldMachineMemoryDirection.Read)]
    [Theory]
    public void ARestoredMachineRunsOnAsIfNeverRestored(WorldMachineMemoryDirection direction) {
        var definition = Document(direction: direction);
        var engineA = new GuestEngine();
        var engineB = new GuestEngine();
        using var a = Fixtures.FreshServer(definition: definition, engines: [engineA]);
        using var b = Fixtures.FreshServer(definition: definition, engines: [engineB]);

        // A read binding mirrors what each guest starts with; a write binding pokes each row's value into its guest.
        foreach (var machine in Machines) {
            foreach (var address in new[] { ValueAddress, OtherAddress }) {
                engineA.Runtime(machine: machine).SetOwn(address: address, value: ((ulong)WorldValue(address: address, machine: machine)));
            }
        }
        a.Step();
        foreach (var machine in Machines) {
            foreach (var address in new[] { ValueAddress, OtherAddress }) {
                Assert.Equal(expected: ((ulong)WorldValue(address: address, machine: machine)), actual: engineA.Runtime(machine: machine).ValueAt(address: address));
                engineA.Runtime(machine: machine).SetOwn(address: address, value: GuestEdit(address: address, machine: machine));
            }
        }
        a.Step();
        b.Server.RestoreCheckpoint(checkpoint: ThroughTheCodec(server: a.Server));

        var journaledA = 0;
        var journaledB = 0;

        a.Server.MutationJournalTap = (_, _, _) => journaledA++;
        b.Server.MutationJournalTap = (_, _, _) => journaledB++;
        Assert.Equal(expected: Guests(engine: engineA), actual: Guests(engine: engineB));

        for (var step = 0; (step < 3); step++) {
            a.Step();
            b.Step();

            var tick = Completed(server: a.Server);

            Assert.Equal(expected: tick, actual: Completed(server: b.Server));
            Assert.Equal(expected: Guests(engine: engineA), actual: Guests(engine: engineB));
            Assert.Equal(actual: journaledB, expected: journaledA);
            Assert.Equal(
                expected: WorldStateHashComposition.HashAuthoritative(server: a.Server, tick: tick),
                actual: WorldStateHashComposition.HashAuthoritative(server: b.Server, tick: tick)
            );
        }
        foreach (var machine in Machines) {
            Assert.Equal(expected: engineA.Runtime(machine: machine).CaptureCheckpoint(), actual: engineB.Runtime(machine: machine).CaptureCheckpoint());
        }
        Assert.Equal(
            expected: WorldDefinitionSerialization.Serialize(definition: a.Server.Definition),
            actual: WorldDefinitionSerialization.Serialize(definition: b.Server.Definition)
        );
        if (direction == WorldMachineMemoryDirection.Write) {
            // The control: each guest still holds its own edit, which the uninterrupted run never overwrote.
            foreach (var machine in Machines) {
                Assert.Equal(expected: GuestEdit(address: ValueAddress, machine: machine), actual: engineA.Runtime(machine: machine).ValueAt(address: ValueAddress));
            }
        }
    }

    // Creates one guest per machine, told apart by the seed its configuration carries.
    private sealed class GuestEngine : IMachineEngine {
        public const string EngineId = "guest";
        public const string Schema = "puck.guest.config.v1";

        private readonly Dictionary<long, GuestRuntime> m_created = [];

        public string Id => EngineId;

        public MachineEngineDescriptor Descriptor { get; } = new(
            EngineId,
            "Synthetic checkpointable guest",
            new(Schema, [new("seed", MachineFieldKind.Integer, "Which machine")]),
            [],
            [],
            [],
            [],
            GuestRuntime.Spaces
        );

        public GuestRuntime Runtime(string machine) => m_created[Array.IndexOf(array: Machines, value: machine)];
        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) => throw new NotSupportedException();
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new GuestRuntime();

            m_created[request.Configuration.GetProperty(propertyName: "seed").GetInt64()] = runtime;
            return runtime;
        }
    }
    // A guest whose memory is a few bytes it can edit itself, and whose checkpoint is those bytes and its write count.
    private sealed class GuestRuntime : IMachineRuntime, IMachineHardwareAccess, IMachineCheckpointRuntime {
        private readonly SortedDictionary<ulong, ulong> m_memory = [];

        public static IReadOnlyList<MachineMemorySpaceDescriptor> Spaces { get; } = [new(
            AccessModes: [MachineAccessMode.Inspect, MachineAccessMode.Patch],
            Description: "Synthetic guest bytes",
            FirstAddress: 0,
            LastAddress: 0xFF,
            LittleEndian: true,
            Name: "bus",
            Widths: [1]
        )];
        public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => Spaces;
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public int Writes { get; private set; }

        public ulong ValueAt(ulong address) => m_memory.GetValueOrDefault(key: address);
        // The guest's own program editing its memory: not a host write.
        public void SetOwn(ulong address, ulong value) => m_memory[address] = value;
        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() { }
        public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) => new(MachineAccessStatus.Available, ValueAt(address: address.Address));
        public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) {
            m_memory[address.Address] = value;
            Writes++;
            return new(MachineAccessStatus.Available);
        }
        public byte[] CaptureCheckpoint() {
            var bytes = new byte[(4 + (m_memory.Count * 16))];
            var offset = 4;

            BinaryPrimitives.WriteInt32LittleEndian(destination: bytes, value: Writes);
            foreach (var (address, value) in m_memory) {
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes.AsSpan(start: offset), value: address);
                BinaryPrimitives.WriteUInt64LittleEndian(destination: bytes.AsSpan(start: (offset + 8)), value: value);
                offset += 16;
            }
            return bytes;
        }
        public void RestoreCheckpoint(ReadOnlyMemory<byte> checkpoint) {
            var bytes = checkpoint.Span;

            m_memory.Clear();
            Writes = BinaryPrimitives.ReadInt32LittleEndian(source: bytes);
            for (var offset = 4; (offset < bytes.Length); offset += 16) {
                m_memory[BinaryPrimitives.ReadUInt64LittleEndian(source: bytes[offset..])] = BinaryPrimitives.ReadUInt64LittleEndian(source: bytes[(offset + 8)..]);
            }
        }
    }
}
