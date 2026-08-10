# AGENTS.md

Puck is an **everything-as-data** engine: versioned JSON documents describe what
runs, and the engine renders, composites, validates, and replays them
deterministically on either GPU backend — Vulkan or Direct3D 12. The live game
is `Puck.World`, whose document is `puck.world.def.v1` — the world itself, and,
seeded from it, one per owned identity. It carries the `Extensions` round-trip
convention (`Puck.Abstractions.Documents.DocumentExtensionsPolicy`).
It is a deliberately dumb terminal *beneath* engines; where it ends up is left
open on purpose.

## Enforcement

The build, the architecture gate, determinism checks, and calibrated ceilings
are enforced. **POST is quarantined as of 2026-08-02** — `Puck.Post` is in
`experimental/` and out of the build, so nothing it used to gate is enforced
today; do not cite it, run it, or write a stage for it. The engine-contract
verification story it carried is a live gap, not a live gate. Presentation-only float and artistic
work remain outside the simulation-state determinism contract.

Enforcement covers **observable behavior** — pixels, hashes, parity, determinism
— and says nothing about the design being settled. Puck has never shipped and
has no consumers. Every name, shape, document, and ABI is free to change; a gate
that fails because a deliberate correction moved a hash gets re-recorded in the
same change, never worked around. A label calling something frozen, closed, or
versioned is a description of what it is today, never a reason to leave it that
way (rules 2 and 5).

## Orientation

These are kept current — read them before deep work.

| Doc | Answers |
|---|---|
| [docs/project-map.md](docs/project-map.md) | What each `Puck.*` project is for, how they layer, the dependency rules. Its layering block is GENERATED from per-project declarations (`puck architecture --map`) — do not hand-edit it. |
| [docs/agent-guide.md](docs/agent-guide.md) | How to verify, env vars, hardware gotchas, conventions. **Read before touching GPU or emulator code.** |
| [docs/README.md](docs/README.md) | Index of what remains. |

**There is no capability catalog or register any more, deliberately.** Both
claimed per-capability *verification status*, and with `Puck.Post` quarantined
that column was false — a document consulted precisely to decide whether
something is safe, asserting coverage that does not exist. A catalog that can
drift from the code is worse than asking the code. **Do not recreate one as
prose.** If an inventory is wanted, generate it (`puck` already derives the
layering block this way) and give it a runner that fails when it disagrees with
its source — a generator nobody runs is a hand-maintained file with extra steps.

The same test governs every document here: if acting on it would produce the
wrong behavior today it is not stale, it is hostile — delete it. Keep what
records a DECISION and the reasoning that cannot be re-derived. Generate what
the code already knows.

For an area's settled contract facts, load its skill: `sdf-world`,
`gaming-bricks`, `rom-forge`, `symbol-analysis`, `maths-usage`,
`maths-laws`, `documentation`, `boy-scout`. There is deliberately no
verification-routing skill: the one that existed described `Puck.Post` and was
deleted with it. The two emulator batteries (`gaming-bricks`),
`tests/Puck.Maths.Tests` (`maths-laws`), and the narrow deterministic
real-World canaries run by `puck landing` are the live gates.

## `InternalsVisibleTo` is not endorsed — publicity is the better option

**Owner ruling, 2026-08-02.** Reaching for `InternalsVisibleTo` is a signal you
have the wrong accessibility, not a solution to it: if another project needs a
member, **make the member public**. A TEST project is the one arguable
exception. This holds in both of IVT's forms — the `Properties/AssemblyInfo.cs`
attribute and the csproj `<InternalsVisibleTo>` item — and scanning for one form
alone returns a confident wrong answer, measured by getting it wrong.

Widen the member, not the assembly: a grant hands a whole assembly's internals
to a friend forever, which is strictly more than the caller needed and invisible
at the call site. If a member looks wrong to make public, that is evidence about
the design — say so rather than reaching for a grant to avoid the question.

## `experimental/` is a reference tree, not a sealed one

**Owner ruling, 2026-08-08, superseding the 2026-08-02 blanket ban.** The
quarantine governs *work*, not *reading*. Under `experimental/` you are
expected to READ the source and CITE it as prior art, and to DELETE code there
once live code has eclipsed it. You may NOT improve it, fix it, build it, run
it, or run its tests. Expect its builds to break as deletions land — that is
the intended outcome, not a regression to repair. The trees hold `Puck.Demo`,
`Puck.Post`, `tools/`, both `scripts/` trees, and `Puck.BareMetal`; each
carries a firewall pair so the root build cannot reach it either. See
[experimental/README.md](experimental/README.md).

Read it the way you read git history: evidence of how a problem was solved
once, never a precedent that binds, and never something to revive in place.
Anything there that must keep working belongs in a real project or a `puck`
verb, rewritten under the gate and verified by running.

**Retiring eclipsed code.** The deletion rides in the SAME squash as the
landing that eclipses it, so the evidence sits beside the removal and every
deletion line stays accounted for. "Eclipsed" is a claim that needs a
mechanical check behind it, not an impression — bring it to the lead for a
decision rather than deciding alone. Documents that still cite the old
`tools/…`, `src/Puck.World/scripts/…`, or `src/Puck.Post` paths are STALE;
correct them where they live.

## Core rules

1. **Split `Puck.*` projects only.** Every feature lives in the split projects;
   `src/Puck` and `src/Puck.Avatars` exist only in git history. Never reference
   those paths.
2. **The current instruction outranks every artifact.** Docs, skills, gates,
   comments, and precedent are evidence, not law — if one argues against a
   change you've been asked to make, it is stale; update it in the same change
   rather than watering the change down. Gates prove *observable* behavior
   (pixels, hashes, parity, determinism), never internal structure. Full
   doctrine: [agent-guide.md](docs/agent-guide.md#engineering-doctrine).
3. **The game is greenfield; Post gates the engine.** `Puck.World` — the
   overworld and everything under `src/Puck.World/` — is the playground: expected
   to churn, never settled precedent. (`Puck.Demo` is **quarantined at
   `experimental/Puck.Demo`** by owner ruling 2026-08-01 — out of the solution
   and the root build. READ it as prior art and retire it as it is eclipsed,
   per `experimental/` above; never build, run, fix, or revive it in place.
   Capabilities that once lived only there are simply absent from
   `Puck.World`, and no plan of record sequences bringing them over.) Verify
   game/overworld changes by RUNNING `Puck.World`
   (`dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2`;
   0 or less runs until the window is closed). Narrow deterministic headless
   canaries MAY gate `puck landing` only by launching that real executable and
   observing its normal stdin/stdout/stderr contract; never substitute a
   build-only gate, add a `--validate-*` flag, or add a Post stage for a game
   feature. **`Puck.Post` is QUARANTINED** (`experimental/Puck.Post`, 2026-08-02)
   — it is not built, not run, and not cited; the shared engine contract it
   used to gate (cross-backend render path, SDF VM ISA, document schemas,
   deterministic numerics) currently has no gate at all. Say that plainly when
   it matters rather than implying coverage. Emulator changes use the
   `Puck.HumbleGamingBrick.Post`/`Puck.AdvancedGamingBrick.Post` batteries,
   which are still in the build.
4. **Determinism is a feature — it pins the mapping, not the values.** No
   wall-clock, RNG, or float in simulation state; input becomes per-tick
   `CommandSnapshot`s; fixed-point math comes from `Puck.Maths`. The guarantee
   is reproducibility at a fixed code version: same document + same input →
   bit-identical state on every run, machine, and backend. It is NOT output
   stability across code versions — a deliberate correction to math or logic
   is EXPECTED to change state hashes. When one does: make the correction,
   re-run the relevant Post tier to prove determinism still holds (the gates
   are self-referential; they pin no historical values), and re-record any
   persisted replays or baselines the correction invalidates in the same
   change. Never preserve a wrong result to keep a hash stable, and never add
   a path that reproduces old-wrong behavior.
5. **Supergreen — zero consumers.** Nothing outside this repository consumes
   Puck: no published packages, no downstream repos, no users of its APIs.
   Backwards compatibility is a non-goal — never raise it as a concern, and
   never let it shape a change. Rename, reshape, and delete freely, updating
   every internal caller in the same change. No compat aliases, no
   deprecation ceremonies, no migration shims, no read-side tolerance for
   retired data shapes — migrate data once and delete the old path. The only
   stability contract is observable behavior under the gates.
6. **Merges** land on `main` as one squash commit with a hand-written summary —
   no WIP noise, no merge bubbles, no `Co-Authored-By` trailers.
7. **Branded code is settled — changing it is a deliberate act, not a silent
   one.** A member carrying `[VerifiedCode("id", …)]` has been proven correct
   over its whole input space, and `VerifiedCode.json` seals the source that
   proof was read against: the member's own declaration, plus the declarations
   its entry names under `dependencies` — the constants it reads, the
   representation it is written against. Exactly one level, listed by hand.
   What a dependency in turn rests on is outside the seal, and so is anything
   the entry does not name, so the list is part of what a re-verification
   decides. Edit anything inside it and the build fails with **VER001**, quoting
   the recomputed hash. That is not a wall: it is a checkpoint. If the change is
   right, re-establish the brand's basis, paste the new hash into the manifest,
   and say in the commit why the member is still correct. If the member should
   no longer be branded, delete the attribute AND its manifest entry together —
   dropping only one raises VER002. **Never make VER001 go away by deleting the
   attribute to unblock a build**; that discards a proof someone earned and
   leaves no trace that it was discarded. The manifest records a `basis` —
   `exhaustive`, `exact-by-construction`, or `exact-by-proof` — and an entry
   resting on proof alone carries the argument it rests on, so read that before
   deciding the brand still holds. VER003 means the fingerprint cannot cover the
   declaration's shape honestly — `partial`, a preprocessor directive, or a brand
   that does not sit inside what it brands; restructure rather than suppress.
   The rest of the family exists so a brand can never stand unenforced: VER006
   refuses the ledger itself when it is missing, unreadable, off-schema,
   ambiguous, or carries an entry that cannot be trusted — a broken manifest
   fails the build on the manifest, never by passing as an empty one; VER007
   refuses a brand where nothing can record it (a local function or lambda);
   VER008 refuses a declaration claiming an entry recorded for another symbol;
   VER009 refuses an entry claimed more than once; and VER010 refuses an entry
   naming a dependency that resolves to nothing, to more than one declaration,
   or to a shape the walk cannot cover — folding it as nothing would leave the
   seal narrower than the entry claims. Each entry records the `assembly` that
   owns it, and that assembly's compilation is the one that sweeps it.
8. **Assume the system already exists; find it before building it.** This
   repository is deep and much of it is settled, so a "new" mechanism is
   usually an existing one wearing a different name. Before authoring, load
   the skill that owns the area — `sdf-world`, `puck-world`, `maths-usage`,
   `maths-laws`, `gaming-bricks`, `rom-forge`, `symbol-analysis`,
   `content-search`, `documentation`, `boy-scout` — and then ask the CODE with
   a mechanical control (`puck references`, `puck declarations`,
   `puck search -M 0`) rather than guessing from a name. `experimental/` is
   one of the places to look. A second implementation of something already
   here is a defect, not a feature; a skill that proves wrong about its own
   area is stale, and gets corrected in the same change (rule 2).

## The overworld — product intent

> **STATUS: INTENT, NOT A DESCRIPTION OF ANYTHING THAT RUNS.** Nothing in this
> section describes shipped behavior. It records the experience `Puck.World` is
> meant to become, so the target is not lost. **No plan of record sequences it.**
> The document that used to — the Demo → World port plan — was deleted when
> `Puck.Demo` was quarantined, and nothing replaced it; there is no arc list, no
> ordering, and no owner for the gap between this intent and the code. For what
> actually exists today, read [src/Puck.World/README.md](src/Puck.World/README.md)
> and the code, and verify by running `Puck.World`. Never cite this section as
> evidence that a capability is built.

**The four-world charter (owner-ratified, superseding the bootable-cabinet
arcade room this section described before 2026-08-06 — read that design as
history, not destination).** Puck's first official game is FOUR worlds, no
others: **Play**, the overworld hub — it IS the MMO, the game's first main
city, local multiplayer (four seats) in a plaza sized for up to
`WorldPopulationLimits.CapacityCeiling` occupants — and three instanced
"dungeons" reached from it: **Dive** (underwater), **Kart** (racing),
**Jump** (platformer).
Each dungeon is reached through a wall-mounted picture-frame portal standing
in Play, never a loading menu or a `--flag` mode swap — walk to a frame, and
the world underneath changes with no restart. Play's own `references` section
names the three dungeons by document path; a portal placement resolves its
frame against the matching row (the `puck-world` skill's documents reference
carries the section's exact shape). Design is feel-first: a
dungeon earns its place by how it feels to move through — before any scoring,
unlock, or progression system is layered on top — and Play's own feel is
gentler than any dungeon's, a promenading pace fit for a shared plaza.
Reveals are a core world mechanic — attunement-like achievement facts
carried on the identity, general enough for cross-game unlocks between
trusted servers. Every world is a starting point; all starting points
converge on the hub. The dmg↔cgb↔agb device swap, forged cartridges (SDF
art, camera, hand-authored SM83 games), and creator mode await their places
in that reveal graph — an arcade cabinet already stands dark in Play's
plaza as the first of them; no plan of record sequences the rest.

The charter bounds the GAME worlds, not the `Assets/worlds/` directory.
`studio` ships beside them as a non-game DEV CANVAS for character and
creation work — a neutral floor, no scenery, no crowd, and four anchored
camera eyes with a `sheet` layout that composes front/three-quarter/side/back
at once. It is reached with `--world`, and — owner amendment, 2026-08-09 —
it is also Puck's FIRST FORMAL BORDER CROSSING: play and studio meet at a
mapped border (the zone-stitching proof), so studio is additionally
reachable by walking out of the plaza. It remains a dev canvas, not a game
world, and not a destination in the reveal graph. A doc counting "the four
shipped worlds" is counting the charter's roster; the directory holds five
documents.

**The unification contract (the north star).** The overworld is ONE cohesive
experience, not a menu of `--flag` modes: every capability is reachable from
inside a single running session — a diegetic act, a pad chord, or a **console
verb** — with no restart. The **console is the control plane**, driven by the
on-screen panel AND process **stdin** with results echoed to **stdout**, so an
agent (or a deterministic test) scripts the whole engine over a pipe. Durable
configuration lives in the world document (`puck.world.def.v1`) — which world
a boot resolves to, and each portal's dungeon target (the `references`
section). There is no `PUCK_*` configuration surface: durable values are
document fields, live operations are console verbs, and a headless flag is
only ever a developer reflection of an in-session capability, never a separate
product.

Boot loads directly into Play, standing in the plaza; later, a diegetic
reveal unlocks the editor (which stays always-on for devs/agents). No gating
code for this reveal exists.

The in-engine game studio roadmap and the machine-fleet workload/performance
briefing were deleted with the rest of the plan documents; neither the studio
nor the fleet has a plan of record today.

## Controller input

Switch Pro / Xbox Series / DualSense, all flowing through `Puck.Commands`, live
in `src/Puck.Input`. Its [README](src/Puck.Input/README.md) is the handoff doc —
architecture, cross-family feature matrix, hardware-verified status, deferred
work, debugging notes.
