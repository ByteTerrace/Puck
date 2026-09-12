using Puck.HumbleGamingBrick.Interfaces;
using Xunit;

namespace Puck.HumbleGamingBrick.Forge.Tests;

/// <summary>Observes original firmware artwork and audio produced by native instructions through the real PPU/APU.</summary>
public sealed class BootRomPresentationTests {
    // An independent pixel description, deliberately not read from the emitter's tile data.
    private static readonly string[] m_wordmark = [
        "01111100011001100011110001100110",
        "01100110011001100110011001101100",
        "01100110011001100110000001111000",
        "01111100011001100110000001110000",
        "01100000011001100110000001111000",
        "01100000011001100110011001101100",
        "01100000001111000011110001100110",
    ];

    [Theory]
    [InlineData(ConsoleModel.DmgB)]
    [InlineData(ConsoleModel.Mgb)]
    [InlineData(ConsoleModel.CgbD)]
    [InlineData(ConsoleModel.Ags)]
    [InlineData(ConsoleModel.Sgb2)]
    public void CompatibleFirmwareDrawsItsWordmarkAndProducesItsChime(ConsoleModel model) {
        var rom = BootRomProbeCartridge.Create(probe: BootRomLayout.For(model: model).Probes[0]);
        using var core = new HumbleGamingBrickCore(configuration: new MachineConfiguration(
            model: model, cartridgeRom: rom, bootRom: BootRomBuilder.Build(model: model)), dmgSpeed: true);
        core.ConfigureAudio(sampleRate: 48_000);
        var bus = core.Instance.GetRequiredService<ISystemBus>();
        var foundWordmark = false;
        var foundAudio = false;
        var seenScrolls = new HashSet<byte>();
        var samples = new short[4096];

        for (var frame = 0; frame < 40 && (bus.ReadByte(address: MemoryMap.BootRomDisable) & 1) == 0; ++frame) {
            core.RunCycles(cycles: 70_224);
            foundWordmark |= ContainsWordmark(pixels: core.Framebuffer);
            var count = core.DrainAudioSamples(destination: samples);
            if (frame < 28 && count != 0) {
                // A silent DAC can carry a constant offset; require an actual waveform while firmware still owns boot.
                foundAudio |= samples.AsSpan(start: 0, length: count).ContainsAnyExcept(value: samples[0]);
            }
            VerifyMachineSettle.SettleOutOfOamDma(machine: core.Instance.Machine,
                cpu: core.Instance.GetRequiredService<ICpu>(), label: "boot presentation");
            seenScrolls.Add(item: bus.ReadByte(address: 0xFF42));
        }

        Assert.True(condition: foundWordmark, userMessage: "The native framebuffer never contained the complete PUCK wordmark.");
        Assert.True(condition: seenScrolls.Count > 20, userMessage: "The native scroll did not advance across its animation.");
        Assert.Equal(expected: !model.IsSuperGameBoy(), actual: foundAudio);
        Assert.True(condition: (bus.ReadByte(address: MemoryMap.BootRomDisable) & 1) != 0,
            userMessage: "The branded startup did not hand execution to the cartridge.");
    }

    private static bool ContainsWordmark(ReadOnlySpan<uint> pixels) {
        const int Width = 160;
        const int Left = 64;
        var background = pixels[0];
        for (var top = 0; top < 137; ++top) {
            var matches = true;
            for (var row = 0; matches && row < m_wordmark.Length; ++row) {
                for (var column = 0; column < 32; ++column) {
                    if ((pixels[(top + row) * Width + Left + column] != background) != (m_wordmark[row][column] == '1')) {
                        matches = false;
                        break;
                    }
                }
            }
            if (matches) {
                return true;
            }
        }
        return false;
    }
}
