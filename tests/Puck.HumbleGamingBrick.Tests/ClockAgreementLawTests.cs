using Puck.HumbleGamingBrick.Timing;
using Puck.Machines;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>Holds reused clock agreements to independent single-cycle stepping across timer and display events.</summary>
public sealed class ClockAgreementLawTests {
    [Fact]
    public void ATickedCycleWithdrawsTheStandingPeripheralAgreement() {
        using var reference = Create();
        using var absorbing = Create();
        var referenceClock = reference.GetRequiredService<ComponentClock>();
        var absorbingClock = absorbing.GetRequiredService<ComponentClock>();

        Prepare(displayOff: true, machine: reference);
        Prepare(displayOff: true, machine: absorbing);
        referenceClock.AdvanceCpuTCycle();
        absorbingClock.AdvanceCpuTCycles(count: 1);
        Assert.True(condition: (absorbingClock.AgreedQuietCycles > 0));

        referenceClock.AdvanceCpuTCycle();
        absorbingClock.AdvanceCpuTCycle();

        for (var cycle = 0; (cycle < 13); ++cycle) {
            referenceClock.AdvanceCpuTCycle();
        }

        absorbingClock.AdvanceCpuTCycles(count: 13);
        absorbingClock.Settle();

        Assert.Equal(expected: 1, actual: reference.GetRequiredService<TimerComponent>().ReadRegister(address: 0xFF05));
        Assert.Equal(
            expected: reference.GetRequiredService<TimerComponent>().ReadRegister(address: 0xFF05),
            actual: absorbing.GetRequiredService<TimerComponent>().ReadRegister(address: 0xFF05)
        );
        Assert.True(condition: absorbing.Machine.Snapshot().ContentEquals(other: reference.Machine.Snapshot()));
    }
    [Fact]
    public void ParkedHblankDmaKeepsTimerEdgesAcrossDisplayBoundaries() {
        using var reference = Create();
        using var absorbing = Create();

        Prepare(displayOff: false, machine: reference);
        Prepare(displayOff: false, machine: absorbing);
        var referenceClock = reference.GetRequiredService<ComponentClock>();
        var absorbingClock = absorbing.GetRequiredService<ComponentClock>();
        var referenceTimer = reference.GetRequiredService<TimerComponent>();
        var absorbingTimer = absorbing.GetRequiredService<TimerComponent>();

        for (var cycle = 0; (cycle < 140_448); cycle += 8) {
            for (var subcycle = 0; (subcycle < 8); ++subcycle) {
                referenceClock.AdvanceCpuTCycle();
            }

            absorbingClock.AdvanceCpuTCycles(count: 8);
            absorbingClock.Settle();
            Assert.True(
                condition: (referenceTimer.ReadRegister(address: 0xFF05) == absorbingTimer.ReadRegister(address: 0xFF05)),
                userMessage: $"The timer diverged after {(cycle + 8)} cycles while HBlank DMA was parked by HALT."
            );
        }

        Assert.True(condition: absorbing.Machine.Snapshot().ContentEquals(other: reference.Machine.Snapshot()));
    }

    private static MachineInstance<Machine, MachineConfiguration> Create() {
        var rom = new byte[0x8000];

        rom[0x143] = 0x80;

        return MachineFactory.Create(configuration: new MachineConfiguration(model: ConsoleModel.CgbE, cartridgeRom: rom));
    }
    private static void Prepare(MachineInstance<Machine, MachineConfiguration> machine, bool displayOff) {
        var clock = machine.GetRequiredService<ComponentClock>();
        var dma = machine.GetRequiredService<HdmaController>();
        var ppu = machine.GetRequiredService<Ppu>();
        var timer = machine.GetRequiredService<TimerComponent>();

        // HALT parks an HBlank transfer. Turning the LCD off then makes the display quiet while the transfer stays parked.
        dma.WriteRegister(address: 0xFF55, value: 0x80);
        dma.OnCpuHalted();

        if (displayOff) {
            ppu.WriteRegister(address: 0xFF40, value: 0);
        }

        timer.WriteRegister(address: 0xFF04, value: 0);
        timer.WriteRegister(address: 0xFF05, value: 0);
        timer.WriteRegister(address: 0xFF07, value: 5);
        clock.AdvanceCpuTCycle();
        Assert.True(condition: dma.IsQuiet);
        Assert.False(condition: dma.IsIdle);
    }
}
