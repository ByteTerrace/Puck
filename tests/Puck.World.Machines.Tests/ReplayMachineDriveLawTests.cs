using System.Text.Json;
using Puck.Testing;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>
/// CONTRACT UNDER TEST: a world whose machines have stepped records mid-session and drives its tape back into the live
/// session, as often as asked, with every driven tick's authoritative hash and the machine cores at the end matching
/// the recording. The start checkpoint carries each core's image, every screen slot's selector and every link row, so a
/// moved selector and a stepped core are restored over the running host rather than refusing the arm or the drive.
/// </summary>
public sealed class ReplayMachineDriveLawTests {
    private const string Machine = "device";

    private static WorldDefinition Document(string romPath) {
        var document = Fixtures.BuildDocument();
        var configuration = JsonSerializer.SerializeToElement(new {
            schema = "puck.advanced-gaming-brick.config.v1",
            boot = "fast",
            content = new { path = romPath },
        });

        return (document with {
            ScreensRaw = [.. document.Screens.Select(selector: static screen => screen with {
                Source = new WorldScreenSource.Machine(Instance: Machine, Output: "video"),
            })],
            MachinesRaw = [new WorldMachine(
                Machine,
                "advanced-gaming-brick",
                configuration
            )],
        });
    }
    private static byte[] ProgramImage() {
        var image = new byte[0xD0];

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0x00), value: 0xEA00002EU);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC0), value: 0xE3A00402U);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC4), value: 0xE2800001U); // add r0, r0, #1
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xC8), value: 0xE5C01040U);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(destination: image.AsSpan(start: 0xCC), value: 0xEAFFFFFCU); // b 0xC4

        return image;
    }
    private static byte[] Image(WorldServer server) =>
        Assert.Single(collection: ((IWorldMachineCheckpointHost)server.Machines).CaptureCheckpoint().Instances).RuntimeState;
    private static WorldReplaySnapshot ReadTape(string path) {
        using var stream = File.OpenRead(path: path);

        return WorldReplaySnapshot.Read(stream: stream);
    }
    // One live tick the way WorldServerStepShell drives it: recorded input into the doors, the step, the close.
    private static int ShellStep(WorldHistoryHarness harness) {
        harness.Tape.InjectDriveTick();
        harness.Fixture.Step();
        harness.Fixture.Server.EnforceJournalDepth();

        var diverged = (harness.Tape.DriveProgress?.DivergedAt ?? -1);

        harness.Tape.NoteTick();

        return Math.Max(val1: diverged, val2: (harness.Tape.DriveProgress?.DivergedAt ?? -1));
    }

    [Fact]
    public void ASteppedMachineWorldRecordsMidSessionAndDrivesItsTapeTwice() {
        using var directory = new TemporaryDirectory(prefix: "puck-replay-machine-drive-");
        var rom = directory.WriteBytes(bytes: ProgramImage(), name: "program.gba");
        using var harness = new WorldHistoryHarness(
            definition: Document(romPath: rom),
            on: false,
            seats: 1
        );
        var server = harness.Fixture.Server;

        harness.Steps(count: 6);
        Assert.True(condition: server.AnyMachineEverPumped);
        Assert.True(condition: harness.Tape.TryBeginRecording(name: "machines", refusal: out var refusal), userMessage: refusal);
        harness.Steps(count: 12);

        var stop = harness.Tape.StopRecording();

        Assert.True(condition: stop.Verdict!.Passing, userMessage: stop.Verdict.Describe());

        var tape = ReadTape(path: stop.Path);
        var endHash = harness.Hash();
        var endImage = Image(server: server);

        Assert.Single(collection: tape.StartCheckpoint!.Machines!.Instances);
        Assert.Single(collection: tape.StartCheckpoint.Machines.Slots);

        for (var drive = 0; (drive < 2); drive++) {
            // The live world has moved on past the recording since the last drive, machines included.
            harness.Steps(count: 4);
            Assert.True(condition: harness.Tape.TryBeginDrive(documentPath: null, forkName: null, name: "machines", refusal: out refusal, toTick: null), userMessage: refusal);

            var diverged = -1;

            while (harness.Tape.Mode == WorldReplayMode.Replaying) {
                diverged = Math.Max(val1: diverged, val2: ShellStep(harness: harness));
            }

            Assert.True(condition: (diverged < 0), userMessage: $"drive {drive} diverged at tick {diverged}");
            Assert.Equal(expected: endHash, actual: harness.Hash());
            Assert.Equal(expected: tape.RecordedAuthoritativeHashes[^1], actual: harness.Hash());
            Assert.Equal(expected: endImage, actual: Image(server: server));
        }
    }
    [Fact]
    public void AMovedMagazineSelectorRidesTheMachineCheckpoint() {
        var screens = Fixtures.BuildDocument().Screens.Select(selector: static screen => screen with {
            Magazine = new WorldScreenMagazine(Entries: [new WorldScreenSource.None(), new WorldScreenSource.None(), new WorldScreenSource.None()]),
        }).ToArray();
        var catalog = TestMachines.Catalog();
        using var source = new WorldMachineHost(catalog: catalog, screens: screens);
        using var target = new WorldMachineHost(catalog: catalog, screens: screens);

        Assert.True(condition: source.TrySelect(entry: 2, index: screens[0].Index).Ok);

        var checkpoint = source.CaptureCheckpoint();

        Assert.Equal(expected: 2, actual: Assert.Single(collection: checkpoint.Slots).SelectedEntry);

        using var fixture = Fixtures.FreshServer();

        Assert.True(condition: fixture.Server.TryCaptureCheckpoint(
            checkpoint: out var captured,
            hostRow: WorldAuthorityHostRowCheckpoint.Empty,
            reason: out var reason
        ), userMessage: reason);
        Assert.True(condition: WorldAuthorityCheckpointCodec.TryDecode(
            bytes: WorldAuthorityCheckpointCodec.Encode(checkpoint: (captured! with { Machines = checkpoint })),
            checkpoint: out var decoded,
            reason: out reason
        ), userMessage: reason);
        target.RestoreCheckpoint(checkpoint: decoded!.Machines!);
        Assert.True(condition: target.TryMagazine(index: screens[0].Index, magazine: out _, selected: out var selected));
        Assert.Equal(expected: 2, actual: selected);
        // A selector past the magazine refuses by name before anything moves.
        Assert.Throws<InvalidDataException>(testCode: () => target.RestoreCheckpoint(checkpoint: checkpoint with {
            Slots = [checkpoint.Slots[0] with { SelectedEntry = 3 }],
        }));
    }
}
