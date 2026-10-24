namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery reports these in array order (Tier A first) and starts its
/// concurrent stages in that order, so the long self-contained stages belong near the head; <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in report order.</returns>
    public static IPostStage<PostContext>[] Create() =>
        [
            // Tier A — core and queued-host self-tests (hand-assembled vectors + a synthetic cartridge; run anywhere).
            new SmokeStage(),
            new LifecycleStage(),
            new HostPersistenceStage(),
            new BundledFirmwareStage(),
            new FirmwareResetStage(),
            new FirmwarePresentationStage(),
            new FirmwareLegacySoundStage(),
            new FirmwareSoundGuardStage(),
            new FirmwareSequenceStage(),
            new EmbeddingStage(),
            new PpuCompositionStage(),
            new PpuTextRowStage(),
            new CartridgeFetchStage(),
            new CycleBudgetExecutionStage(),
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
            // Tier B — deterministic render-hash floors over the corpus's BIOS-independent direct-boot ppu screen demos. A
            // fixed instruction budget is not portable across BIOS implementations: hold image identity fixed and explain
            // a shifted floor before recapturing it with --render-hash.
            new RenderHashStage(floors: [
                new RenderFloor(
                    ExpectedHash: 0x19E7C5AF1FB0BF25ul,
                    Name: "ppu/shades",
                    RelativePath: "ppu/shades.gba",
                    Steps: 6_000_000
                ),
                new RenderFloor(
                    ExpectedHash: 0x62B76C0E0223A81Cul,
                    Name: "ppu/hello",
                    RelativePath: "ppu/hello.gba",
                    Steps: 6_000_000
                ),
                new RenderFloor(
                    ExpectedHash: 0x2F1E64B48356B525ul,
                    Name: "ppu/stripes",
                    RelativePath: "ppu/stripes.gba",
                    Steps: 6_000_000
                ),
            ]),
            // Tier B — the menu-driven accuracy suite: a measurement, not a gate (skip when the ROM is absent).
            new AccuracySuiteStage(),
            // Tier C — cross-machine link determinism (two consoles on one cable, stepped through AgbLinkSession;
            // self-contained hand-assembled micro-ROMs, so it runs anywhere).
            new FirmwareMultiBootStage(),
            new SerialClockStage(),
            new LinkReplayStage(),
            // Tier C — the same link cable proven transparent to a mid-exchange suspend/snapshot/restore/reconnect
            // churn (AgbLinkSession's credit-preserving resume token), doubled within one run; self-contained.
            new LinkChurnStage(),
        ];
}
