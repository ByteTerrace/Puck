namespace Puck.HumbleGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery runs these in array order (Tier A first); <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IPostStage[] Create() =>
        [
            // Tier A — core self-tests (self-contained synthetic ROM; run anywhere).
            new DeterminismStage(),
            new SnapshotRoundTripStage(),
            new BatterySaveStage(),
            new VictoryRegionStage(),
            new ForkDeterminismStage(),
            new AgbCostumeStage(),
            new TrioLockstepStage(),
            new CameraCaptureStage(),
            new ThroughputStage(),
            // Tier B — reference-ROM correctness (blargg via the $A000 result block; skip when the corpus is absent).
            new BlarggStage(group: "cpu-instrs", subPath: "cpu_instrs/individual", model: ConsoleModel.Dmg),
            new BlarggStage(group: "instr-timing", subPath: "instr_timing", model: ConsoleModel.Dmg),
            new BlarggStage(group: "mem-timing", subPath: "mem_timing/individual", model: ConsoleModel.Dmg),
            new BlarggStage(group: "dmg-sound", subPath: "dmg_sound/rom_singles", model: ConsoleModel.Dmg),
            new BlarggStage(group: "cgb-sound", subPath: "cgb_sound/rom_singles", model: ConsoleModel.Cgb),
            // Tier B — mooneye acceptance timing suite (serial Fibonacci signature; skip when the corpus is absent).
            new MooneyeStage(group: "timer", relativeDirectory: "timer", recurse: true),
            new MooneyeStage(group: "ppu", relativeDirectory: "ppu", recurse: true),
            new MooneyeStage(group: "interrupts", relativeDirectory: "interrupts", recurse: true),
            new MooneyeStage(group: "serial", relativeDirectory: "serial", recurse: true),
            new MooneyeStage(group: "oam-dma", relativeDirectory: "oam_dma", recurse: true),
            new MooneyeStage(group: "bits", relativeDirectory: "bits", recurse: true),
            new MooneyeStage(group: "instr", relativeDirectory: "instr", recurse: true),
            new MooneyeStage(group: "misc", relativeDirectory: "", recurse: false),
            // Tier C — cross-machine link determinism (self-contained synthetic ROMs; run anywhere).
            new SerialLinkStage(),
        ];
}
