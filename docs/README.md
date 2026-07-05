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
| [examples/](examples/) | Twenty valid `puck.run.v1` run documents plus one checked-in negative (`world-single-bad-material.json`), all exercised by the Post `run-document` stage. Since 2026-07-04 the `world`-graph documents also RUN live (`--run <path>`, host backend) through the shared world renderer — they are no longer parse-only corpus. |
| [api/](api/) | DocFX configuration for API reference generation. |

Per-project READMEs that double as handoff docs:
[src/Puck.Input](../src/Puck.Input/README.md) ·
[experimental/Puck.BareMetal](../experimental/Puck.BareMetal/README.md) ·
[experimental/Puck.AdvancedGamingBrick.Post](../experimental/Puck.AdvancedGamingBrick.Post/README.md)

## Active plans

| Doc | Scope |
|---|---|
| [overworld-demo-plan.md](overworld-demo-plan.md) | The demo's plan of record — the overworld (room + bootable console stands + staged layout transitions), its seams, and the next steps. Dated STATUS block up top: multiplayer console mode + proximity takeover, battery saves, `--rom` fourth-wall boots landed 2026-07-04. |
| [sdf-world-render-centralization-plan.md](sdf-world-render-centralization-plan.md) | The shared world render host — phases 1–4 LANDED 2026-07-04 (`SdfWorldRenderBuilder`, `WorldNode` re-enabled through data, the document screen-source seam); the STATUS block up top says what remains open (live-camera re-host, cross-backend `produce`). The plan body below it is the design record. |
| [machine-fleet-plan.md](machine-fleet-plan.md) | The machine-fleet PERFORMANCE plan of record, fully measured: the `--bench` instrument, fleet scaling / burst / latency / footprint numbers, the M3 verdict (deferred), and the levers in measured-payoff order. |
| [machine-fleet-briefing.md](machine-fleet-briefing.md) | The workload-class and vision-posture record behind the fleet plan (executed 2026-07-03): what the fleet must serve — choirs, dormancy, interactions, the settled scale posture — plus standing constraints. |
| [ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) | The GB/GBC/GBA "ideal deterministic machine" roadmap (three rules, milestones M0–M6, salvage sources). |

## Historical records (do not treat as current)

| Doc | What it recorded |
|---|---|
| [avatar-vm.md](avatar-vm.md) | The avatar-era SDF VM reference — references the deleted `src/Puck.Avatars`; the VM's descendant is `src/Puck.SdfVm`. |
| [debug-visualization.md](debug-visualization.md) | The monolith-era debug-view subsystem (deleted with it); kept as the design reference for a future split-engine debug layer. |
| [soa-instruction-stream-scope.md](soa-instruction-stream-scope.md) | The SoA instruction-stream split: built, measured, landed. Design record. |
| [gb-cycle-accuracy-scorecard.md](gb-cycle-accuracy-scorecard.md) | GB/GBC cycle-accuracy snapshot from the (since-deleted) conformance harness. |
| [gb-ppu-accuracy-findings.md](gb-ppu-accuracy-findings.md) | Root cause of the remaining GB PPU mid-mode-3 timing failures. |

*When adding a doc, add a row here and mark it living or historical. When a
living doc stops being true, either fix it or move its row down here.*
