# World runtime consolidation

This plan proposes a consolidation of the world runtime around its existing
ownership boundaries. Rule writes remain simulation state, static geometry is
queried through a baked field instead of marched per query, and `WorldServer`
becomes a facade over owned objects. Runtime subsystems fold into the packages
that already own their vocabulary. **No new packages**—every fold has an
existing home whose declared purpose already covers it.

The plan records design decisions and remaining work. Check the owning source
for implementation status and the [game milestones](../development/game-milestones.md)
for dated verification. A difference between the plan and the implementation
needs an explicit decision about whether the plan is stale or work remains.

## Design comparisons

| Where | The idea | What it implies |
|---|---|---|
| Unreal, Godot, Quantum | Static geometry is baked once into a queryable grid; the authored field is consulted only where the bake says the surface is near; static colliders are never rebuilt at runtime. | A fixed-point distance grid over the authored solids, hashed into the collider census, with the exact program as the narrow-band refinement. Navigation and contact both query it. Deterministic because the bake is fixed-point and part of the census. |
| Every lockstep engine | The simulation emits events; formatting, logging, and metrics are subscribers outside the core. | Narration through `WorldOutputHub`, with the architecture gate's console denial armed against the compiled assembly rather than a source scan. |
| Ludii, RBG | Compiled evaluation and bitsets. | Not where the profile is. No emitted evaluation, no bit-packed cells, until a search-rollout measurement names them. |

## Remaining work

### `WorldServer` becomes a facade

Its partials cluster by the fields they touch, and each cluster becomes a type
in its own folder under `Puck.World.Server` while `WorldServer` keeps the public
API, the constructor, and the phase order.

| Object | Absorbs | Owns |
|---|---|---|
| `WorldDocument` | mutation compose and apply, generate, state-transform admission, admission | definition, base, journal, pending ops, preflight scopes, budget meter, solids, the delivery decision |
| `WorldTick` | step, contributions, channels, engagement, transfers, fields, music, responses, board enforcement, lattice draws | intents, channel fold scratch, federated intents, the music clock |
| `WorldRuleHost` | rule host, rule frame, queries, patterns, decisions, trace, diagnostics, flock affinities | evaluator, compiled rules, the rule frame, tables, latches |
| `WorldExtensions` | extensions, the recorded-extension epoch, the external-operation dispatcher and journal | epoch, suppression; the addon and machine hosts hang off it |
| `WorldGrants` | grants, ownership, the admission half of admission | grants, owner base, the drive-denied latch |
| `WorldPersistence` | checkpoint, state hash, replay | nothing; it walks the others |

Take the pipeline first, then the tick, then the rule host, then grants and
persistence. Bodies stay where they are behind `IWorldGrantsView` and handles,
so nothing under `Bodies/` names `WorldServer`.

Two folds stopped short for a reason worth keeping: the federation and
checkpoint codecs encode records nested inside Server runtime classes, and the
replay describers are called from `WorldReplaySnapshot`. Un-nesting those
records is this facade's job, not a codec move.

### Maintenance constraints

- **One deadline primitive.** The expiry sweeps for escrows, transfer escrows,
  contribution tenure, and parks become one deadline table the tick sweeps once,
  without LINQ. This is also where the quiet tick's remaining allocation lives.
- **Comment ledger.** A recorded ledger for `puck scan -Only comment-smells`,
  counts per file that may only shrink, swept folder by folder as the facade
  touches each one.
- **File-length ceiling.** Lower it once the facade has split the largest files.

### Composed-game acceptance check

Two chess instances in one world from one rules fragment, one moved by hand on
the table and one driven by text, producing equivalent accepted histories while
their physical motion and presentation differ; then two solitaire tables at
once; then poker with two participant identities in the same roles. That check
demonstrates an author can change how a game is encountered without touching its
adjudication.

Most games belong in their own documents or as basis deltas over the island,
with small rule-work footprints and fast boots. A shared game room—a market
tabletop, an arcade hall—composes several game modules under `imports[].as`
into one shared diegetic space. The shipped island combines more systems than a typical shared game room.
That composition exposes interactions between the systems and tests their
combined budgets. A capacity failure needs accurate work pricing or a general
implementation correction; a game-specific bypass would hide the constraint.

## Scope limits

- No new packages.
- No rewrite of `WorldBody`. Its existing interfaces preserve its ownership boundary.
- No change to the name grammar. A namespace is a property of an import, never a
  character inside a name.
- No preservation of retired journal or tape shapes. Re-record and delete the old
  path.
- No emitted evaluation or bit-packed cells until a measurement names them.
- The studio's simulation evaluator is single-homed and authoritative: the
  browser hosts the same engine through WebAssembly rather than a second
  implementation.

## Sequence

Each wave is wide and file-disjoint so it integrates in one pass, and each is
held against the game's acceptance playthrough: a definitive playthrough from the
studio's front door through the arena, the arcade hearth, the plaza, and off the
island's edge onto a shard.

- **The content wave.** The studio prologue's acts, the arena crawl's spawners
  and bosses, the arcade hearth's seam content, the retail basis deltas that pin
  a boot seat and district behind a fact, the chance level's hidden operands.
  Author and verify against four eager local seats from the start, so no
  single-player bias reaches camera rigs, loot, or input arbitration. Held by the
  playthrough, played, with its captures.
- **The federation wave.** Provenance signing for carried state, the bilateral
  attestation rows a duel or wager is, a profile world docked at a shard's seam,
  then the silo-hosted hub. The facade and the remaining maintenance constraints ride behind it.
  Held by the four-corners stressor across authorities and the attestation laws.

A wave lands on the working branch after a review with the owner and reaches
`main` as one squash with a hand-written summary. `puck landing` is the merge
gate: its git-loss check and its automatic canary set.
