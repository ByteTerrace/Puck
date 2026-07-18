namespace Puck.AdvancedGamingBrick.Post;

/// <summary>
/// Tier-C stage: a golden replay proving a recorded light-level input SEQUENCE — the same per-segment discipline a
/// queued host's <c>ApplyInput</c> drives via <see cref="AgbCartridge.SetLightLevel"/> — replays byte-identically on a
/// real solar-sensor cart (named by <c>PUCK_AGB_SOLARROM</c>). Two freshly built, full-booted consoles run the identical
/// varying-light script; their final whole-machine snapshots must agree. Skips cleanly when the ROM or a real boot BIOS
/// is absent — no solar-sensor dump ships with the repo, so a clean machine never sees a red stage from this one. The
/// self-contained proof of the solar device's OWN protocol (no ROM asset needed) is the sibling <see cref="SolarDeviceStage"/>.
/// </summary>
internal sealed class SolarReplayStage : IPostStage {
    /// <summary>The environment variable naming the commercial solar-sensor ROM this stage replays a light script against.</summary>
    private const string RomEnvironmentVariable = "PUCK_AGB_SOLARROM";

    // Frames to advance: enough to cover a boot + several seconds of the varying-light script.
    private const int Frames = 300;

    // The light level changes every this many frames, cycling through the script below — coarse enough that a whole
    // engine-tick segment (a queued host's real granularity) plausibly holds one value, fine enough to exercise
    // several RESET/threshold transitions inside the run.
    private const int FramesPerLevel = 30;

    private static readonly byte[] s_script = [255, 200, 128, 64, 1, 0, 128, 255];

    /// <inheritdoc/>
    public string Name =>
        "solar-replay";

    /// <inheritdoc/>
    public PostTier Tier =>
        PostTier.C;

    /// <inheritdoc/>
    public PostStageOutcome Run(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        var romPath = Environment.GetEnvironmentVariable(variable: RomEnvironmentVariable);

        if (string.IsNullOrEmpty(value: romPath) || !File.Exists(path: romPath)) {
            return PostStageOutcome.Skip(detail: $"no solar-sensor ROM (set {RomEnvironmentVariable} to a commercial solar-sensor dump; the core protocol is proven ROM-free by solar-device)");
        }

        if (AgbBiosProfile.Identify(image: context.BiosImage.Span).Kind == AgbBiosKind.ReplacementStub) {
            return PostStageOutcome.Skip(detail: $"needs a real boot BIOS (PUCK_AGB_BIOS) to full-boot {Path.GetFileName(path: romPath)}");
        }

        var rom = File.ReadAllBytes(path: romPath);

        var first = RunScripted(bios: context.BiosImage, rom: rom, cartridge: out var firstCartridge);

        if (!firstCartridge.HasSolar) {
            return PostStageOutcome.Skip(detail: $"{Path.GetFileName(path: romPath)} did not key HasSolar in AgbGameOverrides — not recognized as a solar-sensor title");
        }

        var second = RunScripted(bios: context.BiosImage, rom: rom, cartridge: out _);

        if (!first.ContentEquals(other: second)) {
            return PostStageOutcome.Fail(detail: $"the varying-light script replayed differently across two identical runs — {HashDivergenceProbe.DescribeDivergence(a: first, b: second)}");
        }

        return PostStageOutcome.Pass(detail: $"{Path.GetFileName(path: romPath)}: an {s_script.Length}-step varying-light script ({FramesPerLevel} frames/step) replayed byte-identically over {Frames} frames ({first.Size} state bytes)");
    }

    // One complete scripted run from a freshly built, full-booted console: the light level is set once per
    // FramesPerLevel-frame segment (mirroring a queued host's per-segment ApplyInput), never mid-segment, so the
    // recorded input stays a pure function of frame count.
    private static AgbMachineSnapshot RunScripted(ReadOnlyMemory<byte> bios, byte[] rom, out AgbCartridge cartridge) {
        var instance = AgbMachineFactory.Create(configuration: new AgbMachineConfiguration(bios: bios, rom: (byte[])rom.Clone()));

        instance.Machine.Cpu.Reset();
        cartridge = instance.GetRequiredService<AgbCartridge>();

        var scriptIndex = 0;

        for (var frame = 0; (frame < Frames); ++frame) {
            if ((frame % FramesPerLevel) == 0) {
                cartridge.SetLightLevel(level: s_script[(scriptIndex % s_script.Length)]);
                ++scriptIndex;
            }

            _ = instance.Machine.RunFrame();
        }

        var snapshot = instance.Machine.Snapshot();

        instance.Dispose();

        return snapshot;
    }
}
