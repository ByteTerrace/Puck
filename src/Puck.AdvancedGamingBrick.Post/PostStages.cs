namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery runs these in array order (Tier A first); <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IPostStage[] Create() =>
        [
            // Tier A — core and queued-host self-tests (hand-assembled vectors + a synthetic cartridge; run anywhere).
            new SmokeStage(),
            new DeterminismStage(),
            new StateRoundTripStage(),
            new ForkDeterminismStage(),
            new SaveRoundTripStage(),
            new QueuedHostBackpressureStage(),
            new QueuedHostFramePublicationStage(),
            new QueuedHostAudioStage(),
            new QueuedHostTimeTravelStage(),
            new ThroughputStage(),
            new AllocationStage(),
            // Tier A — the solar sensor's GPIO protocol, proven directly against AgbCartridge (no ROM asset).
            new SolarDeviceStage(),
            // Tier B — reference conformance suites (r12 verdict register; skip when the corpus is absent).
            new ConformanceRomStage(
        cases: [("arm/arm.gba", "arm"), ("thumb/thumb.gba", "thumb"), ("memory/memory.gba", "memory")],
        group: "cpu"
    ),
            new ConformanceRomStage(
        cases: [("save/none.gba", "none"), ("save/sram.gba", "sram"), ("save/flash64.gba", "flash64"), ("save/flash128.gba", "flash128")],
        group: "save"
    ),
            new ConformanceRomStage(
        cases: [("nes/nes.gba", "nes")],
        group: "misc"
    ),
            // Tier B — ARM/Thumb fuzz-corpus coverage (EWRAM failure marker; skip when absent).
            new ArmFuzzStage(),
            // Tier B — deterministic render-hash floors. The ppu screen demos are BIOS-independent direct-boot ROMs from
            // the corpus; the two commercial games are user-supplied and BIOS-dependent (they run BIOS SWIs during boot),
            // so their floors were captured with a real replacement BIOS — portable across the real BIOSes on hand — and
            // skip on the zeroed stub. Re-capture a shifted floor with --render-hash after confirming the frame is still
            // visually correct.
            new RenderHashStage(floors: [
                new RenderFloor(
        ExpectedHash: 0x19E7C5AF1FB0BF25ul,
        Name: "ppu/shades",
        NeedsBios: false,
        RelativePath: "ppu/shades.gba",
        Source: RenderFloorSource.Corpus,
        Steps: 6_000_000
    ),
                new RenderFloor(
        ExpectedHash: 0x62B76C0E0223A81Cul,
        Name: "ppu/hello",
        NeedsBios: false,
        RelativePath: "ppu/hello.gba",
        Source: RenderFloorSource.Corpus,
        Steps: 6_000_000
    ),
                new RenderFloor(
        ExpectedHash: 0x2F1E64B48356B525ul,
        Name: "ppu/stripes",
        NeedsBios: false,
        RelativePath: "ppu/stripes.gba",
        Source: RenderFloorSource.Corpus,
        Steps: 6_000_000
    ),
                new RenderFloor(
        ExpectedHash: 0x634D863B8CE386E8ul,
        Name: "A (commercial RPG)",
        NeedsBios: true,
        RelativePath: "A.gba",
        Source: RenderFloorSource.Games,
        Steps: 120_000_000
    ),
                new RenderFloor(
        ExpectedHash: 0x64044FC6D20B9C93ul,
        Name: "AGS menu",
        NeedsBios: true,
        RelativePath: "AGS Aging Cartridge (World) (v7.1).gba",
        Source: RenderFloorSource.Games,
        Steps: 6_000_000
    ),
            ]),
            // Tier B — reference suites with known partial conformance (measurement, not a gate; skip when the ROM is absent).
            new AccuracySuiteStage(),
            new AgsStage(),
            // Tier C — cross-machine link determinism (two consoles on one cable, stepped through AgbLinkSession;
            // self-contained hand-assembled micro-ROMs, so it runs anywhere).
            new LinkReplayStage(),
            // Tier C — the same link cable proven transparent to a mid-exchange suspend/snapshot/restore/reconnect
            // churn (AgbLinkSession's credit-preserving resume token), doubled within one run; self-contained.
            new LinkChurnStage(),
            // Tier C — the same link stack under a REAL commercial multiplayer game (two full-booted consoles on one
            // cable); proves the game's SIO link probe engages over the cable and the linked scenario is
            // replay-identical. Skips when PUCK_AGB_LINK_GAME or a real boot BIOS is absent.
            new LinkGameReplayStage(),
            // Tier C — a recorded varying-light script replays byte-identically on a real solar-sensor cart. Skips
            // when PUCK_AGB_SOLARROM or a real boot BIOS is absent (no solar-sensor dump ships with the repo).
            new SolarReplayStage(),
        ];
}
