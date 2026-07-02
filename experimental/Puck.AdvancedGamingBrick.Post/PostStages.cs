namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery runs these in array order (Tier A first); <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IPostStage[] Create() =>
        [
            // Tier A — core self-tests (hand-assembled vectors + a self-contained synthetic cartridge; run anywhere).
            new SmokeStage(),
            new DeterminismStage(),
            new SaveRoundTripStage(),
            new ThroughputStage(),
            // Tier B — jsmolka gba-tests (r12 verdict register; skip when the corpus is absent).
            new JsmolkaStage(group: "cpu", cases: [("arm/arm.gba", "arm"), ("thumb/thumb.gba", "thumb"), ("memory/memory.gba", "memory")]),
            new JsmolkaStage(group: "save", cases: [("save/none.gba", "none"), ("save/sram.gba", "sram"), ("save/flash64.gba", "flash64"), ("save/flash128.gba", "flash128")]),
            new JsmolkaStage(group: "misc", cases: [("nes/nes.gba", "nes")]),
            // Tier B — FuzzARM randomized ARM/Thumb coverage (EWRAM failure marker; skip when absent).
            new FuzzArmStage(),
            // Tier B — deterministic render-hash floors. The ppu screen demos are BIOS-independent direct-boot ROMs from
            // the corpus; the two commercial games are user-supplied and BIOS-dependent (they run BIOS SWIs during boot),
            // so their floors were captured with a real replacement BIOS — portable across the real BIOSes on hand — and
            // skip on the zeroed stub. Re-capture a shifted floor with --render-hash after confirming the frame is still
            // visually correct.
            new RenderHashStage(floors: [
                new RenderFloor(Source: RenderFloorSource.Corpus, RelativePath: "ppu/shades.gba", Name: "ppu/shades", Steps: 6_000_000, ExpectedHash: 0x19E7C5AF1FB0BF25ul, NeedsBios: false),
                new RenderFloor(Source: RenderFloorSource.Corpus, RelativePath: "ppu/hello.gba", Name: "ppu/hello", Steps: 6_000_000, ExpectedHash: 0x62B76C0E0223A81Cul, NeedsBios: false),
                new RenderFloor(Source: RenderFloorSource.Corpus, RelativePath: "ppu/stripes.gba", Name: "ppu/stripes", Steps: 6_000_000, ExpectedHash: 0x2F1E64B48356B525ul, NeedsBios: false),
                new RenderFloor(Source: RenderFloorSource.Games, RelativePath: "A.gba", Name: "A (Golden Sun)", Steps: 120_000_000, ExpectedHash: 0x634D863B8CE386E8ul, NeedsBios: true),
                new RenderFloor(Source: RenderFloorSource.Games, RelativePath: "AGS Aging Cartridge (World) (v7.1).gba", Name: "AGS menu", Steps: 6_000_000, ExpectedHash: 0x64044FC6D20B9C93ul, NeedsBios: true),
            ]),
            // Tier B — reference suites with known partial conformance (measurement, not a gate; skip when the ROM is absent).
            new MgbaSuiteStage(),
            new AgsStage(),
        ];
}
