# Demo-dependency ledger (docs) — 2026-07-18

Owner trajectory: `Puck.Demo`'s work is being steadily refactored/ported into
`Puck.World` (or library projects) and the Demo will eventually be fully
removed. This ledger records, for the **living reference docs** (not the Demo's
own plans, which die with it), every place a doc that is NOT about the Demo
leans on Demo-specific machinery to describe or verify an ENGINE capability.
When the Demo goes, each row needs a new home (usually Puck.World or a library)
or deletion.

This is a ledger, not a plan — no work is scheduled here. It is a staleness
pass; nothing was deleted. Scope: `docs/**` + root markdown (Agent DOCS lane).
`src/**` comments are Agent CODE's parallel ledger, not covered here.

## How to read a row

- **Doc → section**: the living doc and the claim.
- **Leans on**: the Demo-specific surface the claim describes/verifies.
- **Destination when the Demo ports**: where the capability's description and
  proof most naturally re-home. "World" = the claim already has a Puck.World
  equivalent or should grow one; "library" = the capability lives in a shared
  library and only its *demonstration* is Demo-bound.

## Ledger

| Doc → section | Leans on (Demo machinery) | Destination when the Demo ports |
|---|---|---|
| capability-catalog → CLI-to-document funnel | `Puck.Demo` launch options synthesize `--run` document records | Library capability (`Puck.Scene`); re-attribute the flag→document example to whichever composition root survives (World's `--world` funnel is the live analogue) |
| capability-catalog → JSON Schema | `Puck.Demo --emit-schema` generates `schema/run.schema.json` | Move the emit entry point to a surviving root or a `tools/` generator; the schema + run-document battery are library-owned already |
| capability-catalog → Graph intents | `overworld` graph intent (the Demo experience) | `world` intent survives in Puck.SdfVm; the `overworld` intent description retires with the Demo |
| capability-catalog → Direct ROM boot | `--rom` synthesizes an immersed overworld document | Re-home to World if World gains a direct-boot funnel; else retire with the overworld |
| capability-catalog → Frame capture | "Demo console scripts can settle, step, and capture" | Re-attribute to World's console (same registry) or Puck.Post; the capability is `Puck.Capture` library-owned |
| capability-catalog → Persisted replay proofs | Demo replay verbs + overworld tapes | World already owns the record/replay seams; re-point the proof to a World or Post surface |
| capability-catalog → Deterministic garden and RTS proofs | Demo simulation pools (garden, RTS) | Port the proof subject into World or a determinism battery; `IWorldQuery` is library-owned |
| capability-catalog → Field-gravity proof | Demo planetoid walker | Port into World's fixed-point locomotion (World already has field-driven bodies) or a Post stage |
| capability-catalog → Overworld demo | The entire default demo (cabinets, creator, link, layouts) | Retires with the Demo; the surviving capabilities re-list under World rows |
| capability-catalog → Diegetic control plane / Replay museum / Creator | Overworld console, Droste-door exhibit, in-engine editor | Editor/creator is the named editor-arc port target (World); the museum exhibit retires or re-homes to World |
| capability-catalog → SDF-to-brick bake / PBAK / SM83 framework / Forged games / Avatar & world-lens forge / Audio proof | `Puck.Demo/Forge/` (bake pipeline, framework, forge, avatar) | The forge is the largest Demo-resident subsystem; port to a `Puck.Forge`-class library (see `rom-forge` skill) so these rows survive the Demo |
| capability-catalog → Neutral audio capability | "The Demo's per-core dual audio path collapsed onto this one contract" | Drop the Demo clause once ported; the contract itself is `Puck.Hosting`-owned and stays |
| project-map → Puck.Demo row + dependency rule 4/5 | Puck.Demo as a composition root consuming both cores | Rule text survives (World is the other root); delete the Puck.Demo row and its rule mentions on removal |
| agent-guide → verification workflow | `dotnet run --project src/Puck.Demo …`; "the demo console is the scriptable control plane"; `docs/examples/scripts/` | Re-point the "run the composition root to verify" guidance to World alone; the console-as-control-plane doctrine is not Demo-specific and stays |
| docs/README → Demo and content section; project handoffs (`Puck.Demo`) | overworld-demo-plan.md, game-studio-plan.md, `src/Puck.Demo/README.md` | Index entries retire with their targets; game-studio/creator content re-homes to the editor arc's docs |

## Notes

- The four orientation docs (project-map, capability-catalog, agent-guide,
  docs/README) are the highest-value re-home targets — they are contractually
  kept current, so a Demo removal must sweep them in the same change.
- `docs/overworld-demo-plan.md`, `docs/game-studio-plan.md`, and
  `src/Puck.Demo/README.md` are Demo-owned and are expected to die with the
  Demo; they are intentionally omitted from the rows above (a ledger of
  *non-Demo* docs leaning on Demo machinery).
- The forge cluster (bake pipeline, SM83 framework, forged games) is the
  single largest capability-catalog surface that is Demo-resident today and
  will need a real library home, not just re-attribution, when the port comes.
