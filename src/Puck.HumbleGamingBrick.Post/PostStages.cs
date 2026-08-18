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
            new TrioLockstepStage(),
            new CameraCaptureStage(),
            new QueuedHostBackpressureStage(),
            new QueuedHostFramePublicationStage(),
            new QueuedHostAudioStage(),
            new QueuedHostMemoryAccessStage(),
            new QueuedHostTimeTravelStage(),
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
            // Tier B — reference-ROM correctness (conformance ROMs via the $A000 result block; skip when the corpus is absent).
            new ConformanceRomStage(
        group: "cpu-instrs",
        model: ConsoleModel.Dmg,
        subPath: "cpu_instrs/individual"
    ),
            new ConformanceRomStage(
        group: "instr-timing",
        model: ConsoleModel.Dmg,
        subPath: "instr_timing"
    ),
            new ConformanceRomStage(
        group: "mem-timing",
        model: ConsoleModel.Dmg,
        subPath: "mem_timing/individual"
    ),
            new ConformanceRomStage(
        group: "dmg-sound",
        model: ConsoleModel.Dmg,
        subPath: "dmg_sound/rom_singles"
    ),
            new ConformanceRomStage(
        group: "cgb-sound",
        model: ConsoleModel.Cgb,
        subPath: "cgb_sound/rom_singles"
    ),
            // Tier B — SingleStepTests/sm83 per-instruction vectors: the shared SM83 core against 500 opcode families
            // on a flat-RAM harness, off-ROM (skip when PUCK_GB_SST is absent).
            new Sm83SstStage(),
            // Tier B — acceptance timing suite (serial Fibonacci signature; skip when the corpus is absent).
            new AcceptanceRomStage(
        group: "timer",
        recurse: true,
        relativeDirectory: "timer"
    ),
            new AcceptanceRomStage(
        group: "ppu",
        recurse: true,
        relativeDirectory: "ppu"
    ),
            new AcceptanceRomStage(
        group: "interrupts",
        recurse: true,
        relativeDirectory: "interrupts"
    ),
            new AcceptanceRomStage(
        group: "serial",
        recurse: true,
        relativeDirectory: "serial"
    ),
            new AcceptanceRomStage(
        group: "oam-dma",
        recurse: true,
        relativeDirectory: "oam_dma"
    ),
            new AcceptanceRomStage(
        group: "bits",
        recurse: true,
        relativeDirectory: "bits"
    ),
            new AcceptanceRomStage(
        group: "instr",
        recurse: true,
        relativeDirectory: "instr"
    ),
            new AcceptanceRomStage(
        group: "misc",
        recurse: false,
        relativeDirectory: ""
    ),
            // Tier C — cross-machine link determinism, one stage per generation pairing (self-contained synthetic
            // ROMs; run anywhere). Dmg↔Cgb is the original pairing; Dmg↔Agb and Cgb↔Agb prove the carry-forward
            // rule's Agb costume links through the identical SerialLinkSession machinery.
            new SerialLinkStage(
        masterModel: ConsoleModel.Dmg,
        name: "serial-link",
        slaveModel: ConsoleModel.Cgb
    ),
            new SerialLinkStage(
        masterModel: ConsoleModel.Dmg,
        name: "serial-link-dmg-agb",
        slaveModel: ConsoleModel.Agb
    ),
            new SerialLinkStage(
        masterModel: ConsoleModel.Cgb,
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
