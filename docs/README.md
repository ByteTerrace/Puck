# Puck Documentation Vault

Welcome to the Puck documentation vault. This directory is the authoritative knowledge base for the Puck engine, the `Puck.World` reference game, and its underlying mathematical and rendering foundations. It is structured for reading front-to-back in an editor, navigating via tools like Obsidian, and guiding autonomous coding agents.

---

## 🗺️ Reading Paths & Orientation

Select the path that matches your goal:

```mermaid
graph TD
    Start([Where do you want to start?]) --> Newbie[New to Puck?]
    Start --> EngineDev[Working on Graphics / VM?]
    Start --> GameDev[Authoring Content / Machines?]
    Start --> Contributor[Ready to Submit Code?]

    Newbie --> Vision[docs/vision.md<br/>Notation, discipline & world model]
    Vision --> Campaign[docs/campaign.md<br/>Active charter & roadmap]

    EngineDev --> SdfWiki[docs/sdf-wiki/<br/>Unified SDF vault: handbook & encyclopedia]
    EngineDev --> GbWiki[docs/gb-wiki/<br/>Gaming Bricks: AGB & HGB virtual consoles]

    GameDev --> Specs[docs/specs/<br/>Subsystem RFCs & specifications]
    GameDev --> Art[docs/art/<br/>Character & art direction]

    Contributor --> ProjectMap[docs/project-map.md<br/>Layering & architecture gate]
    ProjectMap --> AgentGuide[docs/agent-guide.md<br/>Verification, env vars & gotchas]
    AgentGuide --> Ci[docs/ci.md<br/>CI/CD & release pipelines]
```

### 1. New to Puck?
1. **[vision.md](vision.md)** — **Start here.** What Puck is (a closed data notation) and what it refuses to be. The core invariants, determinism doctrine, and the world model.
2. **[campaign.md](campaign.md)** — What we are collectively building. The binding charter, project rulings, and current arc. For the detailed technical engineering breakdown, see [campaign-work-plan.md](campaign-work-plan.md).
3. **[project-map.md](project-map.md)** — How the solution's split `Puck.*` projects layer, what each owns, and the dependency rules enforced by `puck architecture --check`.

### 2. Working on Engine, Hardware, or Graphics?
- **[sdf-wiki/](sdf-wiki/README.md)** — The complete signed-distance knowledge vault: a 9-chapter guided textbook ([`handbook/`](sdf-wiki/handbook/README.md)) plus an encyclopedic reference library of mathematical proofs, Lipschitz bounds, raymarching acceleration, and technique verdicts ([`reference/`](sdf-wiki/reference/README.md)).
- **[gb-wiki/](gb-wiki/README.md)** — The retro virtual hardware emulation wiki: full architecture references for both the 8-bit Humble Gaming Brick ([`hgb/`](gb-wiki/hgb/README.md), SM83 core) and 32-bit Advanced Gaming Brick ([`agb/`](gb-wiki/agb/README.md), ARM7TDMI core), along with shared hosting and link cable protocols ([`shared/`](gb-wiki/shared/README.md)).
- **[citations.md](citations.md)** — Full evidence citations for hardware research and `Puck.Maths` theorems.

### 3. Implementing Features or Subsystems?
- **[specs/](specs/README.md)** — The catalog of subsystem specifications, RFCs, and implementation briefs:
  - [World model](specs/world-model.md) — Multi-world federation, portal topology, session resolution, rates, and handoffs.
  - [Screens and machine extensions](specs/machine-extensions.md) — Machine hosting, screen mirrors, and hardware control.
  - [Groups and cooperative matchmaking](specs/group-finder.md) — In-world matchmaking, portable membership, and recovery.
  - [Retail-scale cartridges](specs/retail-scale-cartridges.md) — Capacity evaluation for large RPG data files.
  - [World runtime consolidation](specs/world-runtime-consolidation.md) — Engine refactoring and facade design.
  - [Abstract-machine costing](specs/abstract-machine-costing.md) — Deterministic pricing models across platforms.
  - [Puck MCP plan](specs/puck-mcp-plan.md) — Model Context Protocol architecture and safety boundaries.
  - [State duplication](specs/state-duplication.md) — Analysis of state evaluation consolidation.
- **[art/](art/README.md)** — Visual design briefs and concept packs:
  - [Armored chibi hero brief](art/armored-chibi-hero-brief.md) — Main character design, proportions, and motion rules.
  - [Moth concept pack](art/moth-concept-pack-2026-09-09/README.md) — Six concept model sheets and generation prompts.

### 4. Contributing Code & Verifying?
- **[agent-guide.md](agent-guide.md)** — Essential guide for humans and AI agents: semantic C# tools (`puck references`, `puck search`), test execution, hardware gotchas, and verification recipes.
- **[ci.md](ci.md)** — Detailed specification of GitHub Actions workflows, release builds, package publishing, and Azure deployment.
- **[verification/manual/](verification/manual/README.md)** — Hand-run `SendInput` desktop harnesses for pointer gestures and camera orbit focus loss.

---

## 📚 Information Architecture

| Directory / File | Description | Audience |
|---|---|---|
| **[vision.md](vision.md)** | Foundational philosophy, closed vocabulary, determinism, world model. | Everyone |
| **[project-map.md](project-map.md)** | Subsystem ownership, stability, and layering block (gated by CLI). | Developers, Agents |
| **[agent-guide.md](agent-guide.md)** | Contributor orientation, verification procedures, environment gotchas. | Contributors, Agents |
| **[campaign.md](campaign.md)** | Shipped game charter, project rulings, and current milestone shape. | Everyone |
| **[campaign-work-plan.md](campaign-work-plan.md)** | Detailed 5-track technical execution plan, slice definitions, and gated ladder. | Developers, Architects |
| **[campaign-milestones.md](campaign-milestones.md)** | Historical verification log and dated milestone validation runs. | Reference |
| **[ci.md](ci.md)** | CI/CD automation, `puck` CLI actions, artifact generation, Azure deploy. | DevOps, Agents |
| **[citations.md](citations.md)** | External hardware research and mathematical theorem evidence citations. | Reference |
| **[world-name-registry.md](world-name-registry.md)** | Autogenerated document field registry (gated by `puck registry --check`). | Generated Reference |
| **[sdf-wiki/](sdf-wiki/README.md)** | Unified SDF knowledge vault: 9-chapter textbook + technical encyclopedia. | Graphics Engineers, Students |
| **[gb-wiki/](gb-wiki/README.md)** | Gaming Bricks wiki: 8-bit HGB & 32-bit AGB cores, PPU, APU, and link cables. | Emulation Engineers |
| **[specs/](specs/README.md)** | Subsystem RFCs, architectural design briefs, and costing models. | System Architects |
| **[art/](art/README.md)** | Visual design hub, character briefs, model sheets, and concept packs. | Artists, Designers |
| **[verification/manual/](verification/manual/README.md)** | Manual OS input injection test harnesses (`SendInput`). | QA, Developers |
| **[examples/](examples/README.md)** | Example standalone creation models and diegetic audio chiptunes. | Content Authors |
| **[api/](api/index.md)** | DocFX API reference configuration and overview. | API Consumers |

---

## ⚖️ Tree Documentation Laws

All documentation in this repository follows strict quality rules:

1. **Single Home per Fact (The No-Drift Law):** Every fact has exactly one owning document. Other surfaces summarize and link out.
2. **State No Status in Living References:** Living architectural documents describe *what the system is*, never transient progress. The code answers what is built.
3. **Audience Registers:**
   - **`docs/` and `README.md`:** Narrative prose, accessible to motivated middle- and high-school students.
   - **Code XML Comments:** Precise API reference for developers in IDEs.
   - **`CLAUDE.md` and `.claude/`:** Operational instructions for agents.
4. **Mechanical Verification:** Links, citations, layering blocks, and registries are validated mechanically by CLI tools (`puck doc-links`, `puck architecture --check`, `puck registry --check`).
