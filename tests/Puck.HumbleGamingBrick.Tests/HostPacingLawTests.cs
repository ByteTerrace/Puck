using Puck.Hosting;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.Machines;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>
/// Holds the core's reported pacing rate to the LCD dot rate at either processor speed: double speed doubles the
/// processor, not the display, so a host that converts its ticks through <see cref="HumbleGamingBrickCore.CycleRate"/>
/// produces one native frame every 70,224 dots, about 59.7 a second, after the cartridge switches to double speed.
/// </summary>
public sealed class HostPacingLawTests {
    private const ulong DotsPerSecond = 4_194_304UL;
    private const int Ticks = 60;

    // di; KEY1 = 1; stop (switch to double speed); then halt forever with interrupts off.
    private static byte[] Program() {
        var rom = new byte[0x8000];

        rom[0x0100] = 0x00;
        rom[0x0101] = 0xC3;
        rom[0x0102] = 0x50;
        rom[0x0103] = 0x01;
        rom[0x0143] = 0x80;

        byte[] code = [0xF3, 0x3E, 0x01, 0xE0, 0x4D, 0x10, 0x00, 0x76, 0x18, 0xFD];

        code.CopyTo(
            array: rom,
            index: 0x0150
        );

        return rom;
    }

    [Fact]
    public void ADoubleSpeedColourMachineIsPacedAtTheDotRate() {
        using var core = new HumbleGamingBrickCore(configuration: new MachineConfiguration(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: Program(),
            model: ConsoleModel.CgbE
        ));
        var phase = new RationalRateAccumulator();
        var stepTicks = EngineTicks.PerRate(ratePerSecond: 30U);

        for (var tick = 0; (tick < Ticks); tick++) {
            core.RunCycles(cycles: phase.TakeCycleBudget(
                rate: core.CycleRate,
                ticks: stepTicks
            ));
        }

        var start = core.NativeFrameIndex;

        Assert.True(condition: core.Instance.GetRequiredService<IKey1>().IsDoubleSpeed);
        Assert.Equal(
            actual: core.CycleRate.Cycles,
            expected: DotsPerSecond
        );

        for (var tick = 0; (tick < Ticks); tick++) {
            core.RunCycles(cycles: phase.TakeCycleBudget(
                rate: core.CycleRate,
                ticks: stepTicks
            ));
        }

        // Two seconds of host ticks: 119.5 native frames at the dot rate, twice that if the rate followed the processor.
        Assert.InRange(
            actual: (core.NativeFrameIndex - start),
            high: 120L,
            low: 119L
        );
    }
}
