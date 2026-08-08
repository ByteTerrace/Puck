---
name: gaming-bricks
description: Guides changes to Puck's deterministic GB, GBC, and GBA GamingBrick emulators and their Post batteries. Use for CPU, PPU, APU, timer, serial, DMA, cartridge, peripheral, link-cable, snapshot, replay, emulator-host integration, conformance ROM, co-simulation, or accuracy work under Puck.HumbleGamingBrick or Puck.AdvancedGamingBrick. Preserves the shared SM83 architecture, integer timing, cross-generation bit lock, oracle discipline, and stage routing. Excludes general engine, renderer, and world design.
---

# Gaming Bricks

Treat the GamingBricks as one deterministic machine family: a shared SM83
compatibility core for DMG, CGB, and AGB costumes, plus the separate
ARM7TDMI-based GBA-native core. The current implementation and its Post stages
are authoritative; update stale skill guidance in the same change.

## Core invariants

- Advance emulated state only from integer clocks and explicit inputs.
- Keep floating point and host timing beyond a one-way presentation seam.
- Express console differences through capability gates, not forked SM83 cores.
- Preserve snapshot, replay, fork, and cross-machine link determinism.
- Treat external suites and co-simulators as evidence; repository Post stages
  and golden replays are gates.

## Workflow

1. Identify the affected machine and capability: shared SM83 compatibility in
   `Puck.HumbleGamingBrick`, or native ARM7TDMI GBA in
   `Puck.AdvancedGamingBrick`.
2. Read the relevant hardware contract and inspect the current implementation,
   stage registry, and Post README. Do not infer timing from names or old stage
   counts.
3. Make the smallest hardware-model change that explains the evidence. Keep
   clock conversions exact, event ordering explicit, and snapshots complete.
4. Add or update a self-checking stage when a durable contract changes. A
   diagnostic trace alone is not a gate.
5. Iterate with `--filter` or `--tier`, then run the unfiltered affected Post
   battery in Release. Run both batteries when shared hosting, snapshots,
   clocks, or link behavior crosses the two machines; run Tier C for serial,
   SIO, infrared, or link changes.
6. Report exact commands, selected stages, asset-gated skips, and whether a
   failure is caused by the change or was already present.

## Load references selectively

- Read [references/hardware-and-oracles.md](references/hardware-and-oracles.md)
  for clock and event ordering, serial and timer edges, snapshots, GB PPU
  timing, oracle conflicts, and the documented GBA ready-line gap.
- Read the
  [Humble Post README](../../../src/Puck.HumbleGamingBrick.Post/README.md) for
  current tiers, assets, SM83 corpus behavior, BESS, and GB diagnostics.
- Read the
  [Advanced Post README](../../../src/Puck.AdvancedGamingBrick.Post/README.md)
  for current tiers, assets, ARM/GBA diagnostics, co-simulation normalization,
  and the accuracy workflow.

## Route adjacent work

| Skill | Use when |
|---|---|
| [`maths-usage`](../maths-usage/SKILL.md) | Selecting or changing deterministic numeric primitives shared with emulator state. |
| [`maths-laws`](../maths-laws/SKILL.md) | Authoring law-suite coverage for a changed public `Puck.Maths` member. |
| [`content-search`](../content-search/SKILL.md) | Finding stage names, registers, opcodes, ROM identifiers, or timing constants textually. |
| [`symbol-analysis`](../symbol-analysis/SKILL.md) | Resolving semantic C# references, implementations, or rename and deletion safety. |
| [`puck-world`](../puck-world/SKILL.md) | Integrating an emulator into world/session behavior beyond the emulator-host seam. |
