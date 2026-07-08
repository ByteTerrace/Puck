# Puck documentation index

Start here. Every doc in this folder is classified as **living** (kept
current; trust it) or **historical** (a completed design/investigation record;
accurate when written, may reference deleted paths).

## Start with these (living)

| Doc | What it answers |
|---|---|
| [capability-catalog.md](capability-catalog.md) | *What can Puck do?* — the master inventory of every capability with verification status and how to invoke it. |
| [project-map.md](project-map.md) | *Where does it live?* — all `src/Puck.*` + `experimental/` projects, layering, dependency rules. |
| [agent-guide.md](agent-guide.md) | *How do I work here?* — verification (POST batteries, fuzzing), env vars, hardware gotchas, conventions. |

## Living references

| Doc | Scope |
|---|---|
| [feature-parity-summary.md](feature-parity-summary.md) | Vulkan ↔ Direct3D 12 parity verdict at a glance. |
| [feature-parity-table.md](feature-parity-table.md) | Per-row parity detail with hardware-verification provenance. |
| [platform-display-kinds.md](platform-display-kinds.md) | Surfaces vs. windows and the `NativeDisplayKind` dispatch. |
| [examples/](examples/) | Fourteen valid `puck.run.v1` run documents plus two checked-in negatives (`world-single-bad-material.json`, `overworld-victory-bad-meta.json`), all exercised by the Post `run-document` stage — which is the source of truth for the counts. `puckton.world.json` shares the directory but is a `puck.world.v1` document (a different format, authored by the world sculptor) and the stage skips it. The `world`-graph documents also run live (`--run <path>`, host backend) through the shared world renderer, not only as parse corpus. |
| [api/](api/) | DocFX configuration for API reference generation. |

Per-project READMEs that double as handoff docs:
[src/Puck.Input](../src/Puck.Input/README.md) ·
[experimental/Puck.BareMetal](../experimental/Puck.BareMetal/README.md) ·
[experimental/Puck.AdvancedGamingBrick.Post](../experimental/Puck.AdvancedGamingBrick.Post/README.md)

## Active plans

| Doc | Scope |
|---|---|
| [overworld-demo-plan.md](overworld-demo-plan.md) | The demo's plan of record — the overworld (room + four bootable console cabinets + staged layout transitions), its seams, and the next steps. Covers multiplayer console mode + proximity takeover, battery saves, and `--rom` fourth-wall boots. **The Demo is GREENFIELD** — verify by running it, not by gating; don't promote its features into Post unasked. |
| [game-studio-plan.md](game-studio-plan.md) | The in-engine game studio's plan of record: the sculpt→bake→forge→play north star, the binding user decisions, and the remaining arcs — the card games (Poker/Solitaire + determinism design), audio, the editor-through-SDF-VM convergence, the recursion, and the open ❓ forks to prompt on. What already EXISTS is in the capability catalog §7. |
| [sdf-world-render-centralization-plan.md](sdf-world-render-centralization-plan.md) | The shared world render host (`SdfWorldRenderBuilder`, `WorldNode` driven through data, the document screen-source seam); the status section says what remains open (live-camera re-host, cross-backend `produce`). The plan body is the design record. |
| [sdf-accumulator-plan.md](sdf-accumulator-plan.md) | The flat accumulator: `mapCore` keeps ONE running distance, so `Intersection`/`Xor`/`Onion`/`Dilate`/`Displace` compose against the whole scene before them, not the shape you meant. The measured evidence (five sites, all fixed), why the forge is the one safe place, why such an instance cannot be culled, and the **open decision** on a scoped accumulator (`PushField`/`PopField`) with its measured cost. The settled *rule* lives in `SdfBlendOp`, `sdf-vm.hlsli` and the `sdf-world` skill. |
| [sdf-vm-evolution-plan.md](sdf-vm-evolution-plan.md) | The SDF renderer's evolution roadmap: the landed Lipschitz-safe/over-relaxed marcher (D1), the log-spherical Droste warp (D2), and the whimsy trio (4-tap normals, vesica, soft shadows) — and, for a future agent, the **unrealized** work (D3 hierarchical cone march + instance BVH, segment tracing, a compute shadow-cull, noise/displacement ops). The landed *contracts* live in the `sdf-world` skill. |
| [machine-fleet-plan.md](machine-fleet-plan.md) | The machine-fleet PERFORMANCE plan of record, fully measured: the `--bench` instrument, fleet scaling / burst / latency / footprint numbers, the M3 verdict (deferred), and the levers in measured-payoff order. |
| [machine-fleet-briefing.md](machine-fleet-briefing.md) | The workload-class and vision-posture record behind the fleet plan: what the fleet must serve — choirs, dormancy, interactions, the settled scale posture — plus standing constraints. |
| [ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) | The GB/GBC/GBA "ideal deterministic machine" roadmap (three rules, milestones M0–M6, salvage sources). |

## No historical records live here

Every doc in this folder is LIVING — trust it. Completed design/investigation
records are kept in git history only, never in the tree: before a doc is
deleted, its durable knowledge is distilled into a living home (usually the
matching skill under `.claude/skills/` — the GB PPU timing frontier lives in
`gaming-bricks`, the SDF VM contract in `sdf-world`, the forge contracts in
`rom-forge`).

*When adding a doc, add a row here. When a living doc stops being true,
either fix it or retire it — distill the durable knowledge into a living
home, then delete.*
