# Puck documentation

Puck is a C# engine for document-defined interactive worlds. Start with the
[overview](overview.md) to understand the components, or
[build and run Puck](getting-started.md) to try the application.

| I want to… | Start here |
|---|---|
| Understand how the engine works | [Engine architecture](architecture/README.md): documents, simulation, presentation, and world hosting. |
| Use the simulation libraries in my own host | [State and rules](reference/state.md), [Commands and input](reference/commands.md), [Deterministic numerics](reference/maths.md), and [Physics kernels](reference/physics.md). |
| Connect devices and provide feedback | [Device input](reference/input.md): controllers, motion, haptics, and lighting. |
| Create or change content | [Authoring content](authoring/README.md): world documents, the DSL, shader pipelines, and cartridges. |
| Work on graphics or emulation | [Rendering](rendering/README.md) or [Machine emulation](emulation/README.md): introductions followed by detailed technical guides. |
| Investigate or change the code | [Development](development/README.md): project ownership, debugging, verification, and releases. |
| Look up a command, schema, or API | [Reference](reference/README.md): the precise contracts and their owning projects. |

[Plans](plans/README.md) describe proposed work and its completion criteria.
[Design decisions](decisions/README.md) explain important engineering choices.
The [reference game](game/README.md) has its own design and artwork; it is not
required reading for using the engine libraries.

📚 [Repository overview](../README.md) · 🛠️ [Writing documentation](development/documentation.md) · 🎨 [Branding](../branding/README.md)

## Orientation

| Doc | Answers |
|---|---|
| [docs/project-map.md](project-map.md) | What each `Puck.*` project is for and how they layer. Its layering block is generated (`puck architecture --map`) and checked (`--check`); never hand-edit it. |
| [docs/development/contributing.md](development/contributing.md) | How to find code and verify a change, hardware and toolchain cautions, conventions. Read it before touching GPU or emulator code. |
| [docs/overview.md](overview.md), [docs/architecture/README.md](architecture/README.md) | What the engine does and how its runtime boundaries fit together. |
| [docs/architecture/worlds.md](architecture/worlds.md), [src/Puck.World.Server/README.md](../src/Puck.World.Server/README.md) | How worlds are represented and run: documents, authoritative simulation, the server. |
| [docs/plans/README.md](plans/README.md) | Proposed engineering work, including [runtime and delivery](plans/runtime-and-delivery.md) and [machines and cartridges](plans/machines-and-cartridges.md). |
| [docs/game/README.md](game/README.md), [src/Puck.World/README.md](../src/Puck.World/README.md) | The reference game's design and play programme, and the World's current commands. Read them only for game work. The debug world is the one live world; the game's other worlds are quarantined under `experimental/` (see [the reference tree](../experimental/README.md)). |
| [docs/decisions/engine-design.md](decisions/engine-design.md) | Settled engine design decisions, including [configuration and sessions](decisions/engine-design.md#configuration-and-operations-remain-discoverable). |
| [docs/reference/input.md](reference/input.md) | Controller input (`src/Puck.Input`): architecture, feature matrix, hardware status. |
| [docs/development/documentation.md](development/documentation.md) | Prose, titles, filenames, README ownership, navigation, the XML documentation register, and how to evaluate an agent skill. |

Docs state current behavior, limitations and open work in the present tense.
They name no dates, no commit SHAs and no versions of outside tools, unless the
version itself adds something (a pin the build enforces, a minimum a feature
needs); verification evidence goes in the commit message. Update incoming links and routing, agent routing included, in the
change that moves a document.


## Area skills

Load the skill owning the work under `.claude/skills/`: `puck-world`, `puck-dsl`, `rendering`, `sdf-authoring`, `maths-usage`, `maths-laws`, `gaming-bricks`, `rom-forge`, `dotnet-performance`, `symbol-analysis`, `content-search`, `boy-scout`, `verification`, `review-passes`, or `orchestration`. Each maps its code, contracts and execution, and routes adjacent work.
