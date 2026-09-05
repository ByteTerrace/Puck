namespace Puck.HumbleGamingBrick.Post;

/// <summary>The ordered POST stage registry. The battery runs these in array order (Tier A first); <c>--tier</c> and
/// <c>--filter</c> select a subset without changing the order.</summary>
internal static class PostStages {
    /// <summary>Creates the ordered stage list.</summary>
    /// <returns>The stages, in run order.</returns>
    public static IPostStage<PostContext>[] Create() =>
        [
            // Tier A — core self-tests (self-contained synthetic ROM; run anywhere).
            new DeterminismStage(),
            new SnapshotRoundTripStage(),
            new BatterySaveStage(),
            new VictoryRegionStage(),
            new ForkDeterminismStage(),
            new AgbCostumeStage(),
            // Tier A — every revision's authored boot ROM lands on the same observable handoff as the seeded post-boot
            // state, across the header classes its timing and register tables branch on (self-contained; run anywhere).
            new BootRomHandoffStage(),
            new TrioLockstepStage(),
            new CameraCaptureStage(),
            new QueuedHostBackpressureStage(),
            new QueuedHostFramePublicationStage(),
            new QueuedHostAudioStage(),
            new QueuedHostMemoryAccessStage(),
            new QueuedHostTimeTravelStage(),
            // Tier A — two RUNNING queued hosts cable-linked into one owned group: the link steps instead of its
            // members, per-seat pads route by cable order, both members keep publishing, the group backpressures as a
            // unit, and the sever returns independent stepping (self-contained synthetic ROM; run anywhere).
            new LinkedHostCableStage(),
            // Tier A — a live cable link is replay-identical and rewinds as one coupled group, pair-stepper pacing
            // included (self-contained synthetic ROM; run anywhere).
            new LinkedHostTimeTravelStage(),
            new ThroughputStage(),
            new AllocationStage(),
            // Tier A — STOP's pad-byte consumption is gated on a pending interrupt (IE & IF, independent of IME), not
            // unconditional (self-contained synthetic ROM; run anywhere).
            new Sm83StopPendingInterruptStage(),
            // Tier A — a watchpoint hit reports the ACCESSING instruction's PC, across every debugger advance
            // granularity (self-contained synthetic ROM; run anywhere).
            new WatchpointAccessPcStage(),
            // Tier A — the MBC5 rumble variant's motor latch (self-contained synthetic ROM; run anywhere).
            new RumbleDeviceStage(),
            // Tier A — the machine's serial printer peripheral as a deterministic serial-cable peer: a synthetic ROM drives
            // it through INIT/DATA(raw+RLE)/PRINT/STATUS, and the emitted print (a machine-to-host event) is replay- and
            // churn-identical (self-contained synthetic ROM; run anywhere).
            new PrinterStage(),
            // Tier A — the BESS importer's validate-then-apply safety contract (M-08): every malformed-corpus case is
            // rejected before any machine state mutates (self-contained synthetic ROM; run anywhere).
            new BessImportGuardStage(),
            // Tier A — SerialLinkSession.Suspend refuses a mid-transfer boundary rather than silently severing the
            // cable (self-contained synthetic ROM; run anywhere).
            new SerialSuspendIdleGuardStage(),
            // Tier A — the resume constructor refuses a credit that exceeds the machine's cycle count rather than
            // wrapping the pacing target with unsigned arithmetic (self-contained synthetic ROM; run anywhere).
            new SerialResumeCreditGuardStage(),
            // Tier A — the infrared resume constructor refuses an oversized credit the same way, and does so before either
            // transceiver is wired (self-contained synthetic ROM; run anywhere).
            new InfraredResumeCreditGuardStage(),
            // Tier B — reference-ROM correctness (conformance ROMs via the $A000 result block; ledger-gated; skip when
            // the corpus is absent).
            new LedgerRomStage(
        discover: context => SuiteCatalog.ConformanceLedgerCases(
            group: "cpu-instrs",
            model: ConsoleModel.DmgC,
            root: context.TestRomRoot,
            subPath: "cpu_instrs/individual"
        ),
        name: "conformance-cpu-instrs"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.ConformanceLedgerCases(
            group: "instr-timing",
            model: ConsoleModel.DmgC,
            root: context.TestRomRoot,
            subPath: "instr_timing"
        ),
        name: "conformance-instr-timing"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.ConformanceLedgerCases(
            group: "mem-timing",
            model: ConsoleModel.DmgC,
            root: context.TestRomRoot,
            subPath: "mem_timing/individual"
        ),
        name: "conformance-mem-timing"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.ConformanceLedgerCases(
            group: "dmg-sound",
            model: ConsoleModel.DmgC,
            root: context.TestRomRoot,
            subPath: "dmg_sound/rom_singles"
        ),
        name: "conformance-dmg-sound"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.ConformanceLedgerCases(
            group: "cgb-sound",
            model: ConsoleModel.CgbE,
            root: context.TestRomRoot,
            subPath: "cgb_sound/rom_singles"
        ),
        name: "conformance-cgb-sound"
    ),
            // Tier B — the un-gated blargg corpus: oam_bug/mem_timing-2 singles via the same $A000 block, and the
            // top-level ROMs (halt_bug, interrupt_time, oam_bug, mem_timing-2) that report by screen content instead.
            new LedgerRomStage(
        discover: context => SuiteCatalog.BlarggOamBugSinglesRoms(root: context.TestRomRoot),
        name: "conformance-oam-bug-singles"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.BlarggMemTiming2SinglesRoms(root: context.TestRomRoot),
        name: "conformance-mem-timing-2-singles"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.BlarggVisualRoms(root: context.TestRomRoot),
        name: "blargg-visual"
    ),
            // Tier B — SingleStepTests/sm83 per-instruction vectors: the shared SM83 core against 500 opcode families
            // on a flat-RAM harness, off-ROM (skip when PUCK_GB_SST is absent).
            new Sm83SstStage(),
            // Tier B — acceptance timing suite (serial Fibonacci signature; ledger-gated; skip when the corpus is absent).
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "timer",
            recurse: true,
            relativeDirectory: "timer",
            root: context.TestRomRoot
        ),
        name: "acceptance-timer"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "ppu",
            recurse: true,
            relativeDirectory: "ppu",
            root: context.TestRomRoot
        ),
        name: "acceptance-ppu"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "interrupts",
            recurse: true,
            relativeDirectory: "interrupts",
            root: context.TestRomRoot
        ),
        name: "acceptance-interrupts"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "serial",
            recurse: true,
            relativeDirectory: "serial",
            root: context.TestRomRoot
        ),
        name: "acceptance-serial"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "oam-dma",
            recurse: true,
            relativeDirectory: "oam_dma",
            root: context.TestRomRoot
        ),
        name: "acceptance-oam-dma"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "bits",
            recurse: true,
            relativeDirectory: "bits",
            root: context.TestRomRoot
        ),
        name: "acceptance-bits"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "instr",
            recurse: true,
            relativeDirectory: "instr",
            root: context.TestRomRoot
        ),
        name: "acceptance-instr"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.AcceptanceLedgerCases(
            group: "misc",
            recurse: false,
            relativeDirectory: "",
            root: context.TestRomRoot
        ),
        name: "acceptance-misc"
    ),
            // Tier B — the rest of the mooneye-test-suite tree: emulator-only (mbc1/mbc2/mbc5), misc (boot state and
            // I/O), and the one manual screenshot case.
            new LedgerRomStage(
        discover: context => SuiteCatalog.MooneyeEmulatorOnlyRoms(root: context.TestRomRoot),
        name: "mooneye-emulator-only"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.MooneyeMiscRoms(root: context.TestRomRoot),
        name: "mooneye-misc"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.MooneyeManualRoms(root: context.TestRomRoot),
        name: "mooneye-manual"
    ),
            // Tier B — the wilbertpol fork: same tree shape, register-signature read (this fork never emits its
            // result over serial) except the visual manual case.
            new LedgerRomStage(
        discover: context => SuiteCatalog.WilbertpolAcceptanceRoms(root: context.TestRomRoot),
        name: "wilbertpol-acceptance"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.WilbertpolEmulatorOnlyRoms(root: context.TestRomRoot),
        name: "wilbertpol-emulator-only"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.WilbertpolMiscRoms(root: context.TestRomRoot),
        name: "wilbertpol-misc"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.WilbertpolManualRoms(root: context.TestRomRoot),
        name: "wilbertpol-manual"
    ),
            // Tier B — SameSuite: also never emits its signature over serial, and its own apu/README.md restricts
            // most of apu/ to CPU-CGB-E.
            new LedgerRomStage(
        discover: context => SuiteCatalog.SameSuiteRoms(root: context.TestRomRoot),
        name: "same-suite"
    ),
            // Tier B — GBMicrotest: 513 small DMG-only ROMs read through the $FF80-$FF82 result block.
            new LedgerRomStage(
        discover: context => SuiteCatalog.GbMicrotestRoms(root: context.TestRomRoot),
        name: "gbmicrotest"
    ),
            // Tier B — AGE: register-signature or screenshot depending on what each ROM's own leaf folder ships.
            new LedgerRomStage(
        discover: context => SuiteCatalog.AgeRoms(root: context.TestRomRoot),
        name: "age"
    ),
            // Tier B — the acid family and mealybug: pixel-exact screenshot comparisons under the shared "common
            // palette" this framebuffer already produces.
            new LedgerRomStage(
        discover: context => SuiteCatalog.DmgAcid2Roms(root: context.TestRomRoot),
        name: "dmg-acid2"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.CgbAcid2Roms(root: context.TestRomRoot),
        name: "cgb-acid2"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.CgbAcidHellRoms(root: context.TestRomRoot),
        name: "cgb-acid-hell"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.MealybugRoms(root: context.TestRomRoot),
        name: "mealybug"
    ),
            // Tier B — gambatte: hex-pattern, audio-silence-or-sound, and screenshot cases all route mechanically per
            // ROM stem (see SuiteCatalog.GambatteRoms); only button-driven or dump-only ROMs stay unrunnable.
            new LedgerRomStage(
        discover: context => SuiteCatalog.GambatteRoms(root: context.TestRomRoot),
        name: "gambatte"
    ),
            // Tier B — the small screenshot suites.
            new LedgerRomStage(
        discover: context => SuiteCatalog.LittleThingsRoms(root: context.TestRomRoot),
        name: "little-things-gb"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.ScribbleTestsRoms(root: context.TestRomRoot),
        name: "scribbltests"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.StrikethroughRoms(root: context.TestRomRoot),
        name: "strikethrough"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.TurtleTestsRoms(root: context.TestRomRoot),
        name: "turtle-tests"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.BullyRoms(root: context.TestRomRoot),
        name: "bully"
    ),
            // Tier B — suites this battery cannot drive mechanically (button-input selection); recorded unrunnable
            // rather than omitted from the ledger.
            new LedgerRomStage(
        discover: context => SuiteCatalog.Rtc3TestRoms(root: context.TestRomRoot),
        name: "rtc3test"
    ),
            new LedgerRomStage(
        discover: context => SuiteCatalog.Mbc3TesterRoms(root: context.TestRomRoot),
        name: "mbc3-tester"
    ),
            // Tier C — cross-machine link determinism, one stage per generation pairing (self-contained synthetic
            // ROMs; run anywhere). Dmg↔Cgb is the original pairing; Dmg↔Agb and Cgb↔Agb prove the carry-forward
            // rule's Agb costume links through the identical SerialLinkSession machinery.
            new SerialLinkStage(
        masterModel: ConsoleModel.DmgC,
        name: "serial-link",
        slaveModel: ConsoleModel.CgbE
    ),
            new SerialLinkStage(
        masterModel: ConsoleModel.DmgC,
        name: "serial-link-dmg-agb",
        slaveModel: ConsoleModel.Agb
    ),
            new SerialLinkStage(
        masterModel: ConsoleModel.CgbE,
        name: "serial-link-cgb-agb",
        slaveModel: ConsoleModel.Agb
    ),
            // Tier C — the link cable under a longer gapped exchange and a mid-exchange churn: suspend/snapshot/restore/
            // reconnect at a transfer-idle boundary via the credit-preserving resume token, proving the exchange is
            // transparent to a snapshot cycle (self-contained synthetic ROMs; runs anywhere).
            new LinkChurnStage(),
            // Tier C — the cross-machine infrared channel (the Mystery Gift transport): two Cgb machines blink distinct
            // patterns at each other over the CGB RP port and read them back through an IrLinkSession, each side receiving
            // the peer's pattern exactly, replay- and churn-identical via the credit-preserving resume token (self-contained
            // synthetic ROMs; runs anywhere).
            new InfraredExchangeStage(),
            // Tier C — the rule-#3/M5 golden replay of a REAL commercial game across a Cgb↔Agb pair (needs a
            // link-capable cartridge via PUCK_GB_LINKROM; skips cleanly when absent).
            new LinkGameReplayStage(),
            // Tier C — the cross-gen-cart trade-harness foundation: two Cgb machines boot the real trade cartridge with
            // distinct crafted saves, linked through a SerialLinkSession, proving CONTINUE-acceptance onto the Cable Club
            // floor + churn transparency (needs the trade cartridge via PUCK_GB_TRADEROM; skips cleanly when absent).
            new ScriptedTradeContinueStage(),
            // Tier C — the full scripted two-machine cross-gen-cart Cable Club trade: ScriptedTradeDriver's peek-gated phase
            // machine walks both Cgb machines through the rendezvous, block exchange, mon offer/confirm, trade + auto-save,
            // and CANCEL exit; the gate asserts the $01/$02 roles, the committed lead-species swap, replay- and
            // churn-identical traffic/snapshots/SRAMs (needs the trade cartridge via PUCK_GB_TRADEROM; skips cleanly absent).
            new ScriptedTradeLinkLockStage(),
        ];
}
