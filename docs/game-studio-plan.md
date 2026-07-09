# The game studio — the in-engine authoring plan of record

> What EXISTS is documented in [capability-catalog.md](capability-catalog.md)
> §7 (the forge) plus the two handoff READMEs
> ([Forge/Framework](../src/Puck.Demo/Forge/Framework/README.md),
> [Forge/Bake](../src/Puck.Demo/Forge/Bake/README.md)) and the
> [overworld plan](overworld-demo-plan.md)'s creator section — this doc does
> not restate it. This doc is the ROAD AHEAD: the vision, the binding
> decisions, and every idea not yet built. Greenfield discipline holds
> throughout (CLAUDE.md rule 3): everything here lives in `Puck.Demo`, is
> verified by RUNNING + the forge `Verify` discipline, never by gates, and
> folds into the ONE overworld — the editor is a mode you walk into, the
> games are cabinet cart types you cycle to.

## The north star

Stand in the overworld. Walk to an empty pedestal, hold a button, and the room
eases into an authoring surface. You sculpt a character in SDF — smooth-union
a snout onto a head, twist a tail, paint materials — and the easel beside your
hands shows, live, what that character becomes as a GamingBrick sprite. You
forge it — not just baked art, but a **real, professional cartridge**: a game
you also authored, with music, menus, high scores that survive power-off, and
gameplay worth sitting down to. You carry it to a cabinet, boot it, and your
hand-sculpted avatar deals cards in a **Poker** game three other players at
the neighbouring cabinets just joined over the link.

Sculpt → bake → forge → play → iterate — every hop deterministic,
data-defined, and inside the running engine. The acceptance FEEL (a bar, not
a gate):

- A newcomer sculpts a character and sees it become a playable brick sprite
  live, with no idea a bake happened. *(The mechanism exists — the easel;
  the bar is a newcomer needing no instructions.)*
- The card games are ones you'd *actually choose to play* — art, sound, feel,
  and a shuffle that's fair and replayable.
- Four people sit at four cabinets and play Poker together, in a room one of
  them authored, on devices whose screens light the walls.
- Someone asks "what engine is this game made in?" and the honest answer is
  "the one it's running inside."

## Binding decisions (settled with the user)

Settled — never re-litigate; the OPEN forks are mandatory prompts.

| Fork | Decision |
|---|---|
| Game-logic altitude | **Framework + data tables** — SM83 logic over `Forge/Framework/`, rules/layouts/decks as ROM data tables. No DSL, no in-ROM VM. |
| Toolkit home | **`src/Puck.Demo/Forge/Framework/`** (greenfield); the lift to `experimental/Puck.HumbleGamingBrickRom` stays mechanical (it depends only on `Sm83Emitter` + the `HgbImage` encoders). |
| Framework proof | The framework proved on **Brickfall alone** — the Volley/Chroma re-forge rides the card-game arc. |
| Art style | **Both, per-cart** — the bake's `classic`/`bold` style knob, declared in each creation document. |
| Audio | **LANDED (minimal set, 2026-07-06)** — manifest-declared `ApuSoundDriver` + the `SoundTables` SFX catalog (deal/flip/shuffle/win/cursor/thud/sweep/over + a pulse-2 music loop), per-cabinet host `waveOut` streams off the emulator's `IAudioSink`. Remaining for a later arc: the audio *document*/tracker surface and per-game loops beyond Brickfall. |
| Editor input | **Pad-first + console assist** (paged bar verbs + `creator.*` console verbs; keyboard optional, never required). |
| Poker variant & multiplayer priority | **Five-card draw; deterministic AI at the table PLUS link groundwork** (resolved 2026-07-05). Three data-table AI opponents ship this arc; the emulator `ISerial` seam and cross-cabinet session plumbing land now so the follow-on multiplayer arc is pure gameplay work. Hold'em can ride that later arc. |
| ❓ "Truly complete" boundary | **OPEN — the card games are now concrete (landed 2026-07-06), so this is the NEXT arc's opening prompt.** Enumerate the full authored-artifact list with the user: avatars, sprites, backgrounds, tile sets + palettes, audio, game tables — fonts? board layouts? cutscenes? |

## THE NEXT ARC — the world sculptor (start here)

> **★CONTENT PASS BEGUN (2026-07-06, branch `features/alpha-prep`).** The first
> real TOWN exists — "Puckton" (`src/Puck.Demo/Town/`, `--forge-town`): a cozy
> dusk block sculpted out of player-reachable creations (arcade, storefronts,
> fountain, lamps, trees), saved deterministically to the CAS, walkable
> (buildings block via the baked walk grid), and REVEALED by the zoom-out — a
> run document naming `"world": "puckton"` (`OverworldNode.World`; e.g.
> `docs/examples/overworld-town.json`) makes it the room the fourth-wall reveal
> eases you into after you win the intro cartridge (the user's chosen front
> door). The
> flagship trio inhabit it as roaming companions. This proved the whole chain
> (sculpt → save-to-CAS → place → bake → load → reveal) end-to-end and exposed +
> fixed a real latent renderer bug (static placements dropped their transform —
> see the alpha-prep memory). Seams still open: richer authoring in-engine
> (place→edit-in-situ), the flagship rigs posed live, cabinet re-homing, a proper
> road surface, and the render-range ceiling that forces a COMPACT block today
> (the machine-fleet/perf seam — a bigger town needs the reveal camera + the SDF
> instancing to reach farther). The town is CONTENT (verified by RUNNING +
> `--forge-town`'s self-proofs), never a gate.

**This arc is the convergence, not another feature list.** Everything built
so far is an instrument waiting to be played together:

- the deterministic, document-driven engine is the **stage** — one JSON run,
  bit-identical every time, on either backend;
- the GamingBrick machines and the five five-star framework games are the
  **payoff** — real things worth walking toward;
- the forge, the linker, and the bake pipeline are the **press** — anything
  authored becomes a genuine cartridge;
- creator mode is the **hands** — sculpting lives inside the running engine;
- the audio documents are the **voice**; the camera and the serial link are
  the **connective tissue**; the avatar forge is the **player themself**.

The world sculptor plays them in concert. The user's scoping (2026-07-06):
make in-game SDF creation truly rich — for avatars AND for the world —
until the overworld stops being a demo room with cabinets and becomes a
PLACE: a small town built entirely in-engine (a proper new arcade, houses,
a grocery, roads, street lights, the little decorations between them),
molded by the player, iterated freely, saved deliberately. Every authored
thing a document, everything deterministic, saves content-addressed (the
user's phrase: "everything CAS and deterministic as usual"; confirm the CAS
scope at arc start). Creator mode today sculpts a creature on a pedestal;
this arc grows it until the pedestal's room — and the street outside — is
something you sculpted too.

**The destination this arc points at: a consumer ALPHA.** When the town can
be built, played, heard, and kept — when a stranger could download Puck,
walk into a world someone sculpted, play five real games at its cabinets,
make something of their own, and come back tomorrow to find it exactly as
they left it — the studio stops being a demo. This arc should end with us
able to say "we could hand this to strangers" with a straight face.

The acceptance FEEL — the alpha bar (a bar, not a gate — same spirit as the
north star):

- You walk out of your arcade, sculpt a house across the street, line the
  road with lamps you made once and stamped many times, and walk into the
  grocery you just invented — without leaving the engine or the pad.
- Iteration is fearless: mold, look, undo, mold again; SAVE is a deliberate
  act, and a saved world reloads bit-for-bit.
- A creation made for a cart and a creation made for the world are the same
  kind of thing — one vocabulary, one document family, one bake discipline.
- The flagship trio below inhabit the town — the lantern fish filming you,
  the CRT-faced robot emoting at passers-by, the adventurer walking beside
  its own baked-brick twin — all sculpted, rigged, and posed in-engine.

**The seams this arc pulls on — each one finishes a thread an earlier arc
deliberately left waiting (a lever, not a spec; the shape of the work
belongs to the arc itself):**

1. **Creations become world.** The overworld already renders through the
   centralized `SdfWorldRenderSpec`/`SdfWorldRenderBuilder` path with
   instancing to 1024; creator mode already sculpts `puck.creation.v1`
   documents live. The missing move is authoring INTO the world's own SDF
   program — place, edit, and keep world-scale geometry in the room you
   stand in. *(2026-07-08: grid-locking landed — `world.snap`/`creator.snap`
   console verbs plus a RightShoulder chord snap position/rotation to a
   configurable lattice, with an align-to-shape face/center mode, in both the
   world sculptor and creator mode; the grid renders live in each editor.)*
2. **Domain ops as authoring verbs.** Repeat / mirror / twist / bend exist
   in the VM and the renderer reserves two per-shape modifier slots — the
   editor exposes none of them. A road IS a repeat; a row of street lights
   IS a repeat; expose the ops and the town gets cheap.
3. **Assemblies you make once and stamp many.** Flat single-level groups
   were deliberately kept until "a real authoring need bites" — a town is
   that need. Sculpt one lamp, name it, stamp twenty (instancing already
   carries the cost story).
4. **Richer avatars ride the same growth — and get a skeleton.** A basic
   IK framework in the authoring layer: the classic analytic two-bone
   solver (law-of-cosines joint placement, in the spirit of
   <https://iquilezles.org/articles/simpleik/> — no iterative solvers),
   so limbs, tails, and danglers pose from GOALS ("this foot lands here",
   "the lure hangs there") instead of hand-set angles, and walk/swim
   cycles fall out of moving the goals. Authoring/render side only —
   anything entering sim state stays fixed-point — and baked brick sprite
   poses inherit IK'd frames through the existing avatar-forge path. The
   part vocabulary, palette/material picking (today console-numbers
   only), and timeline posing get the same richness attention.
5. **Walkability.** `OverworldWorld` walks a fixed room today; a molded
   town needs the sim to know where the player can stand — derived from the
   SDF, or authored as its own thin layer (❓ open design seam; keep the sim
   deterministic and fixed-point whatever wins).
6. **The world document.** Grow `puck.creation.v1`, or a sibling
   scene/world document in the same family (versioned, nullable optionals,
   normalized at load)? (❓ open fork — decide against real authored
   content, not in the abstract.)
7. **Save = deliberate, deterministic, content-addressed.** Deterministic
   bytes hash stably; ❓ confirm what "everything CAS" should cover
   (documents? bakes? world snapshots?) and land the store accordingly.
8. **Performance posture.** A town is more instances and more shapes than
   four cabinets; the machine-fleet briefing and the engine capacity knobs
   are the levers — measure with `--bench`/probes, never guess.

**The flagship avatars (settled deliverables — each stresses a different
rig, and all three are made with the same tools any player has):**

- **The lantern fish.** An undulating spine-and-tail rig whose dangling
  lure IS a camera — it swims beside the player and films them, the feed
  a live content source for any diegetic screen (the hovering
  camera-operator archetype; the camera content-source and viewport
  seams already exist).
- **The CRT-faced robot.** Rigid two-bone limbs under a boxy body whose
  face is a SCREEN — emotes, game feeds, whatever a screen source can
  carry (the diegetic-screen tech that lights the cabinets, worn as a
  face).
- **The RPG humanoid.** The classic top-down adventurer baseline:
  two-bone arms and legs with planted-foot walk cycles, bakeable to the
  brick's 4-facing walk poses through the existing avatar forge.

**Opening prompts at arc start (ONE AskUserQuestion round, per the settled
ritual):** the alpha's boundary (what is IN a hand-to-strangers alpha, and
what is explicitly out), the CAS scope (7), the world-document fork (6),
the walkability seam (5) if the design surfaces real options — and the
standing ❓ "truly complete" enumeration folds in naturally here, because a
town adds exactly the artifact types that list needs (buildings, roads,
decorations, assemblies).

**Execution shape:** the landed-arc playbook below stands — prompt the
forks, then one self-contained worktree agent per workstream, the
orchestrator owning integration files and independently re-verifying every
claim. Model economics, settled with the user (2026-07-06): planning,
orchestration, integration, and re-verification sit with the strongest
available model; LABOR runs on cheap fast subagents under tight
prescriptive briefs — safe here precisely because the verify net
(batteries, byte-identical artifacts, boot proofs, smokes) catches what a
cheaper model misses; escalate a single workstream to a stronger tier only
when its verification keeps failing or its design is genuinely open-ended.

**Anti-noose clause (binding):** the eight seams above are orientation, not
a work breakdown. The arc re-negotiates its own shape when it starts, builds
whole asks in single passes, and leaves room for whimsy — the waterfall rule:
if a small delightful thing presents itself, build it.

## THE LANDED ARC — the professional forge (2026-07-06)

All seven items below LANDED (10 commits, every workstream independently
re-verified). Kept as the record of what exists and as the proven fan-out
shape for future arcs. The original scoping: the linker, the card games, the
legacy-game re-forge, and audio in one arc; forks resolved 2026-07-05
(five-card draw with deterministic AI plus link groundwork, minimal-scope
audio in-arc).

**The work, in dependency order:**

1. **Forge-as-linker (the foundation upgrade) — LANDED.** The framework
   links `PBAK` bundles natively (`PbakBundle` reader + `AssetLinker` on the
   facade: tile bank + palette slots as sequential grants, references
   relocated, boot tables sealed), and `GameManifest`/`LinkedManifest` is the
   declarative data-table layer every game declares its identity into
   (tiles/font/palettes/screens with the overlay contract, rule/record
   tables, strings, input scripts, sprite art). Brickfall is the reference
   consumer; the `--forge-*` recipes converge on manifest + linker. Full
   contract: the [framework README](../src/Puck.Demo/Forge/Framework/README.md).
2. **The card layer (`Forge/Cards/`) — LANDED.** `CardDeck` (deterministic
   Fisher–Yates over the framework PRNG, with a bit-exact C# oracle whose
   inverted LCG recovers the seed from a dealt board), `CardTables` (card
   records + the composed-face tile set — 52 literal faces exceed the tile
   bank; the trade is narrated), `CardArtBake` (budget-guarded felt/emblem/
   cursor bakes + a suit-and-card SDF scene vocabulary), `CardMenu`,
   `CardInitialsPad`, `CardUndo`, `CardSfx` (aliasing the sound catalog).
   Both card games consume it; neither copies it.
3. **Solitaire (Klondike), five-star — LANDED.** Draw-1 Klondike at the
   full Brickfall bar: tableau/foundation rules from card records, exact-
   snapshot undo, deal-from-seed (the verifier predicts the whole deal from
   the recovered seed), streak/best-streak battery save, scripted attract,
   SDF-baked title/felt/cursor, real sound. Cabinet cart type 7. Verify:
   `--forge-solitaire`.
4. **Poker (five-card draw), five-star — LANDED.** The player + three AI
   opponents whose personalities are data records, fixed-limit betting,
   draw phase, and an oracle-mirrored showdown evaluator (shape-indexed
   category table + strength bases as manifest data); chips in packed BCD,
   conservation-checked. The per-seat decision seam (`DecisionAction` out,
   legality downgrades table-side) is the landing point where the follow-on
   multiplayer arc substitutes link-fed actions for AI. Cabinet cart type 8.
   Verify: `--forge-poker`.
5. **Volley + Chroma re-forged five-star — LANDED.** Both games rebuilt on
   the framework (`Forge/Volley/`, `Forge/Chroma/`) with their identities
   declared as `GameManifest`s: the full seven-state graph (title / scripted
   attract / high scores / play / pause / game over / initials entry),
   battery-backed top-5 tables, D4 input-entropy PRNG (serves and drips),
   SDF-baked title emblems through the `PBAK`→art-screen seam, and the
   shared sound catalog at their trigger sites. Gameplay stays true to the
   originals (Volley: match to 7, +1/rally +100/point; Chroma: drip well +
   swap cascades at +1/block, diff-queue repaints). `ArcadeKernel`/
   `ArcadeCartridge`/`ArcadeArt` retired in the same change (the settled
   ritual). Verify: `--forge-volley` / `--forge-chroma` (full
   BrickfallVerify-bar batteries + boot PNG + audio WAV).
6. **Audio (in-arc, minimal scope per the resolved fork) — LANDED.**
   `ApuSoundDriver` is the real `ISoundDriver`: three sequencer voices
   (pulse-1/noise SFX, pulse-2 music) pumping `[duration, APU register]`
   streams declared as ordinary manifest tables (`SoundTables.DefineIn`).
   Host side, each booted cabinet opens its own `waveOut` PCM stream over
   the emulator's `IAudioSink` (`CabinetAudioOutput`; output-only — host
   audio can never perturb the simulation). Brickfall sounds; the card
   games consume the same catalog (deal/flip/shuffle/win are already in
   it).
7. **Link groundwork (rider from the opponents fork) — LANDED.** The
   emulator serial-link seam is implemented: `SerialComponent` exchanges bits
   with a linked peer under correct internal/external clock semantics, and
   `SerialLinkSession` (Puck.HumbleGamingBrick) is the one blessed connect
   seam — it owns the deterministic instruction-interleaved pair-stepping a
   cable requires (a linked pair is ONE step unit; never two threads). Gated
   by the Humble battery's first Tier C stage (`serial-link`: dmg↔cgb byte
   exchange, per-transfer serial interrupts, replay-identical across runs).
   Demo side: cabinets link/unlink via the Bricks debug page's Link verb, and
   a linked pair advances together through `ExecuteLinkedStep` (one shared
   budget through the session) — a Poker cart joins a link session by simply
   being the running cart on two linked cabinets.

**Execution shape (use subagents EFFICIENTLY — this arc is built for it):**

- **Prompt forks → plan-mode design → fan out.** The proven split: the main
  loop owns integration files (`OverworldRenderNode`, `Program.cs`, cart-type
  wiring) and sequencing; each numbered item above is a **self-contained
  brief for one background agent** — new folder + stable facade, exactly how
  the framework/bake/Brickfall workstreams ran conflict-free in parallel.
- **Dependency-order the fan-out:** item 1 lands first (solo agent, everyone
  else consumes it); items 2+3 are one agent (cards+Solitaire share a
  brief); item 5 is fully independent (worktree agent — it touches legacy
  files others read); item 4 follows 2; item 6 is independent once scoped.
- **Briefs carry the settled decisions** (this doc + the `rom-forge` skill +
  the two Forge READMEs), the repo rules (analyzer ceilings, named args,
  trademark hygiene, `--exit-after-seconds 2` smokes), and REQUIRED
  self-verification: build clean + the game's `Verify` battery + boot-proof
  PNG before reporting. The orchestrator independently re-runs each agent's
  battery and eyeballs its PNGs — trust, then verify.
- **Every game keeps the `BrickfallVerify` bar**: state-flow assertions,
  seed-entropy/replay determinism, SRAM round-trip with an independent
  checksum, corruption→defaults.

## The remaining workstreams

Arcs, not a waterfall — each ends in something you can boot and see; iterate
toward the bar above rather than declaring "done." The ordering is NOT
settled: re-negotiate it with the user at each arc. (The card games, the
Volley/Chroma re-forge, and audio are folded into THE NEXT ARC above; their
full design briefs stay below.)

### W2 · The card games (the forcing functions)

**Both games LANDED 2026-07-06** (arc items 2–4 above): the forcing function
held — the studio produced 52-card art within real hardware budgets, menus,
scoring, saves, undo, and provably deterministic randomness (the verifiers
predict entire deals from recovered seeds). What remains of W2 is its
centerpiece:

- **Multiplayer Poker across cabinets over the shared timeline / link-cable
  is the north star's centerpiece** — it fuses the games, the framework, the
  overworld's multiplayer, and the recursion (W7) into one scene. It is now
  PURE GAMEPLAY WORK: the serial-link groundwork (item 7) carries the cable
  and deterministic pair-stepping, and Poker's per-seat decision seam is the
  substitution point for link-fed actions. Hold'em can ride this arc too
  (per the resolved variant fork).
- **The determinism design proved out:** Fisher–Yates over the framework
  PRNG from input entropy, same seed → same deal → bit-for-bit replay, all
  state fixed-point/integer, every cart self-verified on a real machine —
  now with C# oracles mirroring the shuffle, Poker's evaluator, and the AI
  personalities byte-for-byte.

### W5 · Audio

**LANDED through the tracker (2026-07-06).** The minimal set (arc item 6:
real driver, real speakers, the curated SFX catalog), then the document
layer: `puck.audio.v1` (patterns of note rows, an order list, named effect
entries) compiles deterministically through `AudioDocumentCompiler` to the
driver's exact streams; `--forge-tune` builds a self-verified jukebox cart
from any document (byte-identical, WAV-proven); Brickfall's loop compiles
from a checked-in document; and TRACKER MODE authors documents in-engine —
the `tracker` console verb (or pad toggle) takes over the creating slot
exactly like creator mode (mutually exclusive with it), pad-first
row/note/tempo editing narrated to the on-screen console, preview compiled
onto a headless machine and heard through the cabinet speaker path, saves
beside the creations. What remains of W5:

- Loops for the other four games authored as documents (Brickfall is the
  proof; the rest still play the stock hand-authored loop).
- Tracker v2 niceties: live-preview patching mid-edit, effects authoring
  through the already-implemented `CompileEffect` path, and the multi-
  channel music question (the driver plays one music voice today).

### W6 · The editor through the SDF VM (shaped 2026-07-09 — the DIEGETIC UI arc)

Author the action bar + dev console — and eventually the whole editor —
*through the SDF VM itself*, as DIEGETIC geometry, so ONE renderer owns all
UI and the editor dogfoods the engine it authors for. **User rulings
(2026-07-09, settled — don't re-litigate):** the current overlay console is
PERMANENT as the convenience/agent-facing surface (stdin/stdout stays the
control plane); the diegetic UI is the immersion surface that MIRRORS it.
The action bar does not survive as a separate bespoke item: same layout,
same concept, ROLLED INTO the diegetic UI as SDF geometry. The staircase and
its enabling machinery (post-grid-cull instance budget, the 2D-lifted panel
family, scoped widget groups, the MTSDF glyph op mining `Puck.Text`) live in
[sdf-backlog.md](sdf-backlog.md) item 21. The CRT screen treatment being
fully view-independent also makes it **100% bakeable** — the deferred
"cheaper screens" lever rides this arc.

### W7 · The recursion (the spectacular layer)

Forged carts are already cabinet cart types; extend toward the settled arcs:
in-ROM pickups that promote the real device (needs the machine→engine event
seam), world-lens machines revealing hidden things, **link-cable trading**,
machines feeding machines. Multiplayer Poker over the link (W2) is the
synthesis. Related plumbing: the emulator's serial-link seam is LANDED (item
7 above — `SerialLinkSession` + the Tier C `serial-link` stage + the demo's
cross-cabinet Link verb), and the fleet/performance groundwork lives in
[machine-fleet-briefing.md](machine-fleet-briefing.md).

### Editor remainder (grow W4 toward "rich")

*(This backlog is the world-sculptor arc's substrate — see THE NEXT ARC at
the top; every item below feeds one of its seams.)*

- **Domain ops as authoring verbs** — repeat, mirror, twist, bend: the VM
  expresses them all; the editor exposes none yet. The renderer's worst-case
  probe already reserves TWO per-shape modifier instruction slots for exactly
  this (grow the probe if a shape ever carries more).
- **Hierarchy beyond flat groups** — groups are single-level integer ids
  today; named sub-assemblies / nested grouping is unexplored (deliberately —
  the flat list matched the instruction stream; revisit only when a real
  authoring need bites).
- **Interpolated playback** — the timeline is hold-style frame snapshots by
  design (reads correctly for brick-target art); tweened preview playback is
  an open nicety that must NOT leak into the baked frame sets.
- **Palette/material UX** — the palette is editable only via
  `creator.palette` numbers; a picker surface (or a baked-preview-driven
  suggestion flow) is open.

### Forge-as-linker remainder

The forge should become a **linker**: authored documents (art, sprites,
avatar, game tables, audio) in, ONE self-verified `.gbc` out. In place today:
the `PBAK` asset blob and per-seam adapters (`SetTitleArt`, the avatar
sheet). Remaining: the framework consuming `PBAK` blobs directly as its asset
sections, and generalizing today's one-class-per-game forge recipes into a
composable pipeline while each game's *identity* (rules, art) stays data.

### The document family remainder (everything-as-data)

`puck.creation.v1` exists. Still to define as the studio grows: the audio
document (W5), a standalone tile-set/palette document (if bakes need to be
shared between carts rather than re-run), and the **game table/rules
documents** — the data half of the resolved framework-plus-data-tables
altitude (Brickfall's C#-declared tables are the seed; card games decide
what generalizes). Keep every schema in the `puck.run.v1` family style:
versioned, nullable optionals, normalized at load.

## How to work on this plan

The general doctrine lives in CLAUDE.md and
[agent-guide.md](agent-guide.md); the studio-specific rules:

- **Iterate toward the bar, don't gatekeep toward safe.** Build the whole ask
  in a pass; git is the rollback.
- **Verify by running** + the forge `Verify`-on-a-real-machine discipline for
  every game. No gates or Post stages for studio features unless asked.
- **Stay data-driven & deterministic** — authored artifacts are documents;
  game randomness is seeded from input; state is fixed-point.
- **Prompt for clarity at every ❓** and whenever a choice would meaningfully
  change the result — then write the decision back into THIS file so the plan
  sharpens each pass.
