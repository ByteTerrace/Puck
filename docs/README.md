# Puck documentation

**Read in this order. There is ONE campaign, and these three documents are it.**

| # | Document | What it answers |
|---|---|---|
| 1 | [Vision](vision.md) | What Puck IS and why — the notation, the discipline, what it refuses to be. Carries no status, deliberately. |
| 2 | [The campaign](campaign.md) | **What we are collectively building, where it stands, and what is next.** The four-world charter, the work list, and the check behind every claim. Read before picking up work. |
| 3 | [Agent guide](agent-guide.md) | How to verify, environments, hardware gotchas, conventions. Read before touching GPU or emulator code. |

Everything below is **reference you consult while building**, never a place to
start. A document here describes one system; the campaign describes the point
of them. If you find yourself deep in a system document without knowing which
campaign track you are serving, stop and re-read (2).

## The rule this index exists to enforce

**No document asserts per-capability status.** Two once did — a capability
catalog and a capability register — and both claimed verification nothing
checked. They were deleted, and the failure recurred anyway: the
capability-channels pair carried a `Landed?` column until 2026-08-10 and drifted
in *both* directions, listing closed decisions as open security risks and
missing work that had shipped. Sessions read those columns and reported
capabilities as done that were not.

So: **a status claim duplicates what the code answers better and is pure
liability; a decision records what the code cannot answer and stays valuable
even when stale.** Keep decisions. Delete status. Ask the code, or run
`Puck.World`. If an inventory is genuinely wanted, generate it and give it a
runner that fails when it disagrees with its source.

And **every durable artifact declares its own falsifier** — a design document
states, as a re-runnable check, the premise that would kill it. An artifact that
cannot say what would falsify it is asking to be believed.

## Design references

| Document | Purpose |
|---|---|
| [The world model](world-model.md) | **DESIGN.** Federation, presence, scale and portals: relationships between worlds; references versus scoped destinations; authored per-world clocks and pause semantics; joined-session rendering; transactional embodiment; capability-driven admission; and rejected shapes. Read before proposing zones, portals, identity or cross-world state. |
| [Signed carriage — wire specification](signed-carriage-wire.md) | **NORMATIVE.** Byte layout, canonicality rule, refusal set and verify algorithm for the signed carriage envelope, written to be implementable from prose alone. The envelope is a specification each side implements independently rather than a shared library, so this is the contract between `src/Puck.Carriage` and Web.Functions' `BindingCarriage`. |
| [Project map](project-map.md) | Project ownership, dependencies, and layering rules. Its layering block is GENERATED (`puck architecture --map`) — do not hand-edit it. |

## Reference corpora

| Document | Purpose |
|---|---|
| [SDF handbook](sdf-handbook/README.md) | Conceptual and operational guide to authoring, rendering, queries, and baking. |
| [SDF research wiki](sdf-wiki/README.md) | Cited technique reference, empirical verdicts, and rejected approaches. |
| [AGB research wiki](agb-wiki/README.md) | Cited architecture, accuracy, determinism, and performance reference. |
| [API reference](api/index.md) | Landing page and build instructions for the docfx member reference. The site itself is git-ignored build output of `dotnet docfx docs/api/docfx.json`, regenerated on demand. |
| [Document examples](examples/) | Reference documents for the live authoring families: `creations/` (`puck.creation.v1`) and `tunes/` (`puck.audio.v1`). Nothing loads them; they are read by hand. |
| [Verification runners](verification/) | Committed, re-runnable batteries — one directory per contract, each with a self-documenting `run.ps1` that exits nonzero on a miss. A quarantined battery keeps its directory but loses its runner: what remains is a README recording what it proved and why it was retired. |
| [Reviews](reviews/) | Dated measurements and audits. Each is a record of what was true when it ran, never a plan. |
| [Acknowledgments](ACKNOWLEDGMENTS.md) | Source provenance, licensing notices, and credit. |

The maths research corpus — the polynomial-tail / Beatty / metallic-mean /
parity-irreducibility program, with its theorems, certificates, verifiers and
Lean project — lives OUTSIDE this repository and carries its own `MANIFEST.md`
there. This repository holds no link to it, because a path outside the tree
cannot be checked and a stale one reads as a missing file. The production
primitives it yielded are in `src/Puck.Maths`.

## Component handoffs

Detailed subsystem usage belongs beside the code:

- [`Puck.World`](../src/Puck.World/README.md) — the world game's three-project split; it links its siblings:
  - [`Puck.World.Data`](../src/Puck.World.Data/README.md)
  - [`Puck.World.Server`](../src/Puck.World.Server/README.md)
- [`Puck.Input`](../src/Puck.Input/README.md)
- [`Puck.DirectX`](../src/Puck.DirectX/README.md)
- [`Puck.SdfVm` shaders](../src/Puck.SdfVm/Assets/Shaders/README.md)
- [`Puck.HumbleGamingBrick.Post`](../src/Puck.HumbleGamingBrick.Post/README.md)
- [`Puck.AdvancedGamingBrick.Post`](../src/Puck.AdvancedGamingBrick.Post/README.md)

## What was deleted, and why it is not coming back

**2026-08-02** — roughly two dozen plan, catalog and survey documents, each
describing work sequenced out of `experimental/` or asserting a verification
status `Puck.Post`'s quarantine had made false.

**2026-08-10** — `capability-channels-plan.md`, `capability-channels-STATE.md`
and `design/navigation-field-spike.md`. Their decisions moved into the code they
govern; what survived as WORK is in [the campaign](campaign.md)'s work list.

In both cases the capabilities did not move anywhere: where a document is gone,
either the capability is absent from the running product and nothing plans its
return, or the campaign carries the remaining work by name. **Do not reconstruct
a deleted plan from Git history and present it as current.**

## Maintenance

- Update a document when its code contract changes, in the SAME commit.
- Never add a status column, a capability catalog, or a register.
- State a design document's falsifying premise as a re-runnable check.
- Remove completed rollout steps, dated progress logs, and superseded plans.
- Preserve measurements only when they still explain a current threshold or decision.
- Preserve source provenance, licensing notices, and research citations.
- Add new top-level documents to this index.
- Never link a document that does not exist.
