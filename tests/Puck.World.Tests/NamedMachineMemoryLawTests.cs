using System.Text.Json;
using System.Buffers.Binary;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.World.Machines;
using Puck.World.Protocol;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Hardware bindings operate by instance identity, preserve availability, and obey authored write policy.</summary>
public sealed class NamedMachineMemoryLawTests {
    private const ulong WideAddress = 0x100000040UL;

    private static JsonElement Configuration(int seed = 0) {
        using var json = JsonDocument.Parse($"{{\"schema\":\"puck.memory.config.v1\",\"seed\":{seed}}}");

        return json.RootElement.Clone();
    }
    private static WorldDefinition Document(WorldMachineMemory binding, long value = 99) =>
        (Fixtures.BuildDocument() with {
            ScreensRaw = null,
            MachinesRaw = [new(
                "device",
                "memory",
                Configuration(),
                Memory: [binding]
            )],
        }).WithWorldState(rows: [new WorldStateRow(
                Name: CellName.Parse(candidate: "mirror"),
                Kind: CellKind.Int,
                Cells: [new StateCell(
                        Key: WorldStateRow.SlotKey,
                        Value: CellValue.Int(value: value)
                    )]
            )]);
    private static WorldMachineMemory Read() => new(
        "binding",
        WorldMachineMemoryDirection.Read,
        "bus",
        "u32",
        "mirror",
        Address: WideAddress
    );
    private static long Value(WorldFixture fixture) => fixture.Server.Definition.State[0].Cells![0].Value.AsInt;
    private static WorldMachineMemory Write() => Read() with { Direction = WorldMachineMemoryDirection.Write, Access = "patch" };

    [Fact]
    public void ARealAdvancedMachineReceivesAndMirrorsA32BitWordWithoutAScreen() {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-named-memory-{Guid.NewGuid():N}.gba"
        );
        var image = new byte[0xC0];

        BinaryPrimitives.WriteUInt32LittleEndian(
            destination: image,
            value: 0xEAFFFFFEU
        );
        File.WriteAllBytes(
            bytes: image,
            path: path
        );
        try {
            var configuration = JsonSerializer.SerializeToElement(new {
                schema = "puck.advanced-gaming-brick.config.v1",
                boot = "fast",
                content = new { path },
            });
            var machine = new WorldMachine(
                "device",
                "advanced-gaming-brick",
                configuration,
                Memory: [
                Write() with { Name = "send", Row = "send", Address = 0x02000040 },
                Read() with { Address = 0x02000040 },
            ]
            );
            var document = Document(
                binding: Read(),
                value: 0
            ) with { MachinesRaw = [machine] };

            document = document.WithWorldState(rows: [.. document.State,
                new WorldStateRow(
                    Name: CellName.Parse(candidate: "send"),
                    Kind: CellKind.Int,
                    Cells: [new StateCell(
                            Key: WorldStateRow.SlotKey,
                            Value: CellValue.Int(value: 0x12345678)
                        )]
                )]);
            using var fixture = Fixtures.FreshServer(
                document,
                engines: [new AdvancedGamingBrickEngine()]
            );

            fixture.Step();
            Assert.Equal(
                0x12345678L,
                Value(fixture: fixture)
            );
            var direct = fixture.Server.Machines.Inspect(
                "device",
                new(
                    Address: 0x02000040,
                    Space: "bus",
                    Width: 4
                )
            );

            Assert.Equal(
                MachineAccessStatus.Available,
                direct.Status
            );
            Assert.Equal(
                0x12345678UL,
                direct.Value
            );
            Assert.Empty(collection: fixture.Server.Definition.Screens);
        } finally {
            File.Delete(path: path);
        }
    }
    [Fact]
    public void AReplacementReceivesTheFirstWriteEvenWhenWorldStateDidNotChange() {
        var engine = new MemoryEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Write()),
            engines: [engine]
        );

        fixture.Step();
        var oldGeneration = fixture.Server.Machines.InstanceState(name: "device")!.Value.Generation;
        var replacement = fixture.Server.Definition.Machines[0] with { Configuration = Configuration(seed: 1) };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            replacement
        ));
        fixture.Step();
        Assert.Equal(
            2,
            engine.Created.Count
        );
        Assert.Equal(
            1,
            engine.Created[1].Writes
        );
        Assert.Equal(
            99UL,
            engine.Created[1].Value
        );
        Assert.True(condition: (fixture.Server.Machines.InstanceState(name: "device")!.Value.Generation > oldGeneration));
        Assert.Equal(
            MachineAccessStatus.Refused,
            fixture.Server.Machines.WriteHardware(
                "device",
                oldGeneration,
                new(
                    Address: WideAddress,
                    Space: "bus",
                    Width: 4
                ),
                123,
                MachineAccessMode.Patch
            ).Status
        );
        Assert.Equal(
            99UL,
            engine.Created[1].Value
        );
    }
    [Fact]
    public void CheckedNarrowingRefusesAndExplicitTruncationWritesTheLowBits() {
        var engine = new MemoryEngine();
        var binding = Write() with { Format = "u8" };
        using var fixture = Fixtures.FreshServer(
            Document(
                binding: binding,
                value: 300
            ),
            engines: [engine]
        );

        fixture.Step();
        var runtime = Assert.Single(collection: engine.Created);

        Assert.Equal(
            0,
            runtime.Writes
        );
        Assert.Equal(
            MachineAccessStatus.Refused,
            fixture.Server.MachineBindingState(
                binding: "binding",
                machine: "device"
            )!.Value.Status
        );
        var declaration = fixture.Server.Definition.Machines[0] with { Memory = [binding with { Conversion = "truncate" }] };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            declaration
        ));
        fixture.Step();
        Assert.Single(collection: engine.Created);
        Assert.Equal(
            44UL,
            runtime.Value
        );
        Assert.Equal(
            1,
            runtime.Writes
        );
    }
    [Fact]
    public void FullUnsignedAddressRangeAdmitsItsLastCompleteScalar() {
        var engine = new MemoryEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Write() with { Address = (ulong.MaxValue - 7), Format = "u64" }),
            engines: [engine]
        );

        fixture.Step();
        Assert.Equal(
            (ulong.MaxValue - 7),
            engine.Created[0].LastAddress
        );
        Assert.Equal(
            99UL,
            engine.Created[0].Value
        );
        Assert.Equal(
            MachineAccessStatus.Available,
            fixture.Server.MachineBindingState(
                binding: "binding",
                machine: "device"
            )!.Value.Status
        );
    }
    [Fact]
    public void MissingObservationDoesNotTurnIntoASuccessfulZero() {
        var engine = new MemoryEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Read()),
            engines: [engine]
        );
        var runtime = Assert.Single(collection: engine.Created);

        runtime.Available = false;
        fixture.Step();
        Assert.Equal(
            99,
            Value(fixture: fixture)
        );
        Assert.Equal(
            MachineAccessStatus.Unavailable,
            fixture.Server.MachineBindingState(
                binding: "binding",
                machine: "device"
            )!.Value.Status
        );
        runtime.Available = true;
        fixture.Step();
        Assert.Equal(
            0,
            Value(fixture: fixture)
        );
        Assert.Equal(
            0,
            fixture.Server.MachineBindingState(
                binding: "binding",
                machine: "device"
            )!.Value.LastValue
        );
        runtime.Available = false;
        fixture.Step();
        Assert.Equal(
            0,
            Value(fixture: fixture)
        );
        var unavailable = fixture.Server.MachineBindingState(
            binding: "binding",
            machine: "device"
        )!.Value;

        Assert.Equal(
            MachineAccessStatus.Unavailable,
            unavailable.Status
        );
        Assert.Equal(
            0,
            unavailable.LastValue
        );
    }
    [Fact]
    public void OverlappingWritesRefuseWithoutReplacingTheLiveInstance() {
        var engine = new MemoryEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Write()),
            engines: [engine]
        );
        var before = fixture.Server.Machines.InstanceState(name: "device")!.Value.Generation;
        var declaration = fixture.Server.Definition.Machines[0] with {
            Memory = [Write(), Write() with { Name = "overlap", Address = (WideAddress + 1), Format = "u8" }],
        };

        fixture.Server.EnqueueMutation(new WorldMutation.UpsertMachine(
            WorldPrincipal.Console,
            declaration
        ));
        fixture.Step();
        Assert.Single(collection: fixture.Server.Definition.Machines[0].Memory!);
        Assert.Equal(
            before,
            fixture.Server.Machines.InstanceState(name: "device")!.Value.Generation
        );
    }
    [Fact]
    public void PreparedContentSymbolsResolveThroughTheNamedBinding() {
        var path = Path.Combine(
            path1: Path.GetTempPath(),
            path2: $"puck-named-symbol-{Guid.NewGuid():N}.tiny"
        );

        File.WriteAllBytes(
            bytes: [17],
            path: path
        );
        try {
            var engine = new MemoryEngine();
            var document = Document(Write() with { Address = null, Symbol = "register" });
            var configuration = JsonSerializer.SerializeToElement(new {
                schema = "puck.memory.config.v1",
                seed = 0,
                content = new { path },
            });

            document = document with { MachinesRaw = [document.Machines[0] with { Configuration = configuration }] };
            using var fixture = Fixtures.FreshServer(
                document,
                machineCatalog: new WorldMachineCatalog(
                    [engine],
                    [new SymbolProvider()]
                )
            );

            fixture.Step();
            Assert.Equal(
                WideAddress,
                engine.Created[0].LastAddress
            );
            Assert.Equal(
                99UL,
                engine.Created[0].Value
            );
        } finally {
            File.Delete(path: path);
        }
    }
    [InlineData("i8", 255UL)]
    [InlineData("i16", 65535UL)]
    [InlineData("i32", 4294967295UL)]
    [InlineData("i64", ulong.MaxValue)]
    [Theory]
    public void SignedFormatsPreserveTwosComplementBits(string format, ulong bits) {
        var binding = Write() with { Format = format };

        Assert.Equal(
            bits,
            binding.Encode(value: -1)
        );
        Assert.Equal(
            -1,
            binding.Decode(value: bits)
        );
    }
    [InlineData("onChange", 1)]
    [InlineData("everyTick", 2)]
    [Theory]
    public void WritePolicyDeterminesWhetherGuestEditsAreOverridden(string update, int writes) {
        var engine = new MemoryEngine();
        using var fixture = Fixtures.FreshServer(
            Document(Write() with { Update = update }),
            engines: [engine]
        );
        var runtime = Assert.Single(collection: engine.Created);

        fixture.Step();
        Assert.Equal(
            99UL,
            runtime.Value
        );
        runtime.Value = 7;
        fixture.Step();
        Assert.Equal(
            writes,
            runtime.Writes
        );
        Assert.Equal(
            ((update == "onChange")
            ? 7UL
            : 99UL),
            runtime.Value
        );
    }

    private sealed class MemoryEngine : IMachineEngine {
        public string Id => "memory";

        public List<MemoryRuntime> Created { get; } = [];
        public MachineEngineDescriptor Descriptor { get; } = new(
            "memory",
            "Synthetic wide-address machine",
            new(
                "puck.memory.config.v1",
                [
                new(
                        "seed",
                        MachineFieldKind.Integer,
                        "Replacement selector"
                    ),
                new(
                        "content",
                        MachineFieldKind.Object,
                        "Content",
                        Fields: [
                    new(
                                "path",
                                MachineFieldKind.String,
                                "Content path",
                                Required: true,
                                Role: MachineFieldRole.ContentPath
                            )
                ]
                    )
            ]
            ),
            [],
            [],
            [],
            [],
            MemoryRuntime.Spaces
        );

        public IMachineRuntime Create(string? options, byte[]? contentBytes = null, string? savePath = null, int audioSampleRate = 0) => throw new NotSupportedException();
        public IMachineRuntime CreateMachine(MachineCreationRequest request) {
            var runtime = new MemoryRuntime();

            Created.Add(item: runtime);
            return runtime;
        }
    }
    private sealed class SymbolProvider : IMachineContentProvider {
        public string EngineId => "memory";

        public PreparedMachineContent Prepare(ReadOnlyMemory<byte> content) => new(
            content.ToArray(),
            WorldDefinitionFileSource.ComputeContentHash(content: content.Span),
            new Dictionary<string, MachineContentSymbol> {
                ["register"] = new(
                Address: WideAddress,
                Space: "bus"
            ),
            }
        );
        public bool Recognizes(string contentPath) => contentPath.EndsWith(
            comparisonType: StringComparison.Ordinal,
            value: ".tiny"
        );
    }
    private sealed class MemoryRuntime : IMachineRuntime, IMachineHardwareAccess {
        public bool Available { get; set; } = true;
        public ulong LastAddress { get; private set; }
        public IReadOnlyList<MachineMemorySpaceDescriptor> MemorySpaces => Spaces;
        public static IReadOnlyList<MachineMemorySpaceDescriptor> Spaces { get; } = [new(
                AccessModes: [MachineAccessMode.Inspect, MachineAccessMode.Patch],
                Description: "Synthetic scalar register",
                FirstAddress: 0,
                LastAddress: ulong.MaxValue,
                LittleEndian: true,
                Name: "bus",
                Widths: [1, 4, 8]
            )];
        public MachineRuntimeStatus Status => MachineRuntimeStatus.Running;
        public ulong Value { get; set; }
        public int Writes { get; private set; }

        public bool Advance(ulong deltaTicks) => true;
        public void Dispose() { }
        public MachineAccessResult Read(MachineMemoryAddress address, MachineAccessMode mode = MachineAccessMode.Inspect) =>
            (Available
                ? new(
                    MachineAccessStatus.Available,
                    Value
                )
                : new(MachineAccessStatus.Unavailable)
            );
        public MachineAccessResult Write(MachineMemoryAddress address, ulong value, MachineAccessMode mode) {
            LastAddress = address.Address;
            Value = value;
            Writes++;
            return new(MachineAccessStatus.Available);
        }
    }
}
