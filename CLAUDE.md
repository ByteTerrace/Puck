# AGENTS.md

Puck is an **everything-as-data** engine: versioned JSON documents describe what
runs, and the engine renders, composites, validates, and replays them
deterministically on either GPU backend — Vulkan or Direct3D 12. The live game
is `Puck.World`, whose document is `puck.world.def.v1`; `puck.run.v1`
(`Puck.Scene`) is the engine-tier run/scene document, gated by Post's
`run-document` stage. Both carry the same `Extensions` round-trip convention.
It is a deliberately dumb terminal *beneath* engines; where it ends up is left
open on purpose.

## Current regime — STABLE

The build, determinism checks, calibrated ceilings, golden replays, and
applicable POST batteries are enforced. Presentation-only float and artistic
work remain outside the simulation-state determinism contract.

## Orientation

These four are kept current — read them before deep work.

| Doc | Answers |
|---|---|
| [docs/capability-catalog.md](docs/capability-catalog.md) | What Puck can do, with per-capability verification status. |
| [docs/project-map.md](docs/project-map.md) | What each `Puck.*` project is for, how they layer, the dependency rules. |
| [docs/agent-guide.md](docs/agent-guide.md) | How to verify, env vars, hardware gotchas, conventions. **Read before touching GPU or emulator code.** |
| [docs/README.md](docs/README.md) | Index of current references, handbooks, and roadmaps. |

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
   doctrine: [agent-guide.md](docs/agent-guide.md#engineering-doctrine).
3. **The game is greenfield; Post gates the engine.** `Puck.World` — the
   overworld and everything under `src/Puck.World/` — is the playground: expected
   to churn, never settled precedent. (`Puck.Demo` is a **library that no longer
   runs**, being retired into `Puck.World` across the
   [Demo → World port](docs/demo-to-world-port-plan.md); its composition root was
   deleted at Beat B.) Verify game/overworld changes by RUNNING `Puck.World`
   (`dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window is closed), never by a gate; never add a `--validate-*` flag or a Post stage for a game
   feature, or promote one into Post, unless explicitly asked. `Puck.Post`
   (`dotnet run --project src/Puck.Post -c Release`) is the verification story
   for the shared *engine* contract only — the cross-backend render path, the
   SDF VM ISA, the run-document schema, the deterministic numerics and backends.
   Emulator changes use the `Puck.HumbleGamingBrick.Post`/`Puck.AdvancedGamingBrick.Post` batteries.
4. **Determinism is a feature — it pins the mapping, not the values.** No
   wall-clock, RNG, or float in simulation state; input becomes per-tick
   `CommandSnapshot`s; fixed-point math comes from `Puck.Maths`. The guarantee
   is reproducibility at a fixed code version: same document + same input →
   bit-identical state on every run, machine, and backend. It is NOT output
   stability across code versions — a deliberate correction to math or logic
   is EXPECTED to change state hashes. When one does: make the correction,
   re-run the relevant Post tier to prove determinism still holds (the gates
   are self-referential; they pin no historical values), and re-record any
   persisted replays or baselines the correction invalidates in the same
   change. Never preserve a wrong result to keep a hash stable, and never add
   a path that reproduces old-wrong behavior.
5. **Supergreen — zero consumers.** Nothing outside this repository consumes
   Puck: no published packages, no downstream repos, no users of its APIs.
   Backwards compatibility is a non-goal — never raise it as a concern, and
   never let it shape a change. Rename, reshape, and delete freely, updating
   every internal caller in the same change. No compat aliases, no
   deprecation ceremonies, no migration shims, no read-side tolerance for
   retired data shapes — migrate data once and delete the old path. The only
   stability contract is observable behavior under the gates.
6. **Merges** land on `main` as one squash commit with a hand-written summary —
   no WIP noise, no merge bubbles, no `Co-Authored-By` trailers.

## The demo is the overworld

> **STATUS (2026-07-19): PORT-REFERENCE — the Demo no longer runs.** `Puck.Demo`
> was flipped to a **library** at Beat B of the
> [Demo → World port](docs/demo-to-world-port-plan.md) (R0/OQ-11): its
> composition root is deleted and the default run is gone. The overworld
> experience described below is being carried into `Puck.World` across the port's
> twelve arcs (seven landed). Read it as the product's intent — verified by
> running `Puck.World`, not as current `Puck.Demo` behavior. The plan of record
> is [docs/demo-to-world-port-plan.md](docs/demo-to-world-port-plan.md); start at
> its **State of execution** block.

`Puck.Demo`'s default run IS the demo: a controller-driven player in a room of
bootable console cabinets — the dmg/cgb/agb costumes of one GamingBrick machine.
Interact (North) inserts and boots a cabinet's selected cart and lights its
diegetic CRT screen; the layout eases fullscreen → side-by-side →
big-top/two-bottom → 2×2 quad as more boot. A Cycle button rotates each
cabinet's cart type (there is no shelf or carrying). Console mode is
multiplayer; `--rom <path>` boots straight into a cartridge; the live
dmg↔cgb↔agb device swap, forged cartridges (SDF art, camera,
hand-authored SM83 games), and creator mode — a rich in-engine editor that
sculpts, animates, and bakes `puck.creation.v1` creations into cartridges — all
live here.

**The unification contract (the demo's north star).** The demo is ONE cohesive
experience, not a menu of `--flag` modes: every capability is reachable from
inside a single running session — a diegetic act, a pad chord, or a **console
verb** — with no restart. The **console is the control plane**, driven by the
on-screen panel AND process **stdin** with results echoed to **stdout**, so an
agent (or a deterministic test) scripts the whole engine over a pipe. Durable
configuration lives in the `puck.run.v1` **data file** (which world the overworld
reveals into and each cabinet's cart). The demo has no `PUCK_*` configuration
surface; durable values are run-document fields and live operations are console
verbs.
The player's journey is a **reveal ladder**: boot immersed in an intro ROM that
mirrors the arcade room the data file defines → win → the fourth wall breaks and
loads you into that world, standing at the machines with their screens glowing →
later, a diegetic reveal unlocks the editor (which stays always-on for
devs/agents). Headless `--forge-*`/`--validate-*`/`--scenario`/`--run` are
CI/proof or developer reflections of in-session capabilities, never separate
products. The current contract, controls, seams, and open product work are in
[docs/overworld-demo-plan.md](docs/overworld-demo-plan.md).

The in-engine game studio roadmap is
[docs/game-studio-plan.md](docs/game-studio-plan.md). Machine-fleet workloads
and performance constraints are described in
[docs/machine-fleet-briefing.md](docs/machine-fleet-briefing.md).

## Controller input

Switch Pro / Xbox Series / DualSense, all flowing through `Puck.Commands`, live
in `src/Puck.Input`. Its [README](src/Puck.Input/README.md) is the handoff doc —
architecture, cross-family feature matrix, hardware-verified status, deferred
work, debugging notes.
