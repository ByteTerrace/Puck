---
name: verifying-puck-changes
description: How to prove a Puck change actually works — which POST battery tier or gate to run for what you touched. Use whenever you have modified engine code (GPU, shaders, backends, sim, input, documents), emulator code (GamingBricks), or are about to claim a change is done/verified; also when asked to "run the tests" (this repo has no unit tests — the POST batteries are the verification story).
---

# Verifying Puck changes

This skill is **procedural only**: it tells you how to check a change, never
what the code should look like. It does not govern architecture, design, or
style. Precedence: the user's current instruction outranks this skill — if
this skill ever argues against a change the user wants, the skill is stale;
update it in the same change and say so.

## Pick the gate by what you touched

| You touched | Run |
|---|---|
| Sim, fixed-point, commands, input routing, binding pages, run documents | `dotnet run --project src/Puck.Post -c Release -- --tier A` (fast, no GPU) |
| Same-device GPU code, kernels, compositor, capture | Tier A + `--tier B` |
| Shaders, either backend, shared GPU path, sharing/export | full battery (`dotnet run --project src/Puck.Post -c Release`) — Tier C is the cross-backend proof |
| Present/pacing, device-loss, backend switching | full battery (Tier D exercises live subsystems) |
| Suspected backend divergence | the differential fuzzer: Post `--filter fuzz` (or `--stage fuzz --fuzz-seed N` for one seed), or a run document with a `fuzzing` section (`docs/examples/fuzz-sweep.json`) |
| Camera/webcam paths | Post's `camera-share` stage covers the share seam; live-hardware smoke = run `docs/examples/four-quad.json` and watch the camera pane (the old `--validate-camera-*` gates were retired) |
| The overworld demo (room sim, boot flow, layout director) | `Puck.Demo --validate-overworld` (pure-CPU determinism + replay self-check — the one gate that stays demo-resident, since Puck.Post cannot reference the demo); layout-stage captures via `PUCK_OVERWORLD_DEBUG_BOOT` + `PUCK_CAPTURE_FRAME` + `--capture` (see agent-guide env table) |
| Engine determinism/replay, paged binding-profile resolver, cross-backend world parity | Puck.Post Tier A (`--stage binding-page` isolates the resolver) / Tier C — these moved off demo `--validate-*` flags when Puck.Demo was purified down to the overworld prototype |
| GB/GBC emulator | `dotnet run --project experimental/Puck.HumbleGamingBrick.Post -c Release` |
| GBA emulator | `dotnet run --project experimental/Puck.AdvancedGamingBrick.Post -c Release` (deep diagnostics: `--lockstep`, `--trace-cycles`, `--iodump`) |

Exit codes everywhere: 0 = pass, 1 = a check failed, 2 = infrastructure
failure. Reference-ROM stages skip cleanly when the corpus is absent.

## Rules

- **Don't add `--validate-*` flags to Puck.Demo** — add a Post stage instead.
  A new stage may assert only observable outcomes (pixels/hashes, parity,
  determinism, exit codes, budgets), never internal structure: no mocks, no
  call-sequence assertions, nothing that breaks on a pure rename.
- **Report gate output faithfully** — a red stage is a finding, not an
  obstacle; say which stage and why.
- Full detail (tier contents, env vars, asset locations, hardware gotchas):
  [docs/agent-guide.md](../../../docs/agent-guide.md). That doc is the source
  of truth; if this table and the guide disagree, trust the guide and fix
  this table.
