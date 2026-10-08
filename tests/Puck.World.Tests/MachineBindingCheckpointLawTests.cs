using System.Buffers.Binary;
using System.Text.Json;
using Puck.Abstractions.Machines;
using Puck.Commands;
using Puck.Testing;
using Puck.World.Protocol;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a named machine binding's on-change memo is simulation state, because it decides whether the
/// next tick pokes the machine. A checkpoint carries it in binding order, and every restore replaces the live memo
/// with it. So a world restored over a running machine, by a history seek in place or into a fresh server as a hosted
/// activation does, leaves a byte the machine's own program rewrote alone until the world's value changes, exactly as
/// the uninterrupted run does. A seek's verdict says when stepped machine cores lie outside the hash it proves
/// (<c>MachineBindingCheckpointLawTests.Surfaces.cs</c>).
/// </summary>
public sealed partial class MachineBindingCheckpointLawTests {
    private const string Machine = "device";
    private const ulong SendAddress = 0x02000040UL;
    private const byte ProgramByte = 0x55;
    private const long FirstValue = 7;
    private const long SecondValue = 9;

    // A real AGB cartridge image, four instructions long, run from the cartridge entry under a fast boot: branch past
    // the header, then store ProgramByte at SendAddress once and spin. The first tick pokes FirstValue through the
    // binding before the machine advances, so the program's store overwrites the byte the world wrote.
    private static byte[] ProgramImage() {
        var image = new byte[0xD0];

        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0x00), value: 0xEA00002EU); // b 0xC0
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC0), value: 0xE3A00402U); // mov r0, #0x02000000
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC4), value: 0xE3A01055U); // mov r1, #0x55
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC8), value: 0xE5C01040U); // strb r1, [r0, #0x40]
        BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xCC), value: 0xEAFFFFFEU); // b .

        return image;
    }
    private static WorldMachineMemory Binding(string name, string row, ulong address, string update) => new(
        name,
        WorldMachineMemoryDirection.Write,
        "bus",
        "u8",
        row,
        Address: address,
        Access: "patch",
        Update: update
    );
    private static WorldDefinition Document(string romPath, params WorldMachineMemory[] bindings) {
        var configuration = JsonSerializer.SerializeToElement(new {
            schema = "puck.advanced-gaming-brick.config.v1",
            boot = "fast",
            content = new { path = romPath },
        });

        return (Fixtures.BuildDocument() with {
            ScreensRaw = null,
            MachinesRaw = [new WorldMachine(
                Machine,
                "advanced-gaming-brick",
                configuration,
                Memory: bindings
            )],
        }).WithWorldState(rows: [.. bindings.Select(selector: static binding => new WorldStateRow(
            Name: CellName.Parse(candidate: binding.Row),
            Kind: CellKind.Int,
            Cells: [new StateCell(
                Key: WorldStateRow.SlotKey,
                Value: CellValue.Int(value: FirstValue)
            )]
        ))]);
    }
    private static ulong ByteAt(WorldServer server) {
        var result = server.Machines.Inspect(
            Machine,
            new(
                Address: SendAddress,
                Space: "bus",
                Width: 1
            )
        );

        Assert.Equal(
            expected: MachineAccessStatus.Available,
            actual: result.Status
        );

        return result.Value;
    }
    private static ulong Completed(WorldServer server) => (server.NextInputTick - 1UL);
    private static byte[] Image(WorldServer server) =>
        Assert.Single(collection: ((IWorldMachineCheckpointHost)server.Machines).CaptureCheckpoint().Instances).RuntimeState;
    private static WorldMutation SetSend(long value) => new WorldMutation.UpsertStateCell(
        Principal: Principal.Console,
        Row: "send",
        Key: WorldStateRow.SlotKey.Value,
        Value: value,
        Kind: WorldDocumentWriteKind.Set
    );
    private static void AssertSameMachine(WorldServer expected, WorldServer actual, ulong tick) {
        var want = ByteAt(server: expected);
        var got = ByteAt(server: actual);

        Assert.True(
            condition: (want == got),
            userMessage: $"tick {tick}: the restored machine holds 0x{got:X2} at 0x{SendAddress:X8} where the uninterrupted one holds 0x{want:X2}"
        );
        Assert.Equal(
            expected: Image(server: expected),
            actual: Image(server: actual)
        );
    }

    [Fact]
    public void ASeekOverARunningMachineLeavesTheByteItsProgramRewroteUntilTheWorldValueChanges() {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-seek-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        using var harness = new WorldHistoryHarness(
            definition: Document(rom, Binding(address: SendAddress, name: "send", row: "send", update: "onChange")),
            on: false,
            seats: 0
        );
        var server = harness.Fixture.Server;

        harness.StepWithoutInput();
        Assert.Equal(
            expected: ProgramByte,
            actual: ByteAt(server: server)
        );
        Assert.True(
            condition: harness.History.TryOn(
                budgetBytes: WorldHistory.DefaultBudgetBytes,
                refusal: out var refusal
            ),
            userMessage: refusal
        );
        harness.StepWithoutInput();

        var keyframe = harness.History.OldestTick!.Value;
        var bytes = new Dictionary<ulong, ulong>();
        var images = new Dictionary<ulong, byte[]>();

        for (var step = 0; (step < 8); step++) {
            if (step == 4) {
                harness.Submit(mutation: SetSend(value: SecondValue));
            }

            harness.StepWithoutInput();
            bytes[harness.Tick] = ByteAt(server: server);
            images[harness.Tick] = Image(server: server);
        }

        var head = harness.Tick;

        Assert.Equal(
            expected: ProgramByte,
            actual: bytes[(keyframe + 1UL)]
        );
        Assert.Equal(
            expected: ((ulong)SecondValue),
            actual: bytes[head]
        );

        var restore = harness.SeekAndProve(target: keyframe);

        Assert.Equal(
            expected: keyframe,
            actual: restore.KeyframeTick
        );

        for (var tick = (keyframe + 1UL); (tick <= head); tick++) {
            _ = harness.SeekAndProve(target: tick);

            var got = ByteAt(server: server);

            Assert.True(
                condition: (bytes[tick] == got),
                userMessage: $"tick {tick}: the seek's machine holds 0x{got:X2} at 0x{SendAddress:X8} where the uninterrupted run held 0x{bytes[tick]:X2}"
            );
            Assert.Equal(
                expected: images[tick],
                actual: Image(server: server)
            );
        }
    }
    [InlineData("onChange")]
    [InlineData("everyTick")]
    [Theory]
    public void AServerRestoredFromACheckpointContinuesTheCapturedBindingMemo(string update) {
        using var directory = new TemporaryDirectory(prefix: "puck-binding-memo-restore-");
        using var profiles = new TemporaryDirectory(prefix: "puck-binding-memo-profiles-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        var definition = Document(rom, Binding(address: SendAddress, name: "send", row: "send", update: update));
        var catalog = TestMachines.Catalog();
        using var source = Fixtures.FreshServer(
            definition: definition,
            machineCatalog: catalog
        );

        source.Step();
        source.Step();
        Assert.True(
            condition: source.Server.TryCaptureCheckpoint(
                WorldAuthorityHostRowCheckpoint.Empty,
                out var captured,
                out var reason
            ),
            userMessage: reason
        );
        Assert.True(
            condition: WorldAuthorityCheckpointCodec.TryDecode(
                bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: captured!),
                checkpoint: out var decoded,
                reason: out reason
            ),
            userMessage: reason
        );

        using var machines = Fixtures.MachineHostFactory(definition.Screens, catalog.Engines.Values, null, null);

        var (restored, _) = WorldServer.FromCheckpoint(
            checkpoint: decoded!,
            documentDirectory: definition.DocumentDirectory,
            instanceIdentity: "boot",
            machines: machines,
            profiles: new WorldOwnedWorlds(
                directory: profiles.RootPath,
                machineId: Guid.NewGuid(),
                template: definition
            )
        );

        AssertSameMachine(actual: restored, expected: source.Server, tick: Completed(server: source.Server));
        for (var step = 0; (step < 6); step++) {
            if (step == 3) {
                source.Server.EnqueueMutation(SetSend(value: SecondValue));
                restored.EnqueueMutation(SetSend(value: SecondValue));
            }

            source.Step();
            restored.Advance(stepTicks: Fixtures.StepTicks);
            AssertSameMachine(actual: restored, expected: source.Server, tick: Completed(server: source.Server));
            Assert.Equal(
                expected: WorldStateHashComposition.HashAuthoritative(server: source.Server, tick: Completed(server: source.Server)),
                actual: WorldStateHashComposition.HashAuthoritative(server: restored, tick: Completed(server: restored))
            );
        }
        Assert.Equal(
            expected: ((ulong)SecondValue),
            actual: ByteAt(server: restored)
        );
    }
}
