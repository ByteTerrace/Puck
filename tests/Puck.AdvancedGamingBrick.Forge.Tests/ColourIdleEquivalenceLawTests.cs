using Microsoft.Extensions.DependencyInjection;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Interfaces;
using Puck.HumbleGamingBrick.Timing;
using Puck.Machines;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Pins the cheap paths the Colour machine takes while a shipped cartridge waits for its next frame to the reference
/// clock, bit for bit: it absorbs stretches its components agree are plain counting, ticks its display alone (double
/// speed included), and idles a halted processor in stretches, where the reference ticks every component on every
/// cycle and steps every halted machine cycle. Both run the same cartridge under the same scripted presses at the
/// hosted tick budget with host audio on, and the law compares their audio every tick and their complete state.
/// </summary>
public sealed class ColourIdleEquivalenceLawTests {
    // One 30 Hz host tick of LCD dots; the remainder ticks keep the budgets uneven.
    private const ulong ColourDotsPerTick = 139_810UL;
    private const int CompareEveryTicks = 15;
    private const int SampleRate = 48_000;
    private const int Ticks = 120;

    private static MachineInstance<Machine, MachineConfiguration> Colour(byte[] rom, ConsoleModel model, bool absorbs) =>
        MachineFactory.Create(
            compose: services => {
                _ = services.AddHumbleGamingBrickComponents();

                if (!absorbs) {
                    _ = services.AddScoped(implementationFactory: static provider => new ComponentClock(
                        absorbsQuietStretches: false,
                        apu: Service<ApuComponent>(provider: provider),
                        apuGeneratorClock: Service<ApuGeneratorClock>(provider: provider),
                        audioOutput: Service<AudioOutputComponent>(provider: provider),
                        cartridge: Service<ICartridge>(provider: provider),
                        clock: Service<MasterClock>(provider: provider),
                        hdma: Service<HdmaController>(provider: provider),
                        key1: Service<Key1Component>(provider: provider),
                        oamDma: Service<OamDmaController>(provider: provider),
                        ppu: Service<Ppu>(provider: provider),
                        serial: Service<SerialComponent>(provider: provider),
                        timer: Service<TimerComponent>(provider: provider)
                    ));
                }
            },
            configuration: new MachineConfiguration(
                bootMode: MachineBootMode.Fast,
                cartridgeRom: rom,
                model: model
            )
        );
    private static T Service<T>(IServiceProvider provider) where T : notnull =>
        provider.GetRequiredService<T>();

    // The shipped cartridges halt only for V-blank, which the display raises as it finishes drawing; split scroll rows
    // add the status interrupt, which wakes the processor while the display is still drawing a line.
    [InlineData("hgb-mirror.cgb", ConsoleModel.CgbE, false)]
    [InlineData("tetromino.cgb", ConsoleModel.CgbE, false)]
    [InlineData("tetromino.cgb", ConsoleModel.CgbE, true)]
    [InlineData("light-gun.cgb", ConsoleModel.CgbE, false)]
    [InlineData("tetromino.cgb", ConsoleModel.DmgC, false)]
    [Theory]
    public void TheColourMachineIdlesExactlyAsTheReferenceClockSteps(string cartridge, ConsoleModel model, bool rastered) {
        var document = CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: $"worlds/debug/cartridges/cgb/{cartridge}.cartridge.json")));

        if (rastered) {
            document = (document with {
                Raster = [
                    new CartridgeRasterRow(
                        Line: 40,
                        ScrollX: CartridgeExpressions.Of(constant: 8),
                        ScrollY: CartridgeExpressions.Of(constant: 0)
                    ),
                    new CartridgeRasterRow(
                        Line: 96,
                        ScrollX: CartridgeExpressions.Of(constant: 16),
                        ScrollY: CartridgeExpressions.Of(constant: 4)
                    ),
                ],
            });
        }

        var rom = new HgbCartridgeCompiler().Compile(document: document).Rom;

        using var reference = Colour(
            absorbs: false,
            model: model,
            rom: rom
        );
        using var absorbing = Colour(
            absorbs: true,
            model: model,
            rom: rom
        );

        var referenceAudio = reference.GetRequiredService<IAudioSink>();
        var absorbingAudio = absorbing.GetRequiredService<IAudioSink>();
        var random = new Random(Seed: 7);
        var referenceSamples = new short[16_384];
        var absorbingSamples = new short[16_384];

        referenceAudio.Configure(sampleRate: SampleRate);
        absorbingAudio.Configure(sampleRate: SampleRate);

        for (var tick = 1; (tick <= Ticks); tick++) {
            var buttons = ((random.Next(maxValue: 4) == 0)
                ? ((JoypadButtons)(1 << random.Next(maxValue: 8)))
                : JoypadButtons.None
            );
            var budget = (ColourDotsPerTick + (((tick % 3) == 0) ? 1UL : 0UL));

            reference.GetRequiredService<IJoypad>().SetButtons(pressed: buttons);
            absorbing.GetRequiredService<IJoypad>().SetButtons(pressed: buttons);
            reference.Machine.Run(tCycles: budget);
            absorbing.Machine.Run(tCycles: budget);

            var referenceCount = referenceAudio.ReadSamples(destination: referenceSamples);
            var absorbingCount = absorbingAudio.ReadSamples(destination: absorbingSamples);

            Assert.True(
                condition: referenceSamples.AsSpan(length: referenceCount, start: 0).SequenceEqual(other: absorbingSamples.AsSpan(length: absorbingCount, start: 0)),
                userMessage: $"{cartridge} on {model}: the absorbing clock's audio diverged from the reference's at tick {tick}."
            );

            if ((tick % CompareEveryTicks) == 0) {
                Assert.True(
                    condition: absorbing.Machine.Snapshot().ContentEquals(other: reference.Machine.Snapshot()),
                    userMessage: $"{cartridge} on {model}: the absorbing clock's machine state diverged from the reference's by tick {tick}."
                );
            }
        }
    }
}
