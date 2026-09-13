using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.GamingBricks;
using Puck.HumbleGamingBrick;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises the hardware boundary independently of display ownership and content compilation.</summary>
public sealed class MachineHardwareAccessLawTests {
    private static byte[] AdvancedLoop() {
        var image = new byte[0xC0];
        // ARM B . at the native cartridge entry.
        image[0] = 0xFE;
        image[1] = 0xFF;
        image[2] = 0xFF;
        image[3] = 0xEA;
        return image;
    }
    private static MachineAccessResult Available(MachineAccessResult result) {
        Assert.Equal(
            MachineAccessStatus.Available,
            result.Status
        );
        return result;
    }
    private static byte[] Capture(IQueuedMachineCore core) {
        var bytes = Array.Empty<byte>();
        var length = core.CaptureState(buffer: ref bytes);

        return bytes[..length];
    }
    private static byte[] HumbleLoop() {
        var image = new byte[0x8000];
        // JR -2 at the cartridge entry; no memory or peripheral writes.
        image[0x100] = 0x18;
        image[0x101] = 0xFE;
        return image;
    }

    [Fact]
    public void AdvancedAccessKeepsWideAddressesAndRealTimerBusSideEffects() {
        using var core = new AdvancedGamingBrickCore(
            AdvancedLoop(),
            bootMode: MachineBootMode.Fast
        );
        var ram = new MachineMemoryAddress(
            Address: 0x02000040,
            Space: "bus",
            Width: 4
        );

        Available(result: core.Write(
            address: ram,
            mode: MachineAccessMode.Patch,
            value: 0x78563412
        ));
        var before = Capture(core: core);

        Assert.Equal(
            0x78563412UL,
            Available(result: core.Read(ram)).Value
        );
        Assert.Equal(
            before,
            Capture(core: core)
        );

        var timer = new MachineMemoryAddress(
            Address: 0x04000100,
            Space: "bus",
            Width: 4
        );
        var previous = Available(result: core.Read(timer)).Value;

        Assert.Equal(
            MachineAccessStatus.Refused,
            core.Write(
                address: timer,
                mode: MachineAccessMode.Patch,
                value: 0x0080FFF0
            ).Status
        );
        Assert.Equal(
            previous,
            Available(result: core.Read(timer)).Value
        );
        var cycles = core.CycleCount;

        Available(result: core.Write(
            address: timer,
            mode: MachineAccessMode.Bus,
            value: 0x0080FFF0
        ));
        Assert.True(condition: (core.CycleCount > cycles));
        core.RunCycles(cycles: 64);
        Assert.Equal(
            0x80UL,
            Available(result: core.Read(new(
                Address: 0x04000102,
                Space: "bus",
                Width: 2
            ))).Value & 0x80UL
        );
        Assert.NotEqual(
            previous,
            Available(result: core.Read(timer)).Value
        );
    }
    [Fact]
    public void EmptyAndUnsupportedHardwareAreDistinctFromAnObservedZero() {
        using var empty = new AdvancedMachineHost(AgbFirmware.GetImage());

        Assert.Equal(
            MachineAccessStatus.Unavailable,
            empty.Read(new(
                Address: 0x02000040,
                Space: "bus",
                Width: 4
            )).Status
        );
        Assert.Equal(
            MachineAccessStatus.Unavailable,
            default(MachineAccessResult).Status
        );
        using var assigned = new AdvancedMachineHost(
            AgbFirmware.GetImage(),
            AdvancedLoop()
        );

        Available(result: assigned.Write(
            new(
                Address: 0x02000040,
                Space: "bus",
                Width: 4
            ),
            0,
            MachineAccessMode.Patch
        ));
        Assert.Equal(
            0UL,
            Available(result: assigned.Read(new(
                Address: 0x02000040,
                Space: "bus",
                Width: 4
            ))).Value
        );
        Assert.Equal(
            MachineAccessStatus.Refused,
            assigned.Read(new(
                Address: 0x02000041,
                Space: "bus",
                Width: 4
            )).Status
        );
        Assert.Equal(
            MachineAccessStatus.Unsupported,
            assigned.Read(new(
                Address: 0x0E000000,
                Space: "bus",
                Width: 1
            )).Status
        );
    }
    [Fact]
    public void HumbleInspectionIsPureAndBusWritesReachRegistersThatPatchesRefuse() {
        using var core = new HumbleGamingBrickCore(
            ConsoleModel.CgbE,
            HumbleLoop(),
            bootMode: MachineBootMode.Fast
        );
        var ram = new MachineMemoryAddress(
            Address: 0xC240,
            Space: "bus",
            Width: 2
        );

        Available(result: core.Write(
            address: ram,
            mode: MachineAccessMode.Patch,
            value: 0xB731
        ));
        var before = Capture(core: core);

        Assert.Equal(
            0xB731UL,
            Available(result: core.Read(ram)).Value
        );
        Assert.Equal(
            before,
            Capture(core: core)
        );

        var flags = new MachineMemoryAddress(
            Address: 0xFF0F,
            Space: "bus",
            Width: 1
        );
        var previous = Available(result: core.Read(flags)).Value;

        Assert.Equal(
            MachineAccessStatus.Refused,
            core.Write(
                address: flags,
                mode: MachineAccessMode.Patch,
                value: 3
            ).Status
        );
        Assert.Equal(
            previous,
            Available(result: core.Read(flags)).Value
        );
        Available(result: core.Write(
            address: flags,
            mode: MachineAccessMode.Bus,
            value: 3
        ));
        Assert.Equal(
            3UL,
            Available(result: core.Read(flags)).Value & 0x1FUL
        );
    }
    [Fact]
    public void InvalidWritesRefuseBeforeChangingAnyByte() {
        using var core = new HumbleGamingBrickCore(
            ConsoleModel.CgbE,
            HumbleLoop(),
            bootMode: MachineBootMode.Fast
        );
        var interruptEnable = new MachineMemoryAddress(
            Address: 0xFFFF,
            Space: "bus",
            Width: 1
        );

        Available(result: core.Write(
            address: interruptEnable,
            mode: MachineAccessMode.Patch,
            value: 5
        ));
        Assert.Equal(
            MachineAccessStatus.Refused,
            core.Write(
                interruptEnable with { Width = 2 },
                0x0102,
                MachineAccessMode.Patch
            ).Status
        );
        Assert.Equal(
            MachineAccessStatus.Refused,
            core.Write(
                address: interruptEnable,
                mode: MachineAccessMode.Patch,
                value: 0x100
            ).Status
        );
        Assert.Equal(
            5UL,
            Available(result: core.Read(interruptEnable)).Value
        );
        Assert.Equal(
            MachineAccessStatus.Unsupported,
            core.Read(new(
                Address: 0,
                Space: "missing",
                Width: 1
            )).Status
        );
        Assert.Equal(
            MachineAccessStatus.Refused,
            core.Read(new(
                Address: ulong.MaxValue,
                Space: "bus",
                Width: 2
            )).Status
        );
    }
    [Fact]
    public void QueuedInputIsCapturedAtSubmissionAndHardwareReadsDrainTheAcceptedSegment() {
        using var host = new MachineHost(
            ConsoleModel.CgbE,
            HumbleLoop(),
            bootMode: MachineBootMode.Fast
        );
        IMachineRuntime runtime = host;
        IQueuedMachineRuntime queue = host;
        var port = host.InputPorts["controls"];

        Available(result: host.Write(
            new(
                Address: 0xFF00,
                Space: "bus",
                Width: 1
            ),
            0x10,
            MachineAccessMode.Bus
        ));
        var pressed = MachinePadState.Neutral with { Buttons = MachineButtons.South };

        port.SetState(state: in pressed);
        Assert.NotEqual(
            QueuedMachineSubmission.Rejected,
            queue.Submit(deltaTicks: 840)
        );
        port.SetState(state: MachinePadState.Neutral);

        Assert.Equal(
            0UL,
            Available(result: host.Read(new(
                Address: 0xFF00,
                Space: "bus",
                Width: 1
            ))).Value & 1UL
        );
        Assert.Equal(
            1,
            queue.CompletedSteps
        );
        Assert.Equal(
            0,
            queue.PendingSteps
        );
        Assert.True(condition: runtime.Advance(deltaTicks: 840));
        Assert.Equal(
            1UL,
            Available(result: host.Read(new(
                Address: 0xFF00,
                Space: "bus",
                Width: 1
            ))).Value & 1UL
        );
    }
}
