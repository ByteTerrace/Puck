# CLAUDE.md

Puck is an **everything-as-data** engine: one versioned JSON document
(`puck.run.v1`) describes a run, and the engine renders, composites, validates,
and replays it deterministically on either GPU backend — Vulkan or Direct3D 12.
It is a deliberately dumb terminal *beneath* engines; where it ends up is left
open on purpose.

## Orientation

These four are kept current — read them before deep work.

| Doc | Answers |
|---|---|
| [docs/capability-catalog.md](docs/capability-catalog.md) | What Puck can do, with per-capability verification status. |
| [docs/project-map.md](docs/project-map.md) | What each `Puck.*` project is for, how they layer, the dependency rules. |
| [docs/agent-guide.md](docs/agent-guide.md) | How to verify, env vars, hardware gotchas, conventions. **Read before touching GPU or emulator code.** |
| [docs/README.md](docs/README.md) | Docs index — living vs. historical. |

For an area's settled contract facts, load its skill: `sdf-world`,
`run-document`, `gaming-bricks`, `rom-forge`, `verifying-puck-changes`,
`roslyn-first-analysis`.

## Core rules

1. **Split `Puck.*` projects only.** Every feature lives in the split projects;
   `src/Puck` and `src/Puck.Avatars` exist only in git history. Never reference
   those paths.
2. **The current instruction outranks every artifact.** Docs, skills, gates,
   comments, and precedent are evidence, not law — if one argues against a
   change you've been asked to make, it is stale; update it in the same change
   rather than watering the change down. Gates prove *observable* behavior
   (pixels, hashes, parity, determinism), never internal structure. Full
   doctrine: [agent-guide.md](docs/agent-guide.md#anti-calcification-doctrine).
3. **The Demo is greenfield; Post gates the engine.** `Puck.Demo` — the
   overworld and everything under `src/Puck.Demo/` — is the playground: expected
   to churn, never settled precedent. Verify demo changes by RUNNING the demo
   (`dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window is closed), never by a gate; never add a `--validate-*` flag or a Post stage for a demo
   feature, or promote one into Post, unless explicitly asked. `Puck.Post`
   (`dotnet run --project src/Puck.Post -c Release`) is the verification story
   for the shared *engine* contract only — the cross-backend render path, the
   SDF VM ISA, the run-document schema, the deterministic numerics and backends.
   Emulator changes use the mirrored `experimental/*.Post` batteries.
4. **Determinism is a feature.** No wall-clock, RNG, or float in simulation
   state; input becomes per-tick `CommandSnapshot`s; fixed-point math comes from
   `Puck.Maths`.
5. **Merges** land on `main` as one squash commit with a hand-written summary —
   no WIP noise, no merge bubbles, no `Co-Authored-By` trailers.

## The demo is the overworld

`Puck.Demo`'s default run IS the demo: a controller-driven player in a room of
bootable console cabinets — the dmg/cgb/agb costumes of one GamingBrick machine.
Interact (North) inserts and boots a cabinet's selected cart and lights its
diegetic CRT screen; the layout eases fullscreen → side-by-side →
big-top/two-bottom → 2×2 quad as more boot. A Cycle button rotates each
cabinet's cart type (there is no shelf or carrying). Console mode is
multiplayer; `--rom <path>` boots straight into a cartridge; the live
dmg↔cgb↔agb device swap, forged cartridges (SDF art, Pocket Camera,
hand-authored SM83 games), and creator mode — a rich in-engine editor that
sculpts, animates, and bakes `puck.creation.v1` creations into cartridges — all
live here. Full spec, seams, and next steps:
[docs/overworld-demo-plan.md](docs/overworld-demo-plan.md). The in-engine game
studio's road ahead (the card games, audio, the UI convergence, the recursion)
is [docs/game-studio-plan.md](docs/game-studio-plan.md). The many-machines
performance arc starts from
[docs/machine-fleet-briefing.md](docs/machine-fleet-briefing.md).

## Controller input

Switch Pro / Xbox Series / DualSense, all flowing through `Puck.Commands`, live
in `src/Puck.Input`. Its [README](src/Puck.Input/README.md) is the handoff doc —
architecture, cross-family feature matrix, hardware-verified status, deferred
work, debugging notes.
