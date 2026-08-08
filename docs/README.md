# Puck documentation

Puck documentation describes the current product. Design history belongs in
Git history; durable constraints belong in the relevant guide, skill, or code
contract. Research pages retain citations and measured evidence because those
sources remain useful to engineering decisions.

> **This index shrank hard on 2026-08-02.** Roughly two dozen plan, catalog,
> and survey documents were deleted rather than corrected, because each one
> described work sequenced out of `experimental/` — a tree that is now off
> limits — or asserted a verification status that `Puck.Post`'s quarantine made
> false. A document that would produce the wrong action today is not stale, it
> is hostile. The capabilities those documents described did not move anywhere:
> where one is gone, the capability is absent from the running product and
> **nothing plans its return**. Do not reconstruct a deleted plan from memory or
> from Git history and present it as current.

## Start here

| Document | Purpose |
|---|---|
| [Vision](vision.md) | What Puck is and where it is going, end to end. Read this first. |
| [Project map](project-map.md) | Project ownership, dependencies, and layering rules. |
| [Agent guide](agent-guide.md) | Development workflow, verification, environments, and documentation policy. |

There is deliberately no capability catalog and no capability register. Both
carried a per-capability verification-status column that stopped being true when
`Puck.Post` was quarantined, and a document consulted to decide whether
something is safe must not assert coverage it cannot back. Ask the code, or run
`Puck.World`. If an inventory is wanted again, generate it and give it a runner
that fails when it disagrees with its source.

## Active engineering ledgers

| Document | Purpose |
|---|---|
| [The world model](world-model.md) | **DESIGN.** The plan of record for federation, presence and scale: one kind of thing (a world) and six relationships between worlds, the five invariants everything rests on, the remaining work ordered by leverage, and the designs that were considered and rejected with reasons. Read it before proposing anything about zones, portals, identity or cross-world state. |
| [Signed carriage — wire specification](signed-carriage-wire.md) | **NORMATIVE.** The byte layout, canonicality rule, refusal set, and verify algorithm for the signed carriage envelope, written to be implementable from prose alone — plus the interchange fixture's file set, its `manifest.txt` format, and the `export`/`verify` tool protocol and exit codes the two sides cross-check over. The envelope is a specification each side implements independently rather than a shared library, so this is the contract between `src/Puck.Carriage` and Web.Functions' `BindingCarriage`. |
| [Capability channels](capability-channels-plan.md) | **DESIGN.** The plan of record for reworking input, commands, addons, and UI onto one contribution model: authority as unforgeable handles rather than an access-control list, Simulation and Presentation lanes, vocabularies (including SDF operations) as granted capability subsets, quota as a property of a handle, and SDF constructions as documents. Carries the live traps and the forward Phase 2/3 work. |
| [Capability channels — START HERE](capability-channels-STATE.md) | The campaign's slow-moving truth in one page: what is ruled, what is actually landed, the premise set Phase 3 may build against, and the two re-key boundaries. Read it before the plan. |
| [Input backend surface audit](reviews/2026-07-31-input-backend-surface-audit.md) | Measured reachable input per window backend. A person cannot meaningfully drive `Puck.World` on Linux and never could: Wayland emits no input at all, Xcb emits no letters/Space/Tab/text, and gamepads are Windows-only — including the Steam Deck's own pad. Also the Windows F9–F12 gap and the chords the Win32 layer swallows before bindings see them. |
| [SDF host addressing survey](reviews/2026-08-01-sdf-host-addressing-survey.md) | Survey of host addressing options for the SDF path. |
| [Affordance coverage check](reviews/2026-08-02-affordance-coverage-check.md) | Design requirements for a per-context affordance-reachability check that does not exist yet. Three predicates, each derived from a real past defect: dangling reference, unreachable-in-context, unenterable precondition. Only the first is enforced today, by `WorldAffordances.Validate`. |

## Reference corpora

| Document | Purpose |
|---|---|
| [SDF handbook](sdf-handbook/README.md) | Conceptual and operational guide to authoring, rendering, queries, and baking. |
| [SDF research wiki](sdf-wiki/README.md) | Cited technique reference, empirical verdicts, and rejected approaches. |
| [AGB research wiki](agb-wiki/README.md) | Cited architecture, accuracy, determinism, and performance reference. |
| [API reference](api/index.md) | Landing page and build instructions for the docfx member reference over the reusable libraries. The site itself (`docs/api/api/`, `docs/api/_site/`) is git-ignored build output of `dotnet docfx docs/api/docfx.json`, regenerated on demand. |
| [Document examples](examples/) | Reference documents for the live authoring families: `creations/` (`puck.creation.v1`) and `tunes/` (`puck.audio.v1`). Nothing loads them; they are read by hand. |
| [Verification runners](verification/) | Committed, re-runnable batteries — one directory per contract, each with a self-documenting `run.ps1` that builds what it needs and exits nonzero on a miss. The authority battery lives at the repository root, `verification/authority/`, beside its fixtures — QUARANTINED (2026-08-06; its stub names the successor, `tests/Puck.World.Tests`). |
| [Acknowledgments](ACKNOWLEDGMENTS.md) | Source provenance, licensing notices, and credit. |

The maths research corpus — the polynomial-tail / Beatty / metallic-mean /
parity-irreducibility program, with its theorems, certificates, verifiers, and
Lean project — lives OUTSIDE this repository, in `Maths/` inside the `Temp`
directory beside the repository root. It carries its own `MANIFEST.md` there;
this repository holds no link to it, because a path outside the tree cannot be
checked and a stale one reads as a missing file. The production primitives it
yielded remain in `src/Puck.Maths`.

## Project handoffs

Detailed subsystem usage belongs beside the code. Important entry points
include:

- [`Puck.World`](../src/Puck.World/README.md) — the entry point for the world
  game's three-project split; it links its siblings:
  - [`Puck.World.Data`](../src/Puck.World.Data/README.md)
  - [`Puck.World.Server`](../src/Puck.World.Server/README.md)
- [`Puck.Input`](../src/Puck.Input/README.md)
- [`Puck.DirectX`](../src/Puck.DirectX/README.md)
- [`Puck.SdfVm` shaders](../src/Puck.SdfVm/Assets/Shaders/README.md)
- [`Puck.HumbleGamingBrick.Post`](../src/Puck.HumbleGamingBrick.Post/README.md)
- [`Puck.AdvancedGamingBrick.Post`](../src/Puck.AdvancedGamingBrick.Post/README.md)

## Maintenance

- Update a document when its code contract changes.
- Remove completed rollout steps, dated progress logs, and superseded plans.
- Preserve measurements only when they still explain a current threshold,
  limitation, or decision.
- Preserve source provenance, licensing notices, and research citations.
- Add new top-level documents to this index.
- Never link a document that does not exist, and never describe a plan whose
  document was deleted as though it still sequences work.
