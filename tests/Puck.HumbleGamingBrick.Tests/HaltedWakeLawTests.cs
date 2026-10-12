using Microsoft.Extensions.DependencyInjection;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;
using Puck.Machines;

namespace Puck.HumbleGamingBrick.Tests;

/// <summary>
/// Holds a halted processor's idle to the machine cycle its wake lands on while the display is ticked alone. The
/// program switches to double speed, powers the sound unit off, enables the line-start (mode 2) status interrupt and
/// halts after every service, so it sleeps through each line's drawing and wakes at the next line's start, where the
/// display raises the request with dots still to draw. Each service sums the divider it reads, which records when it ran. The absorbing clock must wake on the same machine cycle as the
/// reference clock, which ticks every component on every cycle and steps every halted machine cycle.
/// </summary>
public sealed class HaltedWakeLawTests {
    // One 30 Hz host tick of LCD dots.
    private const ulong DotsPerTick = 139_810UL;
    private const int Ticks = 12;

    private static byte[] Program() {
        var rom = new byte[0x8000];

        // The status vector adds the divider it reads on entry to 0xC000, so a service that starts a machine cycle late
        // can read a later divider and leave a different sum: ldh a,(DIV); ld hl,0xC000; add a,(hl); ld (hl),a; reti.
        byte[] service = [0xF0, 0x04, 0x21, 0x00, 0xC0, 0x86, 0x77, 0xD9];

        service.CopyTo(
            array: rom,
            index: 0x0048
        );
        // nop; jp 0x0150
        rom[0x0100] = 0x00;
        rom[0x0101] = 0xC3;
        rom[0x0102] = 0x50;
        rom[0x0103] = 0x01;
        // Colour required.
        rom[0x0143] = 0x80;

        byte[] code = [
            0xF3, // di
            0x3E, 0x01, 0xE0, 0x4D, // KEY1 = 1: arm the speed switch
            0x10, 0x00, // stop: switch to double speed
            0xAF, 0xE0, 0x26, // NR52 = 0: sound off
            0x3E, 0x20, 0xE0, 0x41, // STAT = 0x20: request on mode 2
            0x3E, 0x02, 0xE0, 0xFF, // IE = status
            0xAF, 0xE0, 0x0F, // IF = 0
            0xFB, // ei
            0x76, // loop: halt
            0x00, // nop
            0x18, 0xFC, // jr loop
        ];

        code.CopyTo(
            array: rom,
            index: 0x0150
        );

        return rom;
    }
    private static MachineInstance<Machine, MachineConfiguration> Colour(byte[] rom, bool absorbs) =>
        MachineFactory.Create(
            compose: services => {
                _ = services.AddHumbleGamingBrickComponents();

                if (!absorbs) {
                    _ = services.AddScoped(implementationFactory: static provider => new ComponentClock(
                        absorbsQuietStretches: false,
                        apu: provider.GetRequiredService<ApuComponent>(),
                        apuGeneratorClock: provider.GetRequiredService<ApuGeneratorClock>(),
                        audioOutput: provider.GetRequiredService<AudioOutputComponent>(),
                        cartridge: provider.GetRequiredService<ICartridge>(),
                        clock: provider.GetRequiredService<MasterClock>(),
                        hdma: provider.GetRequiredService<HdmaController>(),
                        key1: provider.GetRequiredService<Key1Component>(),
                        oamDma: provider.GetRequiredService<OamDmaController>(),
                        ppu: provider.GetRequiredService<Ppu>(),
                        serial: provider.GetRequiredService<SerialComponent>(),
                        timer: provider.GetRequiredService<TimerComponent>()
                    ));
                }
            },
            configuration: new MachineConfiguration(
                bootMode: MachineBootMode.Fast,
                cartridgeRom: rom,
                model: ConsoleModel.CgbE
            )
        );

    [Fact]
    public void AHaltedProcessorWakesOnTheMachineCycleALineStartRequestLands() {
        var rom = Program();

        using var reference = Colour(
            absorbs: false,
            rom: rom
        );
        using var absorbing = Colour(
            absorbs: true,
            rom: rom
        );

        for (var tick = 1; (tick <= Ticks); tick++) {
            reference.Machine.Run(tCycles: DotsPerTick);
            absorbing.Machine.Run(tCycles: DotsPerTick);

            Assert.True(
                condition: absorbing.Machine.Snapshot().ContentEquals(other: reference.Machine.Snapshot()),
                userMessage: $"The absorbing clock's machine state diverged from the reference's by tick {tick}."
            );
        }

        Assert.True(condition: absorbing.GetRequiredService<IKey1>().IsDoubleSpeed);
    }
}
