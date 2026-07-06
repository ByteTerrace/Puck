---
name: verifying-puck-changes
description: How to prove a Puck ENGINE change actually works — which POST battery tier or gate to run for what you touched. Use whenever you have modified engine code (GPU, shaders, backends, sim, input, documents) or emulator code (GamingBricks), or are about to claim a change is done/verified; also when asked to "run the tests" (this repo has no unit tests — the POST batteries are the verification story). NOTE: Puck.Demo is GREENFIELD — verify demo-only changes by RUNNING the demo, never by gating them or adding Post stages.
---

# Verifying Puck changes

Procedural only — how to check a change, never what the code should look like.
The user's current instruction outranks this skill; if it ever argues against a
change they want, it is stale — fix it in the same change and say so.

**The Demo is greenfield.** A change under `src/Puck.Demo/` (overworld,
cabinets, forge ROMs, creator mode, presentation) is verified by RUNNING it —
`dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2` (the
headless smoke; `0` or less DISABLES auto-exit and runs until closed) — not by
a gate. Never add a `--validate-*` flag or a Post stage for a demo feature,
and never promote one into Post, unless the user explicitly asks.

The demo-side canon beyond the smoke:

- **Document sanity**: `-- --validate-overworld`.
- **Deterministic screenshot**: `PUCK_OVERWORLD_CAPTURE_FRAME=N` with
  `-- --capture <png>` grabs the frame after N produced frames (the machines
  have booted and drawn by then). For creator-mode scenes add
  `PUCK_CREATOR_LOAD=<name-or-path>` (loads a saved creation AND enters
  creator mode) or `PUCK_OVERWORLD_CREATOR=1` (empty creator scene).
- **Forge self-verify**: every forge tool boots its output on a real Humble
  machine and asserts observable behavior before writing bytes — run the ones
  your change touches: `--forge-brickfall` (the full BrickfallVerify battery),
  `--forge-avatar` (also writes `<out>.bake.bin`), `--forge-bake` (asserts
  byte-identical output across runs), `--forge-bake-stress`,
  `--forge-bake-calibration <dir>` (a report, never a failing gate),
  `--forge-volley`, `--forge-camera`.

Everything below is for **engine and emulator** work.

## Pick the gate by what you touched

| You touched | Run |
|---|---|
| Sim, fixed-point, commands, input routing, binding pages, run documents | `Puck.Post -- --tier A` (fast, no GPU) |
| Same-device GPU code, kernels, compositor, capture | `--tier A` + `--tier B` |
| Shaders, either backend, shared GPU path, sharing/export | full battery — Tier C is the cross-backend proof |
| Present/pacing, device-loss, backend switching | full battery (Tier D exercises live subsystems) |
| Suspected backend divergence | the differential fuzzer: `--filter fuzz` (or `--stage fuzz --fuzz-seed N`), or a document with a `fuzzing` section |
| Camera/webcam paths | the `camera-share` stage covers the seam; live smoke = cycle an overworld cabinet to the camera cart type and watch the pane |
| Run-document model / schema / a new field | `--stage run-document` (parses + round-trips every example, checks the committed schema) |
| GB/GBC emulator | `dotnet run --project experimental/Puck.HumbleGamingBrick.Post -c Release` |
| GBA emulator | `dotnet run --project experimental/Puck.AdvancedGamingBrick.Post -c Release` (diagnostics: `--lockstep`, `--trace-cycles`, `--iodump`) |

Full battery = `dotnet run --project src/Puck.Post -c Release`. Exit codes
everywhere: 0 pass, 1 a check failed, 2 infrastructure failure. Reference-ROM
stages skip cleanly when the corpus is absent.

## Rules

- **A new engine stage asserts observable outcomes only** — pixels/hashes,
  parity, determinism, exit codes, budgets — never internal structure: no mocks,
  no call-sequence assertions, nothing that breaks on a pure rename.
- **Report gate output faithfully** — a red stage is a finding, not an obstacle;
  name the stage and why.
- Detail (tier contents, env vars, asset locations, hardware gotchas) lives in
  [docs/agent-guide.md](../../../docs/agent-guide.md), the source of truth. If
  this table and the guide disagree, trust the guide and fix this table.
