# Puck.World verb census — classification ledger

Worktree `.claude/worktrees/verbs-census`, branch `worktree-verbs-census`, base **b5f14dcb**
(note: `origin/main` is 3cfe27fb and is STALE — it predates the split of `Puck.Cli`,
`Puck.World.Data`, `Puck.World.Server` and still carries `src/Puck.Demo|Post|Bench`.
The live trunk is `features/maths-excursion`.)

## Phase 1 — enumeration: the total is 353, and it is provably total

Two independent sources, cross-checked.

**(a) Runtime ground truth.** `help` piped into a boot, both streams captured.

| boot | registered names |
|---|---|
| headless, `play` | 213 |
| windowed 640×480, `play` | **353** |
| headless `dive` / `kart` / `jump` | 211 / 213 / 213 |

Headless is a strict subset of windowed (windowed-minus-headless = 140, headless-minus-windowed = **0**).
Across the four shipped worlds the only variation is 3 generated `channel.name.*` verbs
(`boost`, `burst`, `drift`) that dive/kart declare and play does not — so the union over
every shipped world is **356**.

**(b) Machine-readable cross-check.** `world.affordances` emits **350** commands with
routing/bindability. 350 + the three registry built-ins (`help`, `wire.ack`, `wire.errors`,
which the manifest deliberately excludes as never-bindable) = **353**. Exact match, no diff.

**(c) Totality argument.** `CommandRegistry` aggregates every `ICommandModule` at construction
and throws on a duplicate name. There are exactly two composition tiers —
`WorldBootComposition.AddWorldAuthoritativeCore` (always) and `AddWorldPresentation`
(only when `WorldHostSettings.Headless` is false) — and every `AddSingleton<ICommandModule,…>`
in both sits at method top level with no further condition. Both tiers were exercised.
No boot can register a name neither run saw.

**Aliases: exactly one in the whole `src/` tree** — `exit` → `quit`
(`Puck.Launcher/Commands/TerminalCommandModule.cs:14`). Aliases do not appear in `help`.

### Correction to the console reference
`references/console.md` says "Two definition factories only". True at the
`CommandDefinition` level, but `Puck.World` adds two wrappers —
`WorldCommandDefinition.Simulation` and `WorldCommandDefinition.Row<T>` — and a static sweep
that stops at `CommandDefinition.Verb|WithWireArgs` misses 69 registration sites.
Doc fix owed in the deletion wave.

## Phase 2 — classification

| class | count |
|---|---|
| MUTATION DOOR / bound control | 189 |
| READ-BACK | 62 |
| **SUGAR (tier 1+2 kill list)** | **69** |
| GENERATED (one per declared channel) | 15 |
| ENGINE I/O | 15 |
| BUILT-IN (registry infrastructure) | 3 |
| **total** | **353** |

Tier 3 (49 more) is drawn from the DOOR bucket — see below.

### Maximum honest reduction

| | killed | added | net | surface |
|---|---|---|---|---|
| tier 1 — pure sugar, no design change | 37 | 0 | **−37** | 316 |
| tier 2 — collapse to verb + subcommand token | 32 | 4 | **−28** | 288 |
| tier 3 — the document row/section family | 49 | 2 | **−47** | **241** |
| **all three** | **118** | **6** | **−112 (−31.7 %)** | **241** |

## The kill list

### Tier 1 — 37 verbs, no design change needed

**A. Self-declared "Console sugar" RMW (3)** — the description literally says so.
`world.host.tune`, `world.kit.tune`, `world.look.tune` → the matching `*.set <json>`.

**B. RMW field wrapper over a whole-row upsert (11).**
`world.collision.{gradient,requirements,skin,slope}` → `world.collision <json>`;
`world.kit.{collider,program,response}` → `world.kit.set <json>`;
`world.placement.{face,inhabit}` → `world.placement.set <json>`;
`world.hud.element.{set,remove}` → `world.hud.panel.set <json>` (elements ride the panel row).
This is the ledgered stale-read race class — killing it retires that defect-ledger entry.

**C. Door-not-type split (1).** `world.state.cell.text` → widen `world.state.cell.set` to
dispatch on the row's declared kind. Two verbs exist only because the operand is text.

**D. Stepped/cycled twin of an arg-taking verb (21).**
`editor.sculpt.zoom.{in,out}` (the target verb *already* accepts `in|out|<distance>` — a pure
alias today), `editor.sculpt.smooth.{up,down}`, `editor.sculpt.material.{next,prev}`,
`editor.sculpt.{grow,shrink}`, `editor.sculpt.frame.{next,prev}`, `editor.sculpt.{next,prev}`,
`editor.{next,prev,deselect}`, `editor.{fly,orbit}`, `editor.{faster,slower}`,
`editor.sculpt.chain.define`, `player.run` (→ `player.fly f s 0 turn 0 0 sec`, a strict subset).

**L. Relative twin of an absolute verb (1).** `editor.nudge` → `editor.move`.

### Tier 2 — 32 killed, 4 new verbs

**E (2)** `player.face`, `player.warp` → widen `player.pose` with `-` = hold-current.
**F (9→3)** `screen.{camera,capture,desktop,qr,view}` → `screen.source <index> <kind> …`;
`view.{camera,layout}` → `view.override <kind> <name|auto>`;
`world.{kit,look}.assign` → `world.assign <section> …`.
**G (7→0)** `world.instance.seat.{enter,leave,face,run,stop,warp,where}` — a per-target
duplicate of the `player.*` surface. Widen `player.*` with an `instance:<name>` target token.
**H (1)** `identity.deliver` — a dev harness for the door `chat.whisper` owns. Its only extra
ability is forging an arbitrary source id, which the authority model denies everywhere else.
**Owner ruling wanted.**
**J (8→1)** `editor.sculpt.{bend,dilate,onion,twist,rotate,move,nudge,rename}` →
`editor.sculpt.set <field> <value…>` — the same verdict as `*.tune`, applied to the sculpt target.
**K (5)** `editor.speaker.{move,gain,channel,radius,delete}` — verified in source: all six
`editor.speaker.*` verbs submit only `UpsertSpeaker`/`RemoveSpeaker`, the exact mutations
`world.speaker.set`/`.remove` submit. Only `editor.speaker.place` has a real extra ability
(the editor focus point).

### Tier 3 — 49 → 2. The headline.

20 document sections each ship a `.set`/`.remove` pair of the identical shape
("Upserts a X row, whole-row, keyed by K, from one inline-JSON T") — 40 verbs — plus 9
keyless whole-section replace verbs (`world.audio.set`, `world.authoring.set`,
`world.collision`, `world.host.set`, `world.hud.defaults.set`, `world.input-hold.set`,
`world.motion.set`, `world.render.defaults`, `world.spawns.set`). All 49 collapse to
`world.row.set <section> <json>` + `world.row.remove <section> <key>`.

Two of the 40 use a token grammar rather than inline JSON (`world.grant.set`,
`world.property.set`) and need either a grammar change or a documented exception.

## Constraint findings — the deletion wave is far less constrained than feared

1. **No shipped binding document names any verb by string.** Every `"command"` field in all
   four worlds and both scenarios is `null` — they bind channels and roles instead. The
   engine-default document (`WorldDefaultBindings`/`WorldEditorBindings`) is C#, naming verbs
   by **constant** (`EditorCommandModule.StatusCommand`, …), so a rename or delete fails the
   **build**, not silently at recompose. The "binding pages lose rows" hazard does not apply
   to the shipped set.
2. **No `world.*` verb is bindable** — 0 of the 161. Tier 3 touches zero binding rows.
3. **A bound control receives a `CommandValue`, never arguments** — the snapshot dispatch
   builds `CommandContext(parse: null, text: null, value: entry.Value)`. But a `WithWireArgs`
   verb *is* dispatchable from a binding (it sees an empty `WireArgs` and reads
   `context.Value`) — `player.press` and the F1–F4 `player.claim` slot-in-the-value pattern are
   the in-repo precedent. **So every `.next`/`.prev`/`.up`/`.down` twin can fold onto its
   arg-taking verb with no new mechanism and no lost bindability.** 23 verbs on the kill list
   are bindable and all 23 are covered by this.
4. Replay tapes store command ids; supergreen says old tapes re-record. Say so in the landing.

## The proof oracle — established and validated by running

`world.save` writes the live document in canonical form (stable member order, invariant
numbers). Two forms of the same act must therefore produce **byte-identical** files. Run
form A in a fresh boot, form B in another, hash both.

Executed, headless, own `--state-dir` per run:

| run | line | `contactSkin` | sha256/16 | mutation | `wire.errors` |
|---|---|---|---|---|---|
| A | `world.collision.skin 0.05` | 0.05 | `8fb8cab7f80b7891` | `SetCollision applied` | 0 |
| B | `world.collision {…"contactSkin":0.05…}` | 0.05 | `8fb8cab7f80b7891` | `SetCollision applied` | 0 |
| C | `world.collision.skin 0.09` (control) | 0.09 | `fb40ecd6ff6b3a46` | `SetCollision applied` | 0 |

| run | line | `moveSpeed` | sha256/16 | mutation | `wire.errors` |
|---|---|---|---|---|---|
| D | `world.kit.tune promenader moveSpeed 7` | 7 | `b39e96d12be6c9b5` | `UpsertKit 'promenader'` | 0 |
| E | `world.kit.set <full row json>` | 7 | `b39e96d12be6c9b5` | `UpsertKit 'promenader'` | 0 |

A ≡ B and D ≡ E; C proves the oracle discriminates. Group A and group B are proven.
Run E also proves a `world.save` row **round-trips through its own `.set` verb** — which is
what makes the tier-3 general upsert feedable straight from a save.

**Group K, windowed** — the most contested entry, previously asserted from source only.
`play` authors no speakers, so all three boots share one deterministic prerequisite: the row
harvested from a `world.save` (`world.speaker.set <row>`), never `editor.speaker.place`,
whose focus point rides the drifting avatar pose.

| run | line under test | gain | sha256/16 | mutation | `wire.errors` |
|---|---|---|---|---|---|
| KA | `editor.speaker.gain probe 0.5 1` | 0.5 | `e6b6a4ddc75f2496` | `UpsertSpeaker 'probe'` | 0 |
| KB | `world.speaker.set {…"gain":0.5…}` | 0.5 | `e6b6a4ddc75f2496` | `UpsertSpeaker 'probe'` | 0 |
| KC | `editor.speaker.gain probe 0.75 1` (control) | 0.75 | `10a21d4bd7075f9a` | `UpsertSpeaker 'probe'` | 0 |

**`player.run` → `player.fly`, headless.** A leading `player.pose 0 0 0 0 0 0 1` makes the
comparison reproducible across boots despite the cross-process pacing caveat: it is a hard
teleport that clears accumulated idle drift, and consecutive Simulation lines drain into the
same snapshot, so the segment starts from an identical state each time.

| run | line under test | `player.where` after `world.wait 480` | `wire.errors` |
|---|---|---|---|
| rA | `player.run 1 0 0 1 1` | `pos=(0.00, 0.02, -4.95) yaw=0 pitch=0 roll=0` | 0 |
| rB | `player.fly 1 0 0 0 0 0 1 1` | `pos=(0.00, 0.02, -4.95) yaw=0 pitch=0 roll=0` | 0 |
| rC | `player.run 0.5 0 0 1 1` (control) | `pos=(0.00, 0.02, -2.49)` | 0 |

**Incidental control on the harness itself.** The stage-1 harvest run scored
`[wire.errors: 1 rejected]` on a malformed `editor.cam.pose 0 5 10 0 0 0 1` — the verb takes
`<x> <y> <z> [<yawDeg> <pitchDeg>] [seat]`, yaw and pitch only, no roll. The refusal was loud,
named, and counted, which is the evidence that these runs do not read green through a failure.

**Honest scope limit:** proof-by-running both forms is only possible where the replacement
already exists. That covers groups A, B, K, `player.run`→`player.fly`, and
`editor.sculpt.zoom.{in,out}` (a pure alias today). Groups C, E, F, G, J and tier 3 need
their widening to land first, so their proofs belong in the deletion wave, one per verb,
same oracle.

## Open items needing a ruling

1. **`identity.deliver`** — delete the forge-a-source-id harness, or keep it?
2. **Tier 3 scope** — is one general `world.row.set/remove` pair wanted, or should the
   per-section verbs stay for their per-section refusal-by-name?
3. **`world.grant.set`/`world.property.set`** token grammars under a general upsert.
4. **`channel.role.*` (6)** duplicate `channel.name.*` for the same underlying channels —
   `channel.role.MoveForward` and `channel.name.forward` carry an identical description
   because they resolve to the same channel. The role indirection looks retirable; not
   counted in any tier above because it is a `ChannelRef` design question, not a verb.
5. **Kits have no read-back verb** — there is no `world.kits`. Noted against the read-back
   rule; out of scope for a reduction pass but it is a gap.
