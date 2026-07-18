# Puck documentation

Puck documentation describes the current product. Design history belongs in
Git history; durable constraints belong in the relevant guide, skill, or code
contract. Research pages retain citations and measured evidence because those
sources remain useful to engineering decisions.

## Start here

| Document | Purpose |
|---|---|
| [Capability catalog](capability-catalog.md) | Supported capabilities, verification status, and entry points. |
| [Project map](project-map.md) | Project ownership, dependencies, and layering rules. |
| [Agent guide](agent-guide.md) | Development workflow, verification, environments, and documentation policy. |
| [API reference](api/index.md) | Generated public API documentation. |

## Engine references

| Document | Purpose |
|---|---|
| [Backend parity summary](feature-parity-summary.md) | Current Vulkan and Direct3D 12 parity status. |
| [Backend parity table](feature-parity-table.md) | Capability-level backend support and evidence. |
| [Platform display kinds](platform-display-kinds.md) | Native display and surface dispatch contracts. |
| [Engine benchmark](engine-bench-plan.md) | Benchmark scenes, scoring, report format, and operation. |
| [UI design tokens](ui-design-tokens.md) | Shared visual-token vocabulary and ownership. |

## Active engineering ledgers

| Document | Purpose |
|---|---|
| [Disposal implementation audit](reviews/2026-07-17-disposal-implementation-audit.md) | Active full-repository disposal findings, ownership evidence, and remediation work items. |
| [Moldable-state code review](reviews/2026-07-18-moldable-state-code-review.md) | Active findings from the moldable-state implementation review: grant exclusivity, player-section completeness, screen removal, and source-diff hygiene. |
| [SDF renderer performance plan](reviews/2026-07-16-sdf-renderer-sota-perf-plan.md) | Active measurement-gated SDF optimization phases and current reopen criteria. |
| [World moldable-state handoff](reviews/2026-07-18-world-moldable-state-handoff.md) | Surfaces the executed Puck.World moldable-state arc shipped for the editor and UI arcs to build on, with zero new "make X data" prework. |

## Demo and content

| Document | Purpose |
|---|---|
| [Overworld demo](overworld-demo-plan.md) | The unified demo experience, control plane, data model, and open work. |
| [Game studio](game-studio-plan.md) | Current creator workflow and remaining product roadmap. |
| [Machine fleet](machine-fleet-plan.md) | Emulator-fleet performance model and optimization priorities. |
| [Machine fleet briefing](machine-fleet-briefing.md) | Workload classes, scale posture, and design constraints. |
| [Ideal GamingBrick](ideal-gaming-brick-plan.md) | Cross-generation emulator architecture and roadmap. |
| [Run-document examples](examples/) | Valid and intentionally invalid data examples used by verification. |

## SDF world

| Document | Purpose |
|---|---|
| [SDF handbook](sdf-handbook/README.md) | Conceptual and operational guide to authoring, rendering, queries, and baking. |
| [World-render assembly](sdf-world-render-centralization-plan.md) | Shared render-builder ownership and extension points. |
| [Carve baking](carve-bake-plan.md) | Brick representation, lifecycle, synchronization, and verification. |
| [SDF backlog](sdf-backlog.md) | Open engineering work and measured reopen criteria. |
| [SDF benchmark notes](sdf-bench-notes.md) | Current performance measurements and their interpretation. |
| [SDF shader profiling](sdf-shader-profiling.md) | Nsight source/flame-graph workflow and evidence-driven ISA experiments. |
| [SDF research wiki](sdf-wiki/README.md) | Cited technique reference, empirical verdicts, and rejected approaches. |
| [SDF survey](sdf-sota-survey.md) | Ranked engineering recommendations derived from the research wiki. |

## Emulator research

| Document | Purpose |
|---|---|
| [AGB research wiki](agb-wiki/README.md) | Cited architecture, accuracy, determinism, and performance reference. |
| [AGB survey](agb-sota-survey.md) | Prioritized emulator recommendations and evidence. |

## Project handoffs

Detailed subsystem usage belongs beside the code. Important entry points
include:

- [`Puck.World`](../src/Puck.World/README.md)
- [`Puck.Input`](../src/Puck.Input/README.md)
- [`Puck.Demo`](../src/Puck.Demo/README.md)
- [`Puck.DirectX`](../src/Puck.DirectX/README.md)
- [`Puck.SdfVm` shaders](../src/Puck.SdfVm/Assets/Shaders/README.md)
- [`Puck.HumbleGamingBrick.Post`](../src/Puck.HumbleGamingBrick.Post/README.md)
- [`Puck.AdvancedGamingBrick.Post`](../src/Puck.AdvancedGamingBrick.Post/README.md)
- [`Puck.BareMetal`](../experimental/Puck.BareMetal/README.md)

## Maintenance

- Update a document when its code contract changes.
- Remove completed rollout steps, dated progress logs, and superseded plans.
- Preserve measurements only when they still explain a current threshold,
  limitation, or decision.
- Preserve source provenance, licensing notices, and research citations.
- Add new top-level documents to this index.
