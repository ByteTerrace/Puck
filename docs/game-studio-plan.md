# In-engine game studio roadmap

The studio connects authoring, deterministic baking, cartridge assembly, and
play inside one running overworld:

```text
sculpt or compose → preview → bake → link → self-verify → hot-swap → play
```

Authored content remains greenfield `Puck.Demo` data and is verified by running
the demo and by each forge's machine-level self-check. Reusable engine
mechanisms belong in their natural split project once their contract is clear.

## Product goal

A player can author a character, world object, tune, or game asset with a
controller; see the brick-target result live; forge a valid cartridge; insert
it into a cabinet; and continue editing after play without restarting the
session or switching tools.

The studio is successful when:

- a newcomer can create and forge something without knowing the bake pipeline;
- saved creations and worlds reload deterministically;
- cartridge games are polished enough to choose for play, not only proofs;
- multiple cabinets can participate in link gameplay;
- the same authored object can appear in the world, on a screen, and in a
  cartridge without a lossy translation between private formats.

## Current foundation

| Area | Current capability |
|---|---|
| Creator | Pad-first and console-assisted SDF editing, selection, transforms, flat groups, materials, modifiers, animation frames, save/load, placement, grid snapping, companions, and live bake preview. |
| World sculptor | Load, place, edit, verify, save, and reveal `puck.world.v1` content. Puckton demonstrates authored buildings, roads, props, walkability, companions, and a data-file world reveal. |
| Tracker | In-engine `puck.audio.v1` pattern and order editing, deterministic compile, preview through a machine, save/load, and jukebox forge. |
| Bake | GPU rasterization plus deterministic CPU grading, palette fit, tile assembly, preview, diagnostics, and versioned PBAK output. |
| Linker | Manifests declare tile, palette, screen, text, script, record, sound, and sprite assets. PBAK backgrounds and sprite sets parse and relocate through the same linker. |
| Game framework | Interrupt-driven SM83 runtime with input, OAM, background queue, state machine, PRNG, versioned saves, text, sound, manifests, and link helpers. |
| Games | Brickfall, Volley, Chroma, Solitaire, Poker, Oracle, and Critter-Swap build on the shared framework and boot-verify before output. |
| In-session forge | A subject-neutral registry for avatar, SDF-art, and tune cartridges; forged output becomes cabinet content without restart. |
| UI | Permanent overlay console and binding bars plus diegetic terminal and SDF action-bar mirrors driven by shared layout and text data. |

The [overworld guide](overworld-demo-plan.md) describes how these surfaces are
reached during play. The framework and bake READMEs describe implementation
contracts.

## Settled decisions

| Question | Decision |
|---|---|
| Game logic | Compile SM83 code against the shared framework. Rules, layouts, decks, and content are manifest data; there is no in-ROM VM or new game DSL. |
| Toolkit home | The greenfield framework remains under `src/Puck.Demo/Forge/Framework/` while it depends only on `Sm83Emitter` and image encoders. |
| Art style | Each cartridge selects a data-driven bake style such as `classic` or `bold`. |
| Randomness | Game seeds come from deterministic input timing. A repeated input tape produces the same shuffle and play state. |
| Audio | Games use the shared integer APU driver and manifest-declared streams. Host playback drains each machine's integer audio sink. |
| Editor input | Controller-first with console assistance. Keyboard and mouse may accelerate authoring but are not required. |
| Poker | Five-card draw is the shipped variant. AI personalities are data; the decision seam remains the insertion point for link-fed seats. |
| Verification | Every forge boots its ROM on a real Humble machine and checks observable state, framebuffer, save, and audio behavior before writing the artifact. |

## Roadmap

### 1. Unify the studio document family

Current formats (`puck.creation.v1`, `puck.world.v1`, `puck.audio.v1`, PBAK,
manifests, and game tables) are individually useful but do not yet form one
artifact graph.

Define how documents reference shared creations, palettes, tiles, audio,
assemblies, game tables, and bakes. Keep each wire format versioned and make
optional data nullable and normalized at load. Decide explicitly which objects
are content-addressed:

- source documents;
- generated bakes;
- reusable assemblies;
- complete cartridge recipes;
- mutable world or profile state.

Do not make transient previews or machine snapshots durable merely because
they are serializable.

### 2. Make world authoring production-ready

The world sculptor can create and persist a compact town. The next quality bar
is editing a place rather than demonstrating placement:

- select and edit placed objects in situ;
- create named assemblies once and stamp instances many times;
- expose hierarchy only where a real assembly needs it;
- rebuild walkability deterministically after edits;
- support deliberate save, revert, duplicate, and recovery flows;
- author roads, entrances, lighting, screen wiring, and cabinet placement with
  the same feedback quality as object transforms;
- measure larger scenes with `puck.bench` and the SDF instruments.

The simulation consumes a fixed-point query artifact. It must not query the
presentation renderer or use floating-point field evaluation as authoritative
movement state.

### 3. Improve rigs and animation

Add author-facing rig primitives instead of requiring every pose to be a list
of hand-tuned rotations. The preferred first solver is analytic two-bone IK for
arms and legs, with deterministic frame samples for cartridge bake.

Use the flagship creations as coverage:

- lantern fish: spine, tail, and camera lure;
- CRT-faced robot: rigid limbs and a live screen face;
- RPG adventurer: planted feet and four-facing walk poses.

Runtime preview interpolation is presentation-only. Baked sprite frames remain
explicit deterministic samples.

### 4. Finish editor interaction and recovery

Improve the authoring experience without creating a second desktop-only UI:

- material and palette picking with visual feedback;
- named selection and assembly navigation;
- undo and redo over document operations;
- copy, duplicate, align, distribute, and numeric-entry tools;
- timeline scrubbing, tween preview, and clearer keyframe ownership;
- diagnostics that identify the object and budget responsible for a failed
  bake or invalid save;
- autosave recovery that never replaces deliberate published saves.

The overlay remains the reliable accessibility and automation surface. Diegetic
UI mirrors it; it does not hide the console from scripts or agents.

### 5. Complete audio authoring

The tracker and audio document compile one music voice plus effects. Continue
with:

- authored loops for every framework game;
- effect editing through the existing compiler path;
- live preview updates during edits;
- an explicit decision on additional music voices and their WRAM/APU budget;
- reusable tune and effect libraries referenced by cartridge recipes.

Audio scheduling remains integer machine state. Host mixing and device output
are presentation.

### 6. Deliver multiplayer Poker

Poker already separates seat decisions from table legality and has deterministic
AI and evaluation oracles. Replace selected AI decisions with link-fed actions
while keeping the table authoritative and deterministic.

The first deliverable is two linked cabinets completing a full hand with
replay-identical traffic and saves. Expand to additional seats only after the
two-seat protocol proves disconnect, timeout, retry, cancel, and resume behavior.
Critter-Swap is the reference for bounded role negotiation and checksummed block
exchange; it is not a game-specific protocol to copy verbatim.

### 7. Connect cartridges back to the world

Add a narrow cartridge-to-host event seam for intentional game events such as
device upgrades, world pickups, or completed challenges. Events must be named,
versioned data and must enter the host at a deterministic tick boundary.

This enables:

- in-ROM pickups that promote or alter the real cabinet device;
- world items that become cartridges;
- game results that unlock authoring or world content;
- machines that deliberately feed named views or other machines;
- link activity with visible world consequences.

Arbitrary memory polling is suitable for proof conditions, not a general game
event API.

### 8. Prepare for external creators

Production readiness requires more than feature depth:

- stable error messages and document migration guidance;
- a small set of complete example projects;
- discovery and import for user documents and ROMs;
- explicit trust and sandbox rules for addons and imported content;
- accessibility for console, text size, contrast, and controller remapping;
- packaging that does not require the repository or developer asset paths;
- recovery from missing assets, unsupported GPUs, invalid saves, and interrupted
  forge operations.

## Artifact-completeness checklist

Before declaring the studio document family complete, decide whether it owns
each of these as a first-class reusable artifact:

- creations and rigs;
- world assemblies and placements;
- sprite sheets and backgrounds;
- tile sets and palettes;
- fonts and text styles;
- audio patterns, songs, and effects;
- game tables, rules, and layouts;
- UI layouts and menus;
- cutscenes or scripted sequences;
- cartridge recipes and save migrations.

An item may intentionally remain embedded in a recipe, but the choice should be
documented rather than accidental.

## Working rules

- Build authored content and studio UX in `Puck.Demo`; move only proven,
  content-neutral mechanisms into engine projects.
- Verify studio changes by running the demo and exercising save/load and
  forge/hot-swap. Do not add a `Puck.Post` stage for game-specific behavior.
- Keep source documents deterministic and reviewable. Generated previews and
  caches never become the only copy of authored work.
- Preserve the forge's independent machine-level verification. The builder and
  verifier must not share an oracle that can make the same mistake twice.
- Ask the user when a choice changes the durable document model, CAS boundary,
  multiplayer rules, or public authoring workflow.
