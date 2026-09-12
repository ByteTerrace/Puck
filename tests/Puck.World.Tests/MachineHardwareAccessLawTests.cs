using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.GamingBricks;
using Puck.HumbleGamingBrick;
using Xunit;

namespace Puck.World.Tests;

/// <summary>Exercises the hardware boundary independently of display ownership and content compilation.</summary>
public sealed class MachineHardwareAccessLawTests {
    [Fact]
    public void HumbleInspectionIsPureAndBusWritesReachRegistersThatPatchesRefuse() {
        using var core = new HumbleGamingBrickCore(ConsoleModel.CgbE, HumbleLoop(), bootMode: MachineBootMode.Fast);
        var ram = new MachineMemoryAddress("bus", 0xC240, 2);
        Available(core.Write(ram, 0xB731, MachineAccessMode.Patch));
        var before = Capture(core);
        Assert.Equal(0xB731UL, Available(core.Read(ram)).Value);
        Assert.Equal(before, Capture(core));

        var flags = new MachineMemoryAddress("bus", 0xFF0F, 1);
        var previous = Available(core.Read(flags)).Value;
        Assert.Equal(MachineAccessStatus.Refused, core.Write(flags, 3, MachineAccessMode.Patch).Status);
        Assert.Equal(previous, Available(core.Read(flags)).Value);
        Available(core.Write(flags, 3, MachineAccessMode.Bus));
        Assert.Equal(3UL, Available(core.Read(flags)).Value & 0x1FUL);
    }

    [Fact]
    public void AdvancedAccessKeepsWideAddressesAndRealTimerBusSideEffects() {
        using var core = new AdvancedGamingBrickCore(AdvancedLoop(), bootMode: MachineBootMode.Fast);
        var ram = new MachineMemoryAddress("bus", 0x02000040, 4);
        Available(core.Write(ram, 0x78563412, MachineAccessMode.Patch));
        var before = Capture(core);
        Assert.Equal(0x78563412UL, Available(core.Read(ram)).Value);
        Assert.Equal(before, Capture(core));

        var timer = new MachineMemoryAddress("bus", 0x04000100, 4);
        var previous = Available(core.Read(timer)).Value;
        Assert.Equal(MachineAccessStatus.Refused, core.Write(timer, 0x0080FFF0, MachineAccessMode.Patch).Status);
        Assert.Equal(previous, Available(core.Read(timer)).Value);
        var cycles = core.CycleCount;
        Available(core.Write(timer, 0x0080FFF0, MachineAccessMode.Bus));
        Assert.True(core.CycleCount > cycles);
        core.RunCycles(64);
        Assert.Equal(0x80UL, Available(core.Read(new("bus", 0x04000102, 2))).Value & 0x80UL);
        Assert.NotEqual(previous, Available(core.Read(timer)).Value);
    }

    [Fact]
    public void InvalidWritesRefuseBeforeChangingAnyByte() {
        using var core = new HumbleGamingBrickCore(ConsoleModel.CgbE, HumbleLoop(), bootMode: MachineBootMode.Fast);
        var interruptEnable = new MachineMemoryAddress("bus", 0xFFFF, 1);
        Available(core.Write(interruptEnable, 5, MachineAccessMode.Patch));
        Assert.Equal(MachineAccessStatus.Refused, core.Write(interruptEnable with { Width = 2 }, 0x0102, MachineAccessMode.Patch).Status);
        Assert.Equal(MachineAccessStatus.Refused, core.Write(interruptEnable, 0x100, MachineAccessMode.Patch).Status);
        Assert.Equal(5UL, Available(core.Read(interruptEnable)).Value);
        Assert.Equal(MachineAccessStatus.Unsupported, core.Read(new("missing", 0, 1)).Status);
        Assert.Equal(MachineAccessStatus.Refused, core.Read(new("bus", ulong.MaxValue, 2)).Status);
    }

    [Fact]
    public void QueuedInputIsCapturedAtSubmissionAndHardwareReadsDrainTheAcceptedSegment() {
        using var host = new MachineHost(ConsoleModel.CgbE, HumbleLoop(), bootMode: MachineBootMode.Fast);
        IMachineRuntime runtime = host;
        IQueuedMachineRuntime queue = host;
        var port = host.InputPorts["controls"];
        Available(host.Write(new("bus", 0xFF00, 1), 0x10, MachineAccessMode.Bus));
        var pressed = MachinePadState.Neutral with { Buttons = MachineButtons.South };
        port.SetState(in pressed);
        Assert.NotEqual(QueuedMachineSubmission.Rejected, queue.Submit(840));
        port.SetState(MachinePadState.Neutral);

        Assert.Equal(0UL, Available(host.Read(new("bus", 0xFF00, 1))).Value & 1UL);
        Assert.Equal(1, queue.CompletedSteps);
        Assert.Equal(0, queue.PendingSteps);
        Assert.True(runtime.Advance(840));
        Assert.Equal(1UL, Available(host.Read(new("bus", 0xFF00, 1))).Value & 1UL);
    }

    [Fact]
    public void EmptyAndUnsupportedHardwareAreDistinctFromAnObservedZero() {
        using var empty = new AdvancedMachineHost(AgbFirmware.GetImage());
        Assert.Equal(MachineAccessStatus.Unavailable, empty.Read(new("bus", 0x02000040, 4)).Status);
        Assert.Equal(MachineAccessStatus.Unavailable, default(MachineAccessResult).Status);
        using var assigned = new AdvancedMachineHost(AgbFirmware.GetImage(), AdvancedLoop());
        Available(assigned.Write(new("bus", 0x02000040, 4), 0, MachineAccessMode.Patch));
        Assert.Equal(0UL, Available(assigned.Read(new("bus", 0x02000040, 4))).Value);
        Assert.Equal(MachineAccessStatus.Refused, assigned.Read(new("bus", 0x02000041, 4)).Status);
        Assert.Equal(MachineAccessStatus.Unsupported, assigned.Read(new("bus", 0x0E000000, 1)).Status);
    }

    private static MachineAccessResult Available(MachineAccessResult result) {
        Assert.Equal(MachineAccessStatus.Available, result.Status);
        return result;
    }

    private static byte[] Capture(IQueuedMachineCore core) {
        var bytes = Array.Empty<byte>();
        var length = core.CaptureState(ref bytes);
        return bytes[..length];
    }

    private static byte[] HumbleLoop() {
        var image = new byte[0x8000];
        // JR -2 at the cartridge entry; no memory or peripheral writes.
        image[0x100] = 0x18;
        image[0x101] = 0xFE;
        return image;
    }

    private static byte[] AdvancedLoop() {
        var image = new byte[0xC0];
        // ARM B . at the native cartridge entry.
        image[0] = 0xFE;
        image[1] = 0xFF;
        image[2] = 0xFF;
        image[3] = 0xEA;
        return image;
    }
}
