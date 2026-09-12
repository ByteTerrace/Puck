# Specifications and implementation briefs

This directory contains detailed technical specifications, implementation briefs, and request-for-comments (RFCs) for Puck engine subsystems. These documents record the technical rationale, mathematical constraints, and agreed execution plans for major features.

Living architectural rules live in the root documentation ([vision.md](../vision.md), [project-map.md](../project-map.md)), while operational verification rules belong in [agent-guide.md](../agent-guide.md).

## Specification catalog

| Specification | Focus area | Scope & purpose |
|---|---|---|
| [World model](world-model.md) | World federation, presence, scale | The normative specification for multi-world relationships, portals, topological adjacencies, session resolution, simulation rates, federated handoffs, determinism, and attestation. |
| [Screens and machine extensions](machine-extensions.md) | `Puck.World.Machines`, `Puck.GamingBricks` | Making Gaming Bricks ordinary extensions while preserving physical screens, convenient cabinet authoring, and direct hardware interaction. |
| [Groups and cooperative matchmaking](group-finder.md) | `Puck.World.Schema`, `Puck.World.Server` | In-world group finder, portable membership, recoverable group states, and matchmaking over existing authority and storage. |
| [Retail-scale cartridges](retail-scale-cartridges.md) | `Puck.GamingBricks`, `Puck.State` | Evaluation of whether Puck data can carry a retail-scale RPG (e.g. Pokémon Gold/Silver scale) without restating vocabulary. |
| [World runtime consolidation](world-runtime-consolidation.md) | `Puck.World.Server`, Subsystems | Engine consolidation plan: rule writes as simulation state, baked static geometry queries, and `WorldServer` as a facade. |
| [Abstract-machine costing](abstract-machine-costing.md) | `Puck.State`, `Puck.World.Schema` | Deterministic cost model replacing work-unit heuristics with reproducible instruction-price schedules across platforms. |
| [Puck Model Context Protocol (MCP)](puck-mcp-plan.md) | `Puck.Mcp` | Adversarial review and implementation plan for Puck MCP hosting, tool contracts, and agentic interaction boundaries. |
| [State system duplication](state-duplication.md) | `Puck.State`, `Puck.World` | Analysis of state evaluation machinery, slot handling, and compilation reuse opportunities. |

## Guidance for authors and agents

1. **Specifications propose and plan; the code is authoritative.** If a specification claims a capability exists but the active code does not implement it, the code wins.
2. **Focus on decisions, not ephemeral session chat.** Technical rationale, invariants, data schemas, and mathematical obligations survive; transcript logs and temporary worker handoffs should be consolidated into settled design.
3. **Update inbound references.** When a specification is retired, superseded, or completely merged into core architecture, perform an orphan audit to ensure all inbound links are updated.
