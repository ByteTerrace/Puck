---
name: puck-world
description: Guides work on Puck.World across its document and Protocol model, authoritative server simulation, composition root, mutation and authority systems, ordered submissions, HUD and views, engagement and session lifecycles, addons, replay, and console verbs. Use whenever changing or diagnosing any src/Puck.World* project, especially console verbs, mutation kinds, document sections, grants or refusals, HUD or view bindings, addon or replay behavior, and client/server seams. Also use before writing stdin-driven game verification because it defines the supported run recipes and encoding, indexing, collision, drain, screenshot, and replay-proof constraints.
---

# Puck.World: the game of many games

Keep this skill factual and procedural: record settled contracts, their exact
seams, and how to verify them. Let the user's current instruction outrank this
file. If the skill contradicts a demanded change, update it in the same change.
Treat counts, inventories, quarantine status, and other repository-state claims
as snapshots: verify them against the current tree before relying on them.

## The model in one paragraph

Treat everything as data: versioned JSON documents (`puck.world.def.v1` — the world
itself, and, seeded from it, one per owned identity) describe what runs; the
engine renders, composites,
validates, and replays them deterministically. The world is ONE bootable
experience — no sibling `--flag` modes; durable configuration is document
fields, live operation is console verbs, and there is no `PUCK_*`
configuration surface for this game. **A baked C# constant is the same
violation as a flag, and the commonest one** (owner ruling, 2026-08-03,
re-issued 2026-08-07): the discriminator is whether Play, Dive, Kart, and Jump
would each want the value different — sensitivities, clamps, radii, timings,
speeds, which button arms a mode. If yes, it is a document field in its FIRST
commit, never a constant to migrate later. Before writing any feature carrying
a tunable number, search `src/Puck.World.Data` for existing vocabulary: the
2026-08-07 relapse built a bespoke mouse-orbit with hardcoded sensitivity and
pitch clamps while `WorldCameraMotion.Orbit` and the authored `views.seatRig`
already existed. Legitimate constants: capacity bounds that size memory or the
wire, representation/determinism constants, and math. The console is the
control plane:
process stdin drives verbs, stdout/stderr echo results, and the on-screen
console is only a MIRROR of that pipe — nothing that draws (including a HUD
`replace` panel taking over the whole overlay) can take the control plane
away. Verify game behavior by RUNNING the game, never by a build gate
(`CLAUDE.md` rule 3).

## The three projects

| Project | Owns | Key types |
|---|---|---|
| `src/Puck.World.Data` | The document model and the whole Protocol wire surface | `WorldDefinition` + section records, `WorldDefinitionValidator`, `WorldDefinitionSerialization`; `Protocol/`: `PlayerIntent`, `WorldCommand`, `WorldMutation`, `WorldGrant`/`WorldPrincipal`, `SubmissionEnvelope`, `SessionRequest`, `WorldSnapshot`, `IServerLink`/`IClientSink`/`IWorldServerHost`, `LoopbackTransport` |
| `src/Puck.World.Server` | The authoritative sim | `WorldServer` (the tick, the journal), `WorldGrants`, `WorldHandleTable`, `WorldPopulation`/`WorldBody`, `WorldEngagement`, `WorldMachineHost`, `WorldAddonRuntime`, `WorldOwnedWorlds` (the owned-world identity catalog), `WorldReplayTape`, `WorldOutputHub` |
| `src/Puck.World` | The sole composition root | `Program.cs`, the client (`Client/`), presentation and the screen-output binder, `Audio/`, every `*CommandModule`, `Assets/` (four shipped worlds) |

Dependency rules are enforced by the architecture gate (`PUCKARCH`
diagnostics from `build/Architecture.props`): `Puck.World.Data` references
only `Puck.Abstractions`, `Puck.Commands`, `Puck.Forge`, and `Puck.Maths` —
structurally denied backends, presentation,
`Puck.Overlays`, `Puck.Input`, and `Puck.World.Server`. `Puck.World.Server`
adds `Puck.World.Data`, `Puck.Scripting.Simulation`, `Puck.Storage`,
`Puck.Hosting` — and knows nothing about rendering or input. The two seams
that legitimately cross: `BindingVocabularyHook` (a `[ModuleInitializer]`
injection so Data validators reach the input vocabulary), and hand-mirrored
constants in `Puck.Overlays.OverlayChannelLeases` (see
[references/hud.md](references/hud.md)). Each project's README is the
current developer reference — start there for narrative depth this skill
deliberately does not duplicate.

## Cross-cutting contracts (every task)

**Preserve determinism.** Use no wall clock, RNG, or float in simulation state;
use fixed point from `Puck.Maths` and exact engine-tick durations throughout; the
fixed simulation rate is 240 Hz. Every entity is advanced on the server from
a `PlayerIntent` — poses are never accepted from outside the simulation;
drivers only produce inputs, poses flow out through the tick snapshot. The
guarantee pins the MAPPING, not the values: a deliberate correction to math
or logic is EXPECTED to change replay hashes — make the correction and
re-record any persisted tape it invalidates in the same change. Client-side
(`src/Puck.World/Client/`) is presentation: floats are fine there, nothing
feeds back into the tick.

**Enforce the acting-principal rule.** Make every mutating ingress consult the ACTING
principal before any mutation. The ingress stamps identity
(`SubmissionEnvelope.Principal` on the wire, `CommandContext.Principal` for
console text); handlers READ the stamp via `context.ActingPrincipal()` and
never construct a principal — constructing one is laundering an identity.
Client code never mutates local state before the server's verdict
(completions, not discarded replies). Details:
[references/authority.md](references/authority.md).

**Add a read-back.** Do not land a new decision surface without a verb that
echoes it, in the same change — a decision nothing can echo can only be
asserted through downstream inference. `world.why`, `world.grants`,
`player.channels`, `world.hud`, `world.status`, `world.addons`,
`world.refusals` are the pattern.

**Keep parsing strict and sweep shipped worlds.** Refuse unmapped JSON members by name on
every nested row; only the document root's `Extensions` bag round-trips
reserved-prefix (`$`/`_`) keys. Adding a top-level section refuses at boot
until every shipped world carries it; adding a nested member silently
defaults at parse and (usually) refuses at validation — sweep the shipped
worlds in the same change either way. Precise direction:
[references/documents.md](references/documents.md).

**Adding an authorable feature — the five steps, one change.** The contracts
above are each stated separately; a feature carrying tunable values owes all of
them together, and skipping the binding is how a bespoke mechanism gets built
beside an existing one:
1. **Search `src/Puck.World.Data` for existing vocabulary first** — the record,
   the `$type` arm, or the section that already says this. Extending what exists
   beats a parallel mechanism, and the existing one is usually invisible from
   the call site you started at.
2. **Author the values as a document record** (never C# constants — see the
   model paragraph), with a `Default` carrying today's behavior so an
   unauthored world is unchanged.
3. **Validate** in `WorldDefinitionValidator`, refusing by name in the style of
   its neighbors.
4. **Sweep every shipped world** in the same change (strict parse, above).
5. **Add the read-back verb** (above) — the decision must be echoable.

**Doc hygiene, same commit.** `docs/capability-channels-STATE.md` must be
updated in the SAME commit as any landing that changes its truth (its own
maintenance rule). Component READMEs are developer references (no doctrine
prose); if a change stales one, or stales a comment, fix it in the same
change. A doc that would produce wrong behavior today is hostile, not stale —
delete it.

## Running and verifying

```
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds N --state-dir <tmp> < script.txt > out.log 2> err.log
```

- `--exit-after-seconds 0` (or absent) runs until the window closes. The
  full flag surface is parsed in `Program.cs` (`--backend`, `--width`,
  `--height`, `--exit-after-seconds`, `--present-mode`, `--world`,
  `--recording`, `--storage-uri`, `--user-id`, `--state-dir`, `--headless`,
  `--listen`, `--connect`); host-related flags are nullable deployment
  overrides. Absent host overrides leave the world document's `host`
  section in control.
- `--state-dir <dir>` redirects the on-disk state root (profile catalog,
  replays) — use a temp dir for hermetic verification runs; parallel runs
  each need their own.
- **Capture BOTH streams.** Read-back answers land on stdout; refusals,
  server narration, boot origin lines, and `[world.mutation: …]` echoes
  land on stderr. Reading one stream is reading half the conversation.
- Blank lines and `#` comments in the piped script are skipped — annotate
  your scripts.
- **The drain barrier**: a following `Immediate` verb is held until pending
  `Simulation` traffic applies, so write-then-read pairs need no polling.
  `world.wait <ticks>` is the explicit fence, clocked by completed
  simulation ticks (see [references/console.md](references/console.md)).
- **Encoding, the two traps**: a pwsh spawned from Git Bash reads captured
  output under an OEM codepage and mangles the engine's em-dashes
  (false-FAIL); pin `[Console]::OutputEncoding` and `$OutputEncoding` to
  UTF-8 — but BOM-LESS (`[System.Text.UTF8Encoding]::new($false)`): a
  BOM'd pin writes its preamble into the piped stdin and silently corrupts
  the FIRST command.
- **Indexing**: `world.grant body:<n>` is a 0-based entity index; `player.*`
  verbs are 1-based. `body:1` is "player 2".
- Scenery boulders HAVE collision — zero displacement with no refusal means
  the physical path, not a dead command. A zero-input boot drifts p1
  slightly (~(-0.04, 0, -0.82) over ~300 ticks) — do not assert exact rest
  poses without accounting for it.
- `world.screenshot <path.png>` REQUESTS the next composed frame including
  the overlay — the cheap pixel assertion. It arms; it does not capture:
  the stdout echo says `pending`, the file is announced on STDERR
  (`[capture] unified overlay -> …`), so **fence a frame (`world.wait`)
  before reading it**, and a second shot armed before the first composes is
  refused by name.
- **Use the repository's content search.** Run `puck search`, never `grep`;
  the published project tool is the repository's supported search surface.
- **A verification that cannot fail is a lie.** Pair every denial case with
  a control (actor holds the grant → succeeds), keep actor ≠ target (every
  seat is seeded wide, so self-targeting discriminates nothing), and prove
  a new assertion once by breaking it. This repo's recorded dominant
  failure mode is verification scripts that lie silently.
- `replay.verify` MATCH proves the authoritative pose trajectory only —
  nothing about document, grant-table, or HUD state
  ([references/replay.md](references/replay.md)).
- Committed batteries: the runners under `docs/verification/`
  (undo-all-or-nothing, strict-definition-parse, sdf-decode-sign-refusal,
  doc-links, addon-mutation-seam, four-world-boot-smoke) — re-run the ones
  your change touches. `ordered-domain`,
  `lane-present-deletion`, `hud-document`, `headless-boot`,
  `engagement-dissolution`, and `verification/authority` are QUARANTINED
  (`authority`/`engagement-dissolution` 2026-08-06, the four others
  2026-08-06 by the earlier owner ruling): their fixtures (or, for
  `hud-document`, its
  `scripts/sabotage/hud-skip-writer-emission.patch` context hunk) drift out
  of date at the repo's change rate faster than repair is worth —
  `headless-boot` had rotted to the point that its own sabotage phase no
  longer went red, failing identically at its base commit, so it was
  measuring nothing; `authority`'s cases 04-06 AND every phase of
  `engagement-dissolution` from (b) on assumed the retired `default` world's
  `screen:0` (a mounted addon too, for `authority`), which no shipped world
  authors today — `engagement-dissolution` was found broken the same way
  while re-verifying `authority`'s quarantine, its own dependency being
  implicit (`screen.insert 0 …`, no `--world` override) rather than a named
  file citation a text sweep would catch. Each `run.ps1` is now a stub that
  exits non-pass with a note; validate those contracts by RUNNING THE APP,
  not by the runner. `authority`'s successor is `tests/Puck.World.Tests`
  (`AuthorityAdministrationLawTests`, not yet in `Puck.slnx`) for the
  acting-principal/administration contract; an engage-authority law with
  code-built `testPattern`-screen furniture is chartered to follow there and
  is expected to absorb `engagement-dissolution`'s engage/disengage phases
  too (its tape/codec phases have no chartered successor yet — owed work).
  Do NOT create new persisted runner/battery artifacts without asking, and do
  NOT repair a rotted fixture — quarantine it with a note and move on
  (validation currency is run-the-app, owner-in-the-loop). Verify
  thoroughly; ask before committing new permanent verification
  infrastructure.

A minimal smoke session:

```
printf 'world.status\nplayer.where 1\nworld.grants console\n' |
  dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 6 --state-dir "$TMP/puck-state"
```

## Where state changes — the one pipeline

All durable change flows through the mutation substrate: a `WorldMutation`
buffers through the ordered domain, drains FIFO at the tick boundary,
composes a candidate → revalidates the WHOLE document → capacity-checks →
swaps atomically and rebuilds the changed derived state → journals →
delivers to clients. `world.undo` replays journal-minus-tail
through the same gates, all-or-nothing. Rendering derives from the
delivered definition on revision moves — a mutation's visual effect is a
side effect, never a draw call. The exact `WorldServer.Step` order, the
apply pipeline, the 64-kind catalog with declared ordinals, and the
add-a-kind procedure: [references/mutations.md](references/mutations.md).

## Subsystem index — working on X, read references/X.md

| Working on | Read |
|---|---|
| Document schema, serialization, validators, player profiles, binding layers, capacity constants | [references/documents.md](references/documents.md) |
| The tick order, mutation kinds/ordinals, journal/undo, adding a mutation kind end to end | [references/mutations.md](references/mutations.md) |
| Grants, principals, verdicts, co-driving fold/consent, budgets, handles, refusal catalog, `world.why` | [references/authority.md](references/authority.md) |
| `SubmissionEnvelope`, the one queue, completions, echo routing, the intent buffer | [references/ordered-domain.md](references/ordered-domain.md) |
| HUD schema caps, overlay reservation arithmetic, bands/`replace`, bindings, HUD verbs | [references/hud.md](references/hud.md) |
| Camera rigs (seat chase + named cameras), motion/aim/lens/`SmoothRate`, the seat camera path, live mouse-look orbit and PER-SEAT control feel (`playerDefaults.seatLook`), the pointer/cursor stack and the radial action menu (binding wheels, hold Tab, `player.wheel.ring`/`player.wheel.commit`/`world.view.wheel`), window-layout composition, `world.row.set views.*`/`view.override` verbs | [references/views.md](references/views.md) |
| `player.engage`, context routes (screen or body target, capture policy, channel mask), latch/route repair, server-internal merged pads, possession's co-drive path, machines | [references/engagement.md](references/engagement.md) |
| Join/leave (local seat and peer), park-with-grace, the `$parked:` reserved rule channel, body-resume's identity match rule | [references/session-lifecycle.md](references/session-lifecycle.md) |
| The replay tape: format/re-key, capture scope, pose hash, verify semantics, receipts | [references/replay.md](references/replay.md) |
| Addon rows, mounting, pump points, channels, fuel, ABI verdicts, the `world.addon.mount` verb family | [references/addons.md](references/addons.md) |
| Command modules, routing, the stdin barrier, output contract, verb grammar, screenshots | [references/console.md](references/console.md) |

Adjacent skills: `sdf-world` for the renderer and SDF VM the frame source
feeds; `gaming-bricks` for the emulators behind engaged screens;
`rom-forge` for the SM83 framework and the Tune cart; `maths-usage` for
choosing fixed-point primitives on sim value paths.

## Boundaries worth knowing

- `WorldPopulationLimits.CapacityCeiling` is 128 (the largest authored
  `population.capacity` the validator admits), and `WorldClient.EntityCapacity`
  is SINGLE-SOURCED from it (`= WorldPopulationLimits.CapacityCeiling`, the F3
  reconciliation 2026-08-06) — so the validator's admitted capacity and the
  client's fixed per-entity view arrays are the SAME number by construction; the
  old gap where a document could author past the client bound, validate, and boot
  into an out-of-bounds throw is closed. Shipped worlds author 128 with seats 0–3
  local and 124 simulated.
- `SdfProgramBuilder.MaxInstances = 16384` — the per-tile mask width scales
  with DECLARED instances, which is why the frame source emits active
  avatars only and the render envelope is probed at construction
  (`WorldRenderEnvelope.TryFit` is the apply-time capacity gate).
- The per-pixel soft-shadow gather addresses ≤1024 instances; beyond that
  the engine falls back to coarser camera-tile masking.
- `ViewStack.MaxRegisteredViews = 64` — never register a rendered view per
  population entry.
- `WorldDynamicGeometryCeilings.MaxContributedDynamicInstances = 16000`:
  the document-global CPU/instance-grid ceiling. The separately recorded
  GPU-bound measurement is `0`, but it is not the governing admission term.
- XInput caps at 4 Xbox-family pads locally; HID pads are uncapped.
- The overlay reservation's tightest resource is panels (headroom 2) — a
  HUD capacity bump fails the `Puck.Overlays` build until the leases move
  with it ([references/hud.md](references/hud.md)).
