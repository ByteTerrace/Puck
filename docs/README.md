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
| [World authoring, audio, and Forge implementation review](reviews/2026-07-19-large-change-set-review.md) | Static review of the landed P3–P6, audio, Forge, and overlay work: capacity contracts, transactional/lifetime defects, proof gaps, and ordered remediation. |
| [SDF renderer performance plan](reviews/2026-07-16-sdf-renderer-sota-perf-plan.md) | Active measurement-gated SDF optimization phases and current reopen criteria. |
| [Demo port ledger](reviews/2026-07-18-demo-port-ledger.md) | Row-by-row tracking of Demo capabilities against their World successors. |

The moldable-state, World UI/editor, World audio, and branch-closeout documents
retired once their arcs landed: their commitments are carried in the
[Demo to World port](demo-to-world-port-plan.md) plan's carried tracks, and their
durable contracts — the settled questions, the genre-neutrality audit, the
accepted asymmetries, the authoring-gesture determinism boundary, and the full
proof-battery enumeration — live in [`Puck.World`](../src/Puck.World/README.md).
Git history holds the rest.

## Content and roadmap

| Document | Purpose |
|---|---|
| [Demo to World port](demo-to-world-port-plan.md) | The twelve-arc plan that ports `Puck.Demo` capabilities into `Puck.World` and removes the project. |
| [Game studio](game-studio-plan.md) | Current creator workflow and remaining product roadmap. |
| [Machine fleet](machine-fleet-plan.md) | Emulator-fleet performance model and optimization priorities. |
| [Machine fleet briefing](machine-fleet-briefing.md) | Workload classes, scale posture, and design constraints. |
| [Ideal GamingBrick](ideal-gaming-brick-plan.md) | Cross-generation emulator architecture and roadmap. |
| [Run-document examples](examples/) | Valid and intentionally invalid data examples used by verification. |

## Demo (retiring)

`Puck.Demo` is the prototyping ground `Puck.World` supersedes; its capabilities port into World and shared
libraries over time, and nothing in a live project depends on it.

| Document | Purpose |
|---|---|
| [Overworld demo](overworld-demo-plan.md) | The unified demo experience, control plane, data model, and open work. |
| [`Puck.Demo`](../src/Puck.Demo/README.md) | Project handoff notes for the retiring demo project. |

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
