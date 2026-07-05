---
name: gaming-bricks
description: Working on the GamingBrick emulators — GB, GBC, and GBA as ONE deterministic machine (experimental/Puck.HumbleGamingBrick, Puck.AdvancedGamingBrick, and their .Post batteries). Use whenever touching emulator code (CPU, PPU, APU, timers, serial, DMA, cartridges, link), running or adding emulator conformance/accuracy work, using the mGBA/ares co-simulators or reference ROM suites, or wiring an emulator into the engine as a content source. Carries the settled cross-generation contract facts and oracle discipline so they aren't re-derived or accidentally contradicted.
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
attributable to the host). See `docs/capability-catalog.md` §9.

## Contract facts (hardware truth — verified, load-bearing)

- **Tick base**: unified integer 2²⁴/s. DMG/CGB=2²², CGB-double=2²³,
  GBA=2²⁴ — exact powers of two, zero drift. On GBA think **1.0 tick = one
  16.777216 MHz CPU cycle**; a frame = **280,896 cycles** (the 4×
  cycles-vs-dots confusion is a known trap).
- **Serial**: a division of the shared 16-bit DIV counter — tap **bit-7
  (normal) / bit-2 (CGB-fast), one shift per TWO falling edges**; writing SC
  arms a transfer but does **not** reset the counter.
- **Timer**: TIMA increments on the falling edge of (TAC-selected bit ∧
  enable); DIV writes self-tick; 4-T reload-delay precedence applies.
- **Tie-break**: at an equal timestamp, **timer runs before serial** — this
  ordering is load-bearing for cross-gen lock.
- **APU**: the **integer** channel digital outputs (PCM12/34) are the
  contract surface; any float mixing is presentation, never state. The frame
  sequencer is DIV-driven (bit 12 / bit 13 double-speed).
- **Determinism seams today**: Humble has full mid-frame
  `Snapshot()/Restore()/Fork()` (`ISnapshotable` on every component). The
  GBA core has **no snapshot layer yet** — its determinism gate compares two
  independently built machines (registers + framebuffer); adding save-state
  reinstates snapshot/fork gates.

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

## Verifying

Batteries (full routing lives in the `verifying-puck-changes` skill):

```
dotnet run --project experimental/Puck.HumbleGamingBrick.Post   -c Release
dotnet run --project experimental/Puck.AdvancedGamingBrick.Post -c Release
```

Tier A needs no assets; Tier B skips cleanly without the corpus (env:
`PUCK_GB_TESTROMS`; `PUCK_GBA_BIOS`/`_TESTROMS`/`_MGBA_SUITE`/`_AGS`/`_GAMES`);
Tier C is reserved for cross-machine link determinism — filling it is the
rule-#3 milestone, not an emulator nicety. The GBA Post exe doubles as the
diagnostics toolbox (`--lockstep`, `--trace-cycles`, `--statetrace`,
`--iodump`, `--render-hash`, `--gen-rom`, …) — see its README.

## Not governed here

Engine/GPU work (agent-guide + `verifying-puck-changes`), the viewport
bridge design when it lands (that's engine contract territory), and any
decision the plan doc hasn't settled — for those, the plan doc and the user
decide, not this file.
