# Plans

This directory holds proposed implementation plans and preserved technical decisions. Current architecture lives in the owner documents under `docs/architecture/`; the plans here retain rationale, constraints, dated evidence, and work that remains to be scheduled.

## Rendering and authoring

- [Shader pipelines and hybrid rendering](shader-pipeline-evolution.md)—staged graphics attachments, hybrid visibility, packaging, and representation work.
- [DSL and cartridge release hardening](dsl-release-hardening.md)—release checks and compiler hardening.
- [Retail-scale cartridges](retail-scale-cartridges.md)—capacity and authoring for large cartridge data.
- [Screens and machine extensions](machine-extensions.md)—machine hosting, firmware, authoring, package boundaries, and hardware interaction.

## Runtime and hosting

- [Groups and cooperative matchmaking](group-finder.md)—groups, membership, matchmaking, recovery, and admission.
- [World runtime consolidation](world-runtime-consolidation.md)—`WorldServer` facade and runtime consolidation.
- [MCP integration](mcp-integration.md)—MCP hosting, tools, trust, capture, and release gates.
- [Abstract-machine costing](abstract-machine-costing.md)—deterministic instruction pricing and bounded work.
- [State consolidation](state-consolidation.md)—remaining state-system reuse opportunities.

## Documentation

- [Documentation consistency](documentation-consistency.md)—editorial conventions, researched findings, and completed assignments for the manual.

## Reference game

- [Game development plan](game-development.md)—game sequencing, detailed game topic plans, future waves, federation work, and verification requirements.

The current world architecture is owned by [Worlds and federation](../architecture/worlds.md). The [Reference game design](../game/design.md) defines the reference game requirements; its detailed implementation work remains in the game development plan.
