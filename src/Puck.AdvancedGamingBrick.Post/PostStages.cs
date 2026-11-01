namespace Puck.AdvancedGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery reports these in array order (Tier A first) and starts its
/// concurrent stages in that order, so the long self-contained stages belong near the head; <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in report order.</returns>
    public static IPostStage<PostContext>[] Create() {
        var shared = MachineStageTable.Create<PostContext, AdvancedMachineHost>(
            determinism: RunDeterminism, forkDeterminism: RunForkDeterminism,
            throughput: RunThroughput, allocation: RunAllocation, hosts: HostFactory);

        return [
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
            shared["determinism"],
            new StateRoundTripStage(),
            shared["fork-determinism"],
            new SaveRoundTripStage(),
            shared["queued-host-backpressure"],
            shared["queued-host-frame-publication"],
            shared["queued-host-audio"],
            shared["queued-host-time-travel"],
            shared["throughput"],
            shared["zero-alloc"],
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

    private static PostStageOutcome RunDeterminism(PostContext context) {
        var rom = SyntheticRom.Create();

        return MachineStageProbes.VerifyDeterminism<PostMachine, AgbMachineSnapshot, AgbMachineIdentity, long>(
            build: () => PostMachine.Build(
                bios: context.BiosImage,
                rom: rom
            ),
            describeDivergence: HashDivergenceProbe.DescribeDivergence,
            frames: 200,
            runFrames: static (machine, frames) => machine.RunFrames(frames: frames),
            snapshot: static machine => machine.Machine.Snapshot()
        );
    }
    private static PostStageOutcome RunForkDeterminism(PostContext context) {
        ArgumentNullException.ThrowIfNull(argument: context);

        using var parent = PostMachine.BuildInstance(
            bios: context.BiosImage,
            rom: SyntheticRom.Create()
        );

        return MachineStageProbes.VerifyForkLifecycle<AdvancedGamingBrickMachine, AgbMachineConfiguration, AgbMachineSnapshot, AgbMachineIdentity, long>(
            describeDivergence: HashDivergenceProbe.DescribeDivergence,
            parent: parent,
            runFrames: PostMachine.RunFrames,
            snapshot: static machine => machine.Snapshot(),
            tailFrames: 200,
            warmFrames: 200
        );
    }
    private static PostStageOutcome RunThroughput(PostContext context) =>
        MachineStageProbes.MeasureThroughput(
            benchFrames: 200,
            build: () => PostMachine.Build(
                bios: context.BiosImage,
                rom: SyntheticRom.Create()
            ),
            cycleUnit: "Mcycle/s",
            cyclesPerFrame: PostMachine.CyclesPerFrame,
            hardwareFps: PostMachine.HardwareFps,
            runFrames: static (machine, frames) => machine.RunFrames(frames: frames),
            warmFrames: 15
        );
    private static PostStageOutcome RunAllocation(PostContext context) =>
        MachineStageProbes.VerifyZeroAllocation(
            build: () => PostMachine.Build(
                bios: context.BiosImage,
                rom: SyntheticRom.Create()
            ),
            measureFrames: 600,
            runFrames: static (machine, frames) => machine.RunFrames(frames: frames),
            warmFrames: 120
        );
    private static QueuedHostStageFactory<AdvancedMachineHost> HostFactory(PostContext context) {
        var bios = context.BiosImage.ToArray();

        return new(
            WithContent: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios),
            Empty: () => new AdvancedMachineHost(biosImage: bios),
            WithAudio: () => new AdvancedMachineHost(cartridgeRom: SyntheticRom.Create(), biosImage: bios,
                audioSampleRate: 32_000),
            Observe: ObserveState);
    }
    // The fixture's palette-entry-0 walk reflects its completed CPU iterations and exact cycle position.
    private static long ObserveState(AdvancedMachineHost host) {
        var light = host.EmittedLight;

        return (((long)BitConverter.SingleToUInt32Bits(value: light.X)) << 32)
            ^ (((long)BitConverter.SingleToUInt32Bits(value: light.Y)) << 16)
            ^ BitConverter.SingleToUInt32Bits(value: light.Z);
    }
}
