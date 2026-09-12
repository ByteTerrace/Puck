using System.Runtime.InteropServices;
using Puck.Assets;
using Puck.GamingBricks;

namespace Puck.AdvancedGamingBrick.Post;

/// <summary>Exercises bundled cold-boot pixels, PCM, replay, and native cartridge handoff without an external BIOS.</summary>
internal sealed class FirmwarePresentationStage : IPostStage<PostContext> {
    private const int FrameCycles = AdvancedGamingBrickMachine.CyclesPerFrame;
    private const int SampleRate = 48_000;
    private const long ColdBootBudget = 120L * FrameCycles;

    /// <inheritdoc/>
    public string Name => "firmware-presentation";
    /// <inheritdoc/>
    public PostTier Tier => PostTier.A;
    /// <inheritdoc/>
    public bool IsConcurrent => true;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        try {
            var artifacts = Path.Combine(path1: context.ArtifactsDirectory, path2: Name);
            Directory.CreateDirectory(path: artifacts);
            var peak = FirmwarePresentationArtwork.Create();
            var half = FirmwarePresentationArtwork.Create(dark: 8);
            var rom = FirmwarePresentationCartridge.Create();
            using var cold = new AdvancedGamingBrickCore(cartridgeRom: rom);
            cold.ConfigureAudio(sampleRate: SampleRate);
            Require(condition: AgbBiosProfile.Identify(image: AgbFirmware.GetImage()).Kind == AgbBiosKind.Puck,
                detail: "presentation did not select the bundled Puck image");
            var startup = new List<short>();
            var sawFadeIn = false;
            for (var frame = 0; frame < 64 && !cold.Framebuffer.SequenceEqual(other: peak); ++frame) {
                cold.RunCycles(cycles: FrameCycles);
                startup.AddRange(collection: Drain(apu: cold.Instance.Machine.Apu));
                if (!sawFadeIn && cold.Framebuffer.SequenceEqual(other: half)) {
                    Save(core: cold, directory: artifacts, name: "fade-in.png");
                    sawFadeIn = true;
                }
            }
            Save(core: cold, directory: artifacts, name: "wordmark.png");
            Require(condition: cold.Framebuffer.SequenceEqual(other: peak), detail: "native PUCK/BYTETERRACE geometry or palette mismatch; inspect wordmark.png");
            Require(condition: sawFadeIn, detail: "startup did not render its half-bright fade-in frame");
            Require(condition: !HasEnteredCartridge(core: cold), detail: "branding was not rendered while the CPU was executing firmware");
            CheckReplay(core: cold, startup: startup);
            var sawFadeOut = false;
            while (cold.CycleCount < ColdBootBudget && !HasEnteredCartridge(core: cold)) {
                cold.RunCycles(cycles: FrameCycles);
                startup.AddRange(collection: Drain(apu: cold.Instance.Machine.Apu));
                if (!sawFadeOut && cold.Framebuffer.SequenceEqual(other: half)) {
                    Save(core: cold, directory: artifacts, name: "fade-out.png");
                    sawFadeOut = true;
                }
            }
            Require(condition: sawFadeOut, detail: "startup did not render its half-bright fade-out frame");
            Require(condition: cold.CycleCount >= 72L * FrameCycles, detail: "cold startup skipped its native presentation interval");
            CheckHandoff(core: cold);
            Save(core: cold, directory: artifacts, name: "handoff.png");
            var (firstNote, secondNote) = CheckSound(samples: startup);

            // Header fields are not a content-authentication boundary in the full-capacity firmware.
            // These original fixtures prove arbitrary logo fields and malformed metadata, not retail-game conformance.
            CheckHeader(alternateLogo: true, malformedHeader: false, peak: peak);
            CheckHeader(alternateLogo: false, malformedHeader: true, peak: peak);
            using var fast = new AdvancedGamingBrickCore(cartridgeRom: rom, bootMode: MachineBootMode.Fast);
            fast.ConfigureAudio(sampleRate: SampleRate);
            fast.RunCycles(cycles: FrameCycles);
            CheckHandoff(core: fast);
            Require(condition: cold.Instance.Machine.Identity == fast.Instance.Machine.Identity, detail: "fast startup discarded the selected firmware");
            return PostStageOutcome.Pass(detail: $"38,400 native pixels plus fade-in/out; stereo startup PCM ({firstNote}/{secondNote} lower/upper-note periods); mid-boot replay/fork bytes and PCM; stale-audio discard; cold/fast handoff and unchecked header policy; artifacts: {artifacts}");
        } catch (InvalidOperationException exception) {
            return PostStageOutcome.Fail(detail: exception.Message);
        }
    }

    private static void CheckReplay(AdvancedGamingBrickCore core, List<short> startup) {
        var checkpoint = core.Instance.Machine.Snapshot();
        byte[] raw = [];
        var length = core.CaptureState(buffer: ref raw);
        core.RunCycles(cycles: 4096);
        core.Instance.Machine.Restore(snapshot: checkpoint);
        Require(condition: Drain(apu: core.Instance.Machine.Apu).Length == 0, detail: "typed mid-boot restore exposed stale queued PCM");
        using var fork = core.Instance.Fork();
        // The output rate is a host setting; configure first, then restore the emulated sample phase.
        fork.Machine.Apu.ConfigureOutput(sampleRate: SampleRate);
        fork.Machine.Restore(snapshot: checkpoint);
        var futurePcm = new List<short>();
        int[] slices = [17, 1023, FrameCycles - 1040];
        for (var frame = 0; frame < 18; ++frame) {
            foreach (var cycles in slices) {
                core.RunCycles(cycles: cycles);
                fork.Machine.RunCycles(cycles: cycles);
                Require(condition: core.Instance.Machine.Snapshot().Data.SequenceEqual(other: fork.Machine.Snapshot().Data), detail: $"mid-boot fork state diverged at frame {frame}, slice {cycles}");
                Require(condition: core.Framebuffer.SequenceEqual(other: fork.Machine.Framebuffer), detail: "mid-boot fork pixels diverged");
                var pcm = Drain(apu: core.Instance.Machine.Apu);
                Require(condition: pcm.AsSpan().SequenceEqual(other: Drain(apu: fork.Machine.Apu)), detail: "mid-boot fork PCM diverged");
                futurePcm.AddRange(collection: pcm);
            }
        }
        var expectedState = core.Instance.Machine.Snapshot();
        var expectedPixels = core.Framebuffer.ToArray();
        core.RunCycles(cycles: 4096); // Leave unconsumed audio in the presentation ring before rewinding.
        core.RestoreState(buffer: raw, length: length);
        Require(condition: Drain(apu: core.Instance.Machine.Apu).Length == 0, detail: "restoring mid-boot state exposed stale queued PCM");
        var replayPcm = new List<short>();
        for (var frame = 0; frame < 18; ++frame) {
            foreach (var cycles in slices) {
                core.RunCycles(cycles: cycles);
                replayPcm.AddRange(collection: Drain(apu: core.Instance.Machine.Apu));
            }
        }
        Require(condition: core.Instance.Machine.Snapshot().Data.SequenceEqual(other: expectedState.Data), detail: "raw mid-boot snapshot replay changed machine bytes");
        Require(condition: core.Framebuffer.SequenceEqual(other: expectedPixels), detail: "raw mid-boot snapshot replay changed pixels");
        Require(condition: CollectionsMarshal.AsSpan(list: replayPcm).SequenceEqual(other: CollectionsMarshal.AsSpan(list: futurePcm)), detail: "raw mid-boot snapshot replay changed fresh PCM");
        startup.AddRange(collection: replayPcm);
    }

    private static void CheckHeader(bool alternateLogo, bool malformedHeader, uint[] peak) {
        using var core = new AdvancedGamingBrickCore(cartridgeRom: FirmwarePresentationCartridge.Create(alternateLogo: alternateLogo, malformedHeader: malformedHeader));
        var sawWordmark = false;
        while (core.CycleCount < ColdBootBudget && !HasEnteredCartridge(core: core)) {
            core.RunCycles(cycles: FrameCycles);
            sawWordmark |= core.Framebuffer.SequenceEqual(other: peak);
        }
        Require(condition: sawWordmark, detail: $"header fields changed native branding: alternate={alternateLogo}, malformed={malformedHeader}");
        CheckHandoff(core: core);
    }

    private static void CheckHandoff(AdvancedGamingBrickCore core) {
        Require(condition: HasEnteredCartridge(core: core), detail: "firmware did not hand control to the diagnostic cartridge within 120 frames");
        Require(condition: core.Instance.Machine.Bus.Read32(address: 0x02000000, access: BusAccessType.NonSequential) == 0x5A, detail: "cartridge entry did not write its EWRAM marker");
        core.RunCycles(cycles: FrameCycles);
        var expected = FirmwarePresentationArtwork.Color(rgb555: 0x1234);
        foreach (var pixel in core.Framebuffer) {
            Require(condition: pixel == expected, detail: "cartridge did not replace the firmware picture after handoff");
        }
        // Discard the partly completed handoff frame, then demand fresh silent output.
        _ = Drain(apu: core.Instance.Machine.Apu);
        core.RunCycles(cycles: FrameCycles);
        foreach (var sample in Drain(apu: core.Instance.Machine.Apu)) {
            Require(condition: sample == 0, detail: "firmware startup sound leaked beyond cartridge handoff");
        }
    }

    private static (int FirstNote, int SecondNote) CheckSound(List<short> samples) {
        Require(condition: samples.Count > SampleRate && samples.Count % 2 == 0, detail: "startup did not produce complete stereo PCM");
        var previousRise = -1;
        var firstNote = 0;
        var secondNote = 0;
        var firstLower = int.MaxValue;
        var firstUpper = int.MaxValue;
        for (var index = 0; index < samples.Count; index += 2) {
            Require(condition: samples[index] == samples[index + 1], detail: "startup chime is not centered in stereo");
            Require(condition: Math.Abs(value: (int)samples[index]) < 4096, detail: "startup chime exceeded its quiet amplitude budget");
            if (index >= 2 && samples[index] > samples[index - 2]) {
                var frame = index / 2;
                var interval = frame - previousRise;
                // The authored D5 then A5 pulse periods at 48 kHz: about 82 and 55 sample frames.
                if (previousRise >= 0 && interval is >= 81 and <= 83) {
                    ++firstNote;
                    firstLower = Math.Min(val1: firstLower, val2: frame);
                }
                if (previousRise >= 0 && interval is >= 54 and <= 56) {
                    ++secondNote;
                    firstUpper = Math.Min(val1: firstUpper, val2: frame);
                }
                previousRise = frame;
            }
        }
        Require(condition: firstNote >= 20 && secondNote >= 20, detail: $"startup PCM did not contain both ascending chime notes ({firstNote}/{secondNote} periods)");
        Require(condition: firstLower < firstUpper, detail: "startup chime did not play its lower note before its upper note");
        return (firstNote, secondNote);
    }

    private static bool HasEnteredCartridge(AdvancedGamingBrickCore core) =>
        core.Instance.Machine.Cpu.GetRegister(index: 15) is >= 0x080000C0 and < 0x08000100;

    private static short[] Drain(IAgbApu apu) {
        var samples = new List<short>();
        Span<short> buffer = stackalloc short[4096];
        int count;
        while ((count = apu.DrainSamples(destination: buffer)) != 0) {
            for (var index = 0; index < count; ++index) {
                samples.Add(item: buffer[index]);
            }
        }
        return samples.ToArray();
    }

    private static void Save(AdvancedGamingBrickCore core, string directory, string name) =>
        PngEncoder.Write(height: 160, path: Path.Combine(path1: directory, path2: name), rgba: MemoryMarshal.AsBytes(span: core.Framebuffer), width: 240);

    private static void Require(bool condition, string detail) => FirmwareSwiProbe.Require(condition: condition, detail: detail);
}
