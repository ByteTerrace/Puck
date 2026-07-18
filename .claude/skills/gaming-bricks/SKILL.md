---
name: gaming-bricks
description: Working on the GamingBrick emulators — GB, GBC, and GBA as ONE deterministic machine (src/Puck.HumbleGamingBrick, Puck.AdvancedGamingBrick, and their .Post batteries). Use whenever touching emulator code (CPU, PPU, APU, timers, serial, DMA, cartridges, link), running or adding emulator conformance/accuracy work, using the mGBA/ares co-simulators or reference ROM suites, or wiring an emulator into the engine as a content source. Carries the settled cross-generation contract facts and oracle discipline so they aren't re-derived or accidentally contradicted.
---

# The GamingBricks: one machine, three costumes

This skill is **factual and procedural only**: hardware truth, settled
decisions with provenance, and how to verify. It does not design the code.
The user's current instruction outranks it — if it argues against a requested
change, it is stale; update it in the same change and say so. The
**authoritative plan is [docs/ideal-gaming-brick-plan.md](../../../docs/ideal-gaming-brick-plan.md)**;
this skill summarizes, never supersedes.

## The mission frame (user-settled — do not relitigate)

1. **Deterministic**: emulated state advances only from the integer tick
   clock + inputs/config. No wall-clock, no RNG, no float in emulated logic.
2. **Carry-forward**: a GB game plays bit-perfectly on GBC and GBA through
   **one shared SM83 core** parameterized by a `ConsoleModel` capability gate
   ({Dmg, Cgb, Agb}) — never three emulators. The ARM7TDMI
   (AdvancedGamingBrick) stays a separate GBA-native core, cart-type
   selected, sharing one master clock + link layer.
3. **Cross-generation bit-lock**: two machines of different generations stay
   bit-identical through a link-cable session (golden-replay gate, M5).

**Consequence for THIS skill:** there are deliberately no per-platform
skills. If GB/GBC/GBA knowledge starts diverging in here, that's rule-#2
pressure on the code — surface it, don't fork the skill.

**Why the Bricks exist at all:** they are the engine-hosting thesis's chosen
test tenants (deterministic + self-verifying ⇒ any integration glitch is
attributable to the host). See `docs/capability-catalog.md` §10.

## Contract facts (hardware truth — verified, load-bearing)

- **Tick base**: unified integer 2²⁴/s. DMG/CGB=2²², CGB-double=2²³,
  GBA=2²⁴ — exact powers of two, zero drift. On GBA think **1.0 tick = one
  16.777216 MHz CPU cycle**; a frame = **280,896 cycles** (the 4×
  cycles-vs-dots confusion is a known trap).
- **Serial**: a division of the shared 16-bit DIV counter — the shifter advances
  **one bit on each falling edge of DIV counter bit 8 (normal) / bit 3 (CGB-fast)**,
  edge-detected on the free-running counter with no auxiliary divider state; writing
  SC arms a transfer but does **not** reset the counter (an SC write never re-phases
  the shift clock). This supersedes the earlier "bit-7/bit-2, one shift per two falling
  edges" phrasing (equivalent for natural increments, divergent under DIV-write/SC
  re-phasing); pinned by mooneye `boot_sclk_align`'s spec ("edges align to the reset
  time, not when SC is written") and gated by the Humble `link-churn` Tier C stage.
  The DMG mooneye result-signature reader reads the intended SB write
  (`SerialComponent.ByteQueued`), not the post-arm latch, since DMG's output routine
  re-arms an unfinished normal-clock transfer.
- **Timer**: TIMA increments on the falling edge of (TAC-selected bit ∧
  enable); DIV writes self-tick; 4-T reload-delay precedence applies.
- **Tie-break**: at an equal timestamp, **timer runs before serial** — this
  ordering is load-bearing for cross-gen lock.
- **APU**: the **integer** channel digital outputs (PCM12/34) are the
  contract surface; any float mixing is presentation, never state. The frame
  sequencer is DIV-driven (bit 12 / bit 13 double-speed).
- **Determinism seams today**: BOTH cores have full mid-frame
  `Snapshot()/Restore()/Fork()` through one shared substrate —
  `Puck.Snapshots` (`StateWriter`/`StateReader`, `SnapshotSection`, the
  FNV-1a fingerprint, `SnapshotImage`) behind one `ISnapshotable` component
  contract, implemented by every Humble component and every Advanced
  component alike (identity records and component discovery order stay
  per-core by design). `Fork` restores into a bounded pool of parked
  instances rather than rebuilding a DI container per call (Humble ~62 µs,
  Advanced ~42 µs). Both batteries carry the determinism trio (`determinism`,
  `snapshot-round-trip`/`state-round-trip`, `fork-determinism`); `--hash-divergence`
  (de-forked onto the shared substrate, both Post exes) localizes the
  diverging component+offset on any snapshot mismatch **within one process at
  one code version** — it has no cross-build mode, so cross-build "did this
  change shift any bytes" claims go through `--dump-snapshot <frame>`
  (offline section-table diff) instead. The GBA demo cabinet is
  tick-locked (exact-rational `DeltaTicks`→cycle budget, remainder carried)
  with audio drained as integer `short`s end-to-end; `IAudioMachine` on the
  queued-host contract drains each core's presentation-side ring on both
  hosts (state-of-record audio advance stays unconditional — only the
  presentation-side mix/ring write gates on a sink being attached).
- **GB PPU STAT/lock schedule (settled 2026-07; do not re-derive, do not tweak
  constants in isolation).** Mode 0 is EMERGENT — the internal mode-3→0 edge
  fires on the pop of the 160th pixel (dot 251 + SCX%8 on a clean line; the
  first line after an LCD enable runs with no entry latency, four dots early)
  and drives HDMA on time. Everything CPU-visible TRAILS that edge by tuned
  lags in `PpuTimingParameters`/`Ppu`: polled STAT +4, the mode-0 interrupt
  +5 (−1 on Color at single speed — the Color boot handoff sits on a
  different machine-cycle phase), VRAM READ unlock +4 (coheres with the
  polled STAT flip — hardware never shows STAT mode 0 with a VRAM read
  still blocked; SameBoy's mode-3 exit clears both together, and Pokemon
  Gold's Trade Center poll-STAT-then-read seat-walk is the pinned
  reproducer), VRAM-write and OAM-write unlocks +5, OAM-read unlock +6. The OAM STAT pulse fires one dot after the LY write
  (`OamPulseOffset`=+1) and its tail overlaps the comparison-valid dot, which
  is what keeps a held LY=LYC from re-triggering (`stat_irq_blocking`). OAM
  writes stay open for the first machine cycle of the scan and re-open during
  the mode-3 entry-latency dots; VRAM writes also land during those dots
  (reads don't). Every one of these numbers is pinned by a specific mooneye
  verdict (`hblank_ly_scx_timing`'s 51/50/49 SCX pattern,
  `intr_2_*`, `intr_1_2`, `lcdon_timing`/`lcdon_write_timing` tables) —
  mooneye-ppu is 19/19; moving one constant unbalances several tests at once.
  The object-fetch stall is dot-exact against SameBoy, including
  `intr_2_mode0_timing_sprites`: our per-dot order ticks the fetcher BEFORE
  the pixel pop (the oracle advances it after, and its push state parks until
  the FIFO drains), so from the line's second background tile on the
  check-time fetcher state trails the oracle's by one step — the first-sprite
  wait in `ObjectFetchDot` therefore accepts the high data byte's ADDRESS dot
  as ready once the first push has landed (`!m_firstFetchOfLine`); the line's
  first fetch carries no skew and keeps the read-dot threshold. Diagnose with
  the Post's `--stat-trace` (instruction-level STAT/LY/IF timeline) plus the
  mooneye failure screen via `--render`. **The remaining frontier**: the
  mealybug `m3_*` sub-dot register signatures.

## Oracle discipline

**Reference suites and co-simulators are EVIDENCE, never gates.** Gates are
our own POST batteries and golden replays. Known oracle facts:

- ares itself is ~1–2 cycles off hardware on IRQ-recognition depth — the
  reason ARES-cloning was abandoned as a directive.
- **mGBA co-sim quirks**: cumulative cycle counters rebase to 0 every frame
  (compare per-instruction *deltas*); our PC reads **+4** vs mGBA's
  (pipeline representation) — subtract before aligning.
- The AGS aging cartridge must be the **TCHK10** dump (other revisions don't
  match the output-patch offsets). One AGS cell needs a link partner — a
  measurement, not a failure.
- Reference checkouts, corpora, BIOS, and ROMs live under
  `D:\Source\ByteTerrace\Temp\` (`GBA_bios.rom`, `gba-tests`, `mgba-suite`,
  cosim exes, `GBC Test Suites`, …).
- **SingleStepTests/sm83** (`D:\Source\ByteTerrace\Temp\sm83-sst`, env
  `PUCK_GB_SST`) is **ares-derived** and is the outlier on two opcodes:
  `STOP` (the corpus models a 1-byte instruction; SameBoy and mooneye's
  pinned `ei_sequence`/STOP behavior back Puck's 2-byte read) and `EI`
  (the corpus expects ares' re-arm semantics; mooneye's `ei_sequence` pins
  Puck's no-op instead). Both are documented oracle-conflict skips in
  `Sm83SstStage`, not bugs — SameBoy and mooneye pin our behavior over the
  ares-derived corpus. 498,000/498,000 vectors pass across 498 of 500
  opcode families.
- **BESS export/import** round-trips machine state with SameBoy/mGBA for
  cross-emulator state evidence (diagnostic tooling, never a gate).

## Verifying

Batteries (full routing lives in the `verifying-puck-changes` skill):

```
dotnet run --project src/Puck.HumbleGamingBrick.Post   -c Release
dotnet run --project src/Puck.AdvancedGamingBrick.Post -c Release
```

Current water-line: Humble 37/37 stages pass; Advanced 20 pass + 2 asset-gated
skips (`link-game-replay`, `solar-replay`). Beyond the trio, both batteries
also carry a `QueuedHostBackpressureStage`/`QueuedHostFramePublicationStage`/
`QueuedHostAudioStage` trio (the shared queued-host substrate's observable
contract) and an `AllocationStage` (zero-alloc-per-frame gate). Humble adds
`RumbleDeviceStage`, `PrinterStage`, `Sm83SstStage`, `LinkChurnStage`,
`InfraredExchangeStage`; Advanced adds `SolarDeviceStage`, `LinkChurnStage`.
Tier A needs no assets; Tier B skips cleanly without the corpus (env:
`PUCK_GB_TESTROMS`/`PUCK_GB_SST`; `PUCK_AGB_BIOS`/`_TESTROMS`/`_ACCURACY_SUITE`/`_AGS`/`_GAMES`);
Tier C is the cross-machine link battery — the Humble `serial-link*` stages
(self-contained) prove link-cable byte exchanges through `SerialLinkSession`
for all three generation pairings (dmg↔cgb, dmg↔agb, cgb↔agb — the Agb
costume linking through the same machinery), each replay-identical across
runs; the `link-churn` stage proves the link is transparent to a mid-exchange
snapshot cycle (suspend/snapshot/restore/reconnect at a transfer-idle boundary
via `SerialLinkSession.Suspend`'s credit-preserving resume token, replay- and
churn-identical, with the no-resync connect phase recorded and pinned); and the
full rule-#3 cross-generation bit-lock (golden replay on a real
link game) LANDED as the Humble `link-game-replay` stage — Mario Tennis walked
from power-on to its two-player handshake over a Cgb↔Agb `SerialLinkSession`,
real bidirectional serial traffic, replay-identical from fresh machines (ROM
via `PUCK_GB_LINKROM`, skips when absent). The commercial-TRADE pair goes
further: `trade-continue` proves two crafted Pokémon Gold battery saves
(`TradeSaveFactory` — byte-exact; CONTINUE restores rather than
regenerates the overworld objects AND the `wObjectFollow_*` globals, which a
real save holds as $FF/$FF — zeros arm a phantom follower whose 5-byte queue
overflows into `wPlayerStruct`) are CONTINUE-accepted, and `link-lock` drives
the complete two-machine Cable Club trade (DIV-offset rendezvous
symmetry-break, TRADE_CENTER warp, seat walk, mon-selection menus, a
committed auto-saved species swap, the CANCEL exit) replay- and
churn-identical (ROM via `PUCK_GB_TRADEROM`, skips when absent). The GBA
side's `link-replay` stage proves the ARM7TDMI multiplayer cable the same
way, its own `link-churn` stage proves the cable transparent to a
suspend/snapshot/restore/reconnect cycle via `AgbLinkSession.Suspend`'s
credit-preserving `AgbLinkResumeToken`, and its `link-game-replay`
stage boots a real commercial link game on two consoles over the cable
(replay-identical). The Advanced battery's `solar-replay` stage replays a
recorded varying-light script byte-identically on a real Boktai cart (env
`PUCK_AGB_SOLARROM`, skips cleanly when absent, same pattern as
`PUCK_GB_TRADEROM`). **Settled frontier fact:** that game's multiplayer *lobby*
does not yet complete — a real link game detects a partner by polling the
Multiplayer `SIOCNT` **SD/SI ready-line** (derived from cable-partner presence),
and `AgbSerialController.PackSioControl` doesn't pack those bits yet, so cable
traffic flows but no multiplayer round completes. That is the well-specified next
core item — do not mistake it for a link-session bug. The GBA Post exe doubles as the
diagnostics toolbox (`--lockstep`, `--trace-cycles`, `--statetrace`,
`--iodump`, `--render-hash`, `--gen-rom`, …) — see its README.

## Not governed here

Engine/GPU work (agent-guide + `verifying-puck-changes`), the viewport
bridge design when it lands (that's engine contract territory), and any
decision the plan doc hasn't settled — for those, the plan doc and the user
decide, not this file.
