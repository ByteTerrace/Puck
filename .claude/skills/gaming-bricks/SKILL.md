---
name: gaming-bricks
description: Maps and guards Puck's deterministic Game Boy, Game Boy Color and Game Boy Advance emulators (`src/Puck.HumbleGamingBrick`, `src/Puck.AdvancedGamingBrick`, `src/Puck.Machines`) and the Humble Gaming Deck NES core. Load it before fixing or diagnosing emulator hardware behavior — SM83 opcodes (`Sm83.Decode.cs`: STOP, HALT, EI), timer and serial edges, PPU mode, STAT and interrupt dot timing (`Ppu.cs`), APU channels, DMA and HDMA, mappers, the GBA ARM7TDMI, SIOCNT and link cables (`AgbSerialController`) — and before touching snapshot layout, replay and fork determinism, firmware and boot mode, or a Post battery (`Puck.HumbleGamingBrick.Post`, `Puck.AdvancedGamingBrick.Post`: `--filter`, `--tier`, `--cosim`, `--stat-trace`, `--render`, the `Expectations.json` ledger, `--accept`). Also load it when a mooneye, blargg, SameSuite, gambatte, mealybug, SingleStepTests or SameBoy result disagrees with the core, a timing constant or lag value looks wrong, or the question is where an emulator fix belongs and how to verify it. It excludes authoring cartridge documents and the native CGB/AGB compilers (rom-forge), and general engine, renderer and world design (rendering, puck-world).
---

# Gaming Bricks

Treat the GamingBricks as one deterministic machine family: a shared SM83
compatibility core covering every DMG/MGB/SGB/CGB/AGB hardware revision
(`src/Puck.HumbleGamingBrick`), plus the separate ARM7TDMI-based GBA-native core
(`src/Puck.AdvancedGamingBrick`). `src/Puck.Machines` holds what both share:
snapshots, the instance/fork/pool lifecycle, queued hosts, link groups. The Humble
Gaming Deck adds the NTSC NES/Famicom console on the same substrate; its master
tick is distinct from either handheld clock. Read its
[manual](../../../docs/emulation/hgd/README.md) and run
`Puck.HumbleGamingDeck.Post` for changes to that core. The current implementation
and its Post stages are authoritative; update stale skill guidance in the same
change.

## Core invariants

- Advance emulated state only from integer clocks and explicit inputs.
- Keep configuration in constructor parameters or CLI options, never environment
  variables. AGB diagnostic hardware/trace overrides live in `AgbMachineOptions`;
  preserve them across forks and include behavioral options in typed snapshot identity.
- Preserve direct embedding through the public synchronous `AdvancedGamingBrickCore`
  and `HumbleGamingBrickCore` APIs and default factory composition. The shared
  [Machine hosting runtime](../../../docs/emulation/shared/machine-hosting.md) owns
  the host contract; both Post `embedding` stages
  exercise it without a worker, renderer or audio device.
- Keep firmware identity separate from startup mode. `MachineBootMode.Fast` skips startup without discarding the
  selected image; AGB still executes runtime BIOS services from that image. Explicit low-level diagnostic inputs
  remain supported. Bundled images are generated ahead of packaging, never by a runtime-to-Forge dependency.
- Keep floating point and host timing beyond a one-way presentation seam.
- Express console differences through capability gates (a named question on
  `ConsoleModelExtensions`), not forked SM83 cores and not a revision switch inside
  `Sm83`, `Ppu` or `Apu`. A fix that needs no model question needs no gate.
- Preserve snapshot, replay, fork, and cross-machine link determinism.
- Keep every idle fast path exact: an absorbed quiet stretch, a display ticked
  alone, or a halted span leaves the state the reference stepping would.
  `ComponentClock`'s `absorbsQuietStretches: false` and single `Step` calls are
  the references; `ColourIdleEquivalenceLawTests` and
  `AdvancedIdleEquivalenceLawTests` compare them over the shipped cartridges.
  A ticked cycle withdraws both cached clock agreements, including the
  others-only bound: rejoining the display to a stale bound can skip a timer
  edge.
- External suites, co-simulators, Post stages and golden replays are evidence
  about the hardware model. A stage or replay that a better-sourced correction
  moves is updated with the correction.

## Contracts that bite

Each is detailed in [references/hardware-and-oracles.md](references/hardware-and-oracles.md).

- **STOP and EI are oracle conflicts, not bugs.** `ExecuteStop` (`Sm83.Decode.cs`)
  consumes STOP's pad byte (PC+2) only when no interrupt is pending (`IE & IF`,
  independent of IME) and leaves it when one is latched. The SingleStepTests/sm83
  corpus models STOP as one byte and re-arms EI when IME is already set, so
  families `10` and `fb` are documented oracle-conflict skips in the `sst-sm83`
  stage, never silent exclusions. The Tier-A `sm83-stop-pending-interrupt` stage
  covers the pending edge; the acceptance `ei_sequence` ROM pins EI.
- **The GB PPU schedule is one coupled contract.** Mode transition, polled STAT,
  VRAM and OAM locks, the mode-0 interrupt, the LY-comparison pulse and the
  fetcher order move together. Never tune one lag constant alone, and never move
  the PPU onto SameBoy's dots: the decompositions are measured and refuted
  ([Refuted PPU schedule decompositions](../../../docs/emulation/hgb/post-and-conformance.md#refuted-ppu-schedule-decompositions)),
  and the screenshot corpus is the gate. Acceptance cases that pin it by name:
  `hblank_ly_scx_timing`, the `intr_2_*` family, `intr_1_2`, `stat_irq_blocking`,
  `lcdon_timing`, `lcdon_write_timing`.
- **Timer before serial at equal timestamps**, and serial edges follow the free-running
  DIV counter, never the moment SC is written (`link-churn` and `boot_sclk_align`).
- **A `--cosim` run under about 60 frames proves nothing** (the boot logo scroll
  dominates it). Give a CPU-stream comparison at least 120 frames and check that the
  record count differs per ROM before believing a clean result.
- **Snapshot layout is pinned.** Every component lists its fields once in a
  `TransferState` (a Humble mapper's `TransferRegisters`). A deliberate layout
  change re-records that component in `ComponentSnapshotLayoutLawTests` and raises
  `CurrentVersion` of `MachineIdentity` (Humble) or `AgbMachineIdentity` (Advanced).
- **GBA multiplayer SIOCNT has an unmodeled ready line.** `AgbSerialController.PackSioControl`
  does not derive the SD/SI bits from cable-partner presence, and
  `LinkGameReplayStage` records how far a commercial link game gets without it.
  Implementing it flips that stage; update the stage and the reference together,
  and derive the bits from the modeled `AgbLinkCable` partner, never from ambient
  state.
- **No licensed image is ever a default or a bundled payload.** Retail ROMs and BIOS
  images are named per run (`--link-rom`, `--bios`, `--ags`) and stay out of the
  repository.

## Workflow

1. Identify the affected machine and capability: shared SM83 compatibility in
   `Puck.HumbleGamingBrick`, or native ARM7TDMI GBA in
   `Puck.AdvancedGamingBrick`.
2. Read the relevant hardware contract and inspect the current implementation,
   stage registry (`PostStages.cs` in each Post project), and Post README. Do not
   infer timing from names or old stage counts.
3. Make the smallest hardware-model change that explains the evidence. Keep
   clock conversions exact, event ordering explicit, and snapshots complete.
4. Show the change working by running the Post stages it reaches in Release
   (below), or by booting a cartridge that exercises it. Diagnostic flags
   (`--stat-trace`, `--render`, `--cosim`) explain a run; they are not a verdict.
   Add a self-checking stage only where running cannot show the behavior.
   A recorded outcome that moves is accepted from the run's own candidate ledger
   with `--accept`, never re-measured.
5. Report the exact commands, selected stages and asset-gated skips.

## Run it

The batteries are CPU-only executables; none opens the GPU.

```powershell
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --fetch-corpora
dotnet run --project src/Puck.HumbleGamingBrick.Post -c Release -- --lane gate --filter <stage-text>
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release -- --tier A
```

`--fetch-corpora` fills the pinned corpus cache once. Iterate with `--filter` or
`--tier`; Tier A needs no assets, Tier B runs over the fetched corpora, Tier C
covers serial, link and infrared exchange. A stage whose asset is absent skips
(`--require-assets` turns that into an infrastructure failure), and the report
lists every skip. Exit 0 is pass or skip, 1 a failed check, 2 infrastructure or a
refused accept. `tests/Puck.HumbleGamingBrick.Tests` runs the Humble gate lane
with an empty corpus cache, so CPU suites cover the self-contained stages.

## Load references selectively

- Read [references/hardware-and-oracles.md](references/hardware-and-oracles.md)
  for clock and event ordering, serial and timer edges, snapshots, GB PPU
  timing, oracle conflicts, and the documented GBA ready-line gap.
- Read the
  [`Puck.Machines.Post` README](../../../src/Puck.Machines.Post/README.md)
  first when a change touches stage or verdict semantics rather than one
  brick's own context — the shared battery scaffold both Post projects close
  over: `PostBattery<TContext>`, `IPostStage<TContext>`, `PostVerdict`,
  `PostTier`, `HashDivergenceReport`, `CorpusManifest`, `CoreEmbeddingProbe`.
- Read the
  [Humble Post README](../../../src/Puck.HumbleGamingBrick.Post/README.md) for
  tiers, lanes, the corpus ledger and accepting, BESS, `--dump-snapshot`,
  `--hash-divergence` and the co-simulation workflow.
- Read the
  [Advanced Post README](../../../src/Puck.AdvancedGamingBrick.Post/README.md)
  for current tiers, assets, ARM/GBA diagnostics, co-simulation normalization,
  and the accuracy workflow.
- For a rendered accuracy check rather than a Post stage, write a framebuffer with
  Humble Post `--render <rom> <out.png> [frames] [dmg|cgb|agb]` and compare it
  by eye or against a reference capture. The authored comparison worlds
  (`hgb-compare.puck`, `hgb-mirror.puck`) are quarantined under `experimental/`
  and are not run. No test, tool, or asset names a licensed image, not even as a
  default path.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | Selecting or changing deterministic numeric primitives shared with emulator state. |
| [`maths-laws`](../maths-laws/SKILL.md) | Authoring law-suite coverage for a changed public `Puck.Maths` member. |
| [`content-search`](../content-search/SKILL.md) | Finding stage names, registers, opcodes, ROM identifiers, or timing constants textually. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Resolving semantic C# references, implementations, or rename and deletion safety. |
| [`puck-world`](../puck-world/SKILL.md) | Integrating an emulator into world/session behavior beyond the emulator-host seam. |
| [`rom-forge`](../rom-forge/SKILL.md) | Authoring or compiling the cartridge under test — `puck.cartridge.v1`, the `.puck` DSL, or the native CGB/AGB compilers, rather than the emulator core executing it. |
| [`verification`](../verification/SKILL.md) | The landing bar, building before testing, and reading a flaky versus failing leg. |
