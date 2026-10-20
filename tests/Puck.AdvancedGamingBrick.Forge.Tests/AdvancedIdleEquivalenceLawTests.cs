using Puck.GamingBricks.Forge;
using Puck.Machines;

namespace Puck.AdvancedGamingBrick.Forge.Tests;

/// <summary>
/// Pins the forge frame wait to a halt and the advanced machine's halted spans to the halted steps they replace, bit
/// for bit. The shipped cartridge runs on one machine through <see cref="AdvancedGamingBrickCore.RunCycles"/>, which
/// idles a halted processor between events in one span, and on another one step at a time, under the same scripted
/// keys at the hosted tick budget with host audio on; the law compares their audio and complete state every tick.
/// </summary>
public sealed class AdvancedIdleEquivalenceLawTests {
    // One 30 Hz host tick of master cycles; the remainder ticks keep the budgets uneven.
    private const long AdvancedCyclesPerTick = 559_240L;
    private const int CounterBaselineTick = 15;
    private const int SampleRate = 48_000;
    private const int Ticks = 120;

    private static long FrameCounter(AdvancedGamingBrickCore core) =>
        ((AgbBus)core.Instance.Machine.Bus).DebugRead16(address: AgbForgeMemoryMap.FrameCounter);

    // pip is the shipped advanced cartridge; it is silent, so tetromino, built for this machine, carries music through
    // every channel the spans must leave exactly as the single steps would.
    [Theory]
    [InlineData("agb/pip.agb")]
    [InlineData("cgb/tetromino.cgb")]
    public void TheAdvancedMachineIdlesExactlyAsItsHaltedStepsWould(string cartridge) {
        var document = CartridgeDocuments.Parse(utf8: File.ReadAllBytes(path: RepositoryPaths.Resolve(relativePath: $"src/Puck.World/Assets/cartridges/{cartridge}.cartridge.json")));
        // An advanced palette holds sixteen colours; a Colour one, four.
        static int[][] Widen(int[][] palettes) =>
            [.. palettes.Select(selector: static palette => palette.Concat(second: new int[(16 - palette.Length)]).ToArray())];

        var rom = new AgbCartridgeCompiler().Compile(document: (document with {
            Palettes = new CartridgePalettes(
                Background: Widen(palettes: document.Palettes.Background),
                Object: Widen(palettes: document.Palettes.Object)
            ),
            Target = "agb",
        })).Rom;

        using var spans = new AdvancedGamingBrickCore(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: rom
        );
        using var steps = new AdvancedGamingBrickCore(
            bootMode: MachineBootMode.Fast,
            cartridgeRom: rom
        );

        var random = new Random(Seed: 11);
        var haltedSteps = 0L;
        var firstCount = 0L;
        var firstFrame = 0L;
        var spanState = Array.Empty<byte>();
        var stepState = Array.Empty<byte>();
        var spanSamples = new short[16_384];
        var stepSamples = new short[16_384];

        spans.ConfigureAudio(sampleRate: SampleRate);
        steps.ConfigureAudio(sampleRate: SampleRate);

        for (var tick = 1; (tick <= Ticks); tick++) {
            var keys = ((random.Next(maxValue: 4) == 0)
                ? ((ushort)(0x03FF & ~(1 << random.Next(maxValue: 10))))
                : ((ushort)0x03FF)
            );
            var budget = (AdvancedCyclesPerTick + (((tick % 3) == 0) ? 1L : 0L));
            var target = (steps.CycleCount + budget);

            spans.Instance.Machine.SetKeyInput(keys: keys);
            steps.Instance.Machine.SetKeyInput(keys: keys);
            spans.RunCycles(cycles: budget);

            while (steps.CycleCount < target) {
                if (steps.Instance.Machine.Bus.Halted) {
                    ++haltedSteps;
                }

                steps.Instance.Machine.Step();
            }

            if (tick == CounterBaselineTick) {
                firstCount = FrameCounter(core: spans);
                firstFrame = spans.NativeFrameIndex;
            }

            var spanCount = spans.DrainAudioSamples(destination: spanSamples);
            var stepCount = steps.DrainAudioSamples(destination: stepSamples);
            var spanLength = spans.CaptureState(buffer: ref spanState);
            var stepLength = steps.CaptureState(buffer: ref stepState);

            Assert.True(
                condition: spanSamples.AsSpan(length: spanCount, start: 0).SequenceEqual(other: stepSamples.AsSpan(length: stepCount, start: 0)),
                userMessage: $"{cartridge}: the halted spans' audio diverged from the halted steps' at tick {tick}."
            );
            Assert.True(
                condition: spanState.AsSpan(length: spanLength, start: 0).SequenceEqual(other: stepState.AsSpan(length: stepLength, start: 0)),
                userMessage: $"{cartridge}: the halted spans' machine state diverged from the halted steps' at tick {tick}."
            );
        }

        // The cartridge halts until each V-blank rather than polling for it, so most of the run is halted cycles, one
        // a step, and its frame counter still advances once a native frame.
        Assert.True(
            condition: (haltedSteps > (((Ticks * AdvancedCyclesPerTick) * 3L) / 4L)),
            userMessage: $"{cartridge} halted for {haltedSteps} of {(Ticks * AdvancedCyclesPerTick)} cycles; its frame wait does not halt."
        );
        Assert.InRange(
            actual: (FrameCounter(core: spans) - firstCount),
            high: ((spans.NativeFrameIndex - firstFrame) + 1L),
            low: ((spans.NativeFrameIndex - firstFrame) - 1L)
        );
    }
}
