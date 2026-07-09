# The overworld — Puck.Demo's plan of record

> **The Demo is GREENFIELD — a playground.** Everything in this plan describes a
> prototype that is expected to churn and be rewritten. Demo changes are NOT
> engine changes: verify them by RUNNING the demo
> (`dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2`
> is the headless smoke; `0` or less runs until the window is closed),
> never by gating them. Do NOT promote a demo feature into Post — a stage, gate,
> hash, or `*DeterminismNode` hook — unless the user explicitly asks; that
> calcification is exactly what the user does not want. The "Gate: …" notes
> below record what happens to exist today, not a requirement that demo features
> be gated. Doctrine:
> [agent-guide.md](agent-guide.md#anti-calcification-doctrine) rule 5.

## The unification contract (READ FIRST — the north star for this arc)

`Puck.Demo` is ONE cohesive game experience, not a menu of `--flag` modes. Every
capability is reached from inside a single running session — a diegetic act, a
pad chord, or a console verb — with **no process restart**. The launch surface
(flags, and the former `PUCK_*` env vars) is not how you reach a capability; it
is at most a convenience preset that enqueues the same in-session acts at boot,
or a headless CI/proof twin of an in-game path. A capability with no in-game
path is a unification **TODO, not a mode**. Six rules follow, and they OUTRANK
any older "pick a mode at launch" prose anywhere in this repo:

1. **One experience, many reflections.** The default run IS the game. `--rom`,
   `--run`, the `--forge-*` tools, the review `--scenario`, `--validate-overworld`
   are developer/CI reflections of in-session capabilities (or pure engine
   proofs) — never separate products. Docs name the in-game path FIRST and the
   flag as its twin.

2. **The data file, not env vars.** Durable configuration lives in the
   `puck.run.v1` run document (the data file): which world the overworld IS and
   reveals into, each cabinet's starting cart, immersion, the reveal target.
   **The demo's entire `PUCK_*` environment surface has been removed** — it was
   noise. Each former env var became a run-document field (durable) or a console
   verb (live); see the migration table below. (Engine/launcher diagnostic
   toggles like `PUCK_TIMING`/`PUCK_RAY_QUERY` are a separate, non-demo concern
   and are untouched.)

3. **The console is the control plane.** The on-screen console and process
   **stdin** drive the ONE command registry; every capability has a console verb,
   and results echo to **stdout** so a piped script drives the whole engine
   deterministically and assertably. This is the agent-facing control + testing
   surface — pipe a verb script into a run (`… < script`, or PowerShell
   `Get-Content script | …`; blank and `#` lines are comments) and read the echoed
   results. Runnable, self-documenting examples live in
   [docs/examples/scripts/](examples/scripts/) (the smoke loop, the reveal ladder,
   in-game authoring); `help` lists every verb. (The stdin→registry transport
   already exists in the launcher; verb COVERAGE and the observability verbs —
   `state`, `step`/`settle`, `capture`, `reveal`, `boot`, `link`, `player.add` —
   are the work.) Determinism
   is not a demo concern (Puck.Post owns the engine contract); this
   scripted-console path is how demo changes are verified — by RUNNING, now
   reproducibly.

4. **The reveal ladder (the spine).** ONE continuous experience in three rungs,
   no restart between them:
   - **Rung 1 — immersed.** You boot INSIDE an intro ROM that loosely MIRRORS
     the arcade room the data file defines; the game fills the screen.
   - **Rung 2 — the world reveal.** On the intro's win/exit condition the fourth
     wall breaks and eases you into the world THE DATA FILE DEFINES — you are
     standing at the arcade machine(s), their diegetic screens glowing with the
     games you were inside. The reveal itself LOADS/transitions into that world;
     no env var, no bare-default-room detour.
   - **Rung 3 — the editor reveal.** Later, a diegetic moment reveals that you
     can edit ROMs and the overworld itself (creator / world-sculpt / tracker /
     companions). Its DIEGETIC FORM is the **workbench** — a room-only terminal
     prop (an SDF shape in `OverworldFrameSource.BuildProgram`, never a paned
     cabinet — the four view slots are all spoken for) that stands DARK by
     default and POWERS ON when the editor reveal fires (the meta-victory
     "complete X games", or `reveal editor`): its screen panel lights with an
     emissive CRT glow eased in over a transition, so it reads as "the workshop
     opens." Once lit, walking up to it and pressing interact (North — the same
     proximity+interact machinery cabinets boot on, gated on the reveal via
     `OverworldWorld.IsPlayerNearWorkbench`) ENTERS world-sculpt — the diegetic
     door into "you can shape this world" (world-sculpt's first pad/diegetic
     entry; it was console-verb-only before). This is a NARRATIVE reveal for the
     player; the same authoring stays always-reachable from frame 0 via the
     console (`world` / `creator` / `tracker`) / Start for developers and agents
     — only the workbench entry is gated on the reveal.

5. **Headless flags are CI/proof twins.** `--forge-*` (build + self-verify a
   cart/asset), `--emit-schema`, `--validate-overworld`, and the review
   `--scenario` capture harness stay as headless entry points, but each names its
   in-game reflection. `--run` / the `world` graph kind / live DirectX hosting are
   a documented DEVELOPER/CI launch affordance (CLAUDE.md treats GPU backends as
   engine-contract), out of scope for in-session reachability this arc.

6. **In-game authoring is first-class and lossless.** The in-session
   author→forge→hot-swap loop routes through the FULL `puck.creation.v1` document
   (not a shapes-only export) and generalizes beyond avatars, so the forge
   capabilities are reachable in the one session, not only at the CLI.
   **REALIZED (the self-editing arcade arc, Stage 5).** The loop is now a
   SUBJECT-NEUTRAL registry (`src/Puck.Demo/Forge/ForgeSubject.cs`): one
   mechanism forges the AVATAR walker, the TUNE jukebox, and the SDF-ART SCENE
   creature — three clients, not three copies. `forge [avatar|scene]` bakes the
   creator's live creation into either cart; `tracker.forge` compiles the
   tracker's live tune into a jukebox cart (GPU-FREE — the tune is never gated
   behind device resolution, unlike the avatar/scene bakes); each hot-swaps into
   the nearest cabinet with no restart. The commit / lazy-forge / reload paths in
   `OverworldRenderNode` iterate the registry generically (any forged type is
   Cycle-reachable / lazy-forged, never a cabinet boot default).

**The former env → in-session migration:**

**The demo's entire `PUCK_*` surface has been REMOVED** — every row below now
reaches its capability through a console verb or a run-document field:

| Removed `PUCK_*` (demo) | Reached now by |
|---|---|
| `PUCK_OVERWORLD_WORLD` ✅ REMOVED | run-doc `world` field on the overworld node; live `world.load <name>` |
| `PUCK_OVERWORLD_CART` ✅ REMOVED | live `cart <i> <type>` console verb; live cabinet Cycle |
| `PUCK_OVERWORLD_CREATOR` / `PUCK_CREATOR_LOAD` ✅ REMOVED | `creator` / `creator.load <name>` |
| `PUCK_COMPANION_LOAD` / `_WIRE` / `_FACE` ✅ REMOVED | `companion.add` / `world.wire` / `companion.face` |
| `PUCK_OVERWORLD_DEBUG_REVEAL` ✅ REMOVED | `reveal` |
| `PUCK_OVERWORLD_DEBUG_BOOT` ✅ REMOVED | `boot <i>` |
| `PUCK_OVERWORLD_DEBUG_PLAYERS` ✅ REMOVED | `player.add` / `join <n>` |
| `PUCK_OVERWORLD_CAPTURE_FRAME` ✅ REMOVED | `capture <png>` after `step <n>` / `settle` (`--capture` now grabs frame 0) |
| `PUCK_LINK_CABLE_PROBE` ✅ REMOVED | `link <i> <j>` |
| `PUCK_WORLD_ROUNDTRIP` ✅ REMOVED | `world.verify` |
| `PUCK_CONSOLE_OPEN` ✅ REMOVED | stdin needs no open panel; the `console` verb opens it live |
| `PUCK_OVERWORLD_CELL` ✅ REMOVED | run-doc `cell` field on the overworld node |

**Migration status — the unification arc's CORE is COMPLETE** (rungs 1–2, the
console control plane, the env removal, and the lossless in-game forge all
landed and are verified by running + the full Post battery). What is BUILT:

- **Built:** the one data-driven path; always-immersed boot; the fourth-wall
  reveal INTO the data-file world (rung 2 — the run-doc `OverworldNode.World`
  field names the world, resolved + committed at boot); the
  stdin→console→registry transport with stdout echo (self-documenting `#`-comment
  scripts under [examples/scripts/](examples/scripts/)); creator / world-sculpt /
  tracker / companion authoring; the `world.load` / `world.save` / `world.wire` /
  `world.verify` / `companion.*` / `creator.*` (incl. `creator.place`) verbs;
  cabinet cart-cycle + the `cart <i> <type>` verb; the driving/observability
  verbs (`reveal`, `boot`, `player.add`, `capture`, `step`/`settle`, `link`,
  `state`); the run-doc `world` + `cell` fields; the **lossless** in-game avatar
  forge (byte-identical to `--forge-avatar-from`). **The demo's ENTIRE `PUCK_*`
  surface is REMOVED** — every former env var is now a console verb or a
  run-document field (see the migration table above).
- **2026-07-08 additions:** the `sdf` fullscreen SDF-inspection debug mode
  (`sdf.shape`/`sdf.shape2`/`sdf.blend`/`sdf.op`/`sdf.floor`/`sdf.scope`/
  `sdf.slice`/`sdf.cam`/`sdf.info` verbs) plus its `sdf.bench shapes | ops |
  instances | sweep` perf-bench instrument; grid-locking's `world.snap` /
  `creator.snap` verb family (on/off, pitch, rotation snap, align-to-shape
  reference, grid visibility) in both the world sculptor and creator mode,
  plus a RightShoulder chord toggling it live in each editor.
- **Next arc (the recursion — see "Next steps" below):** **rung 3**, the
  diegetic editor reveal, GATED on completing X arcade games (the 128-bit
  `meta` victory across cabinets) — the state latch + `EditorRevealed` unlock
  (Stage 1), each framework game writing its 128-bit victory share on win
  (Stage 2), and the DIEGETIC FORM + gated player entry (Stage 3: the
  **workbench** — a room-only terminal prop that lights up on the reveal and,
  once lit, is the diegetic North-interact door into world-sculpt; the dev path
  stays ungated), and **THE RECURSION** (Stage 4: the win/reveal conditions that
  gated the editor are themselves **re-forgeable live** — the `condition.*`
  console verbs edit a cabinet's exit + victory gate in-session, including the
  meta gate that unlocked the editor; see below) all landed. Still open: making
  the games/world **editable in-game** (Phase D — the general half of the
  in-game authoring loop); a diegetic/pad entry for tracker (world-sculpt now has
  the workbench). An optional run-doc per-console `startCart` field is a loose
  end (the live `cart` verb already covers the scriptable path).

  **The recursion — live-editable win/reveal conditions (`condition.*`).** A
  cabinet's exit + victory gate — `GamingBrickSource.Exit` (a WRAM
  address/op/value poll) and `.Victory` (the 128-bit SRAM gate: solo = region ==
  target, or meta = a group's cabinet shares XOR to target) — used to be
  immutable, load-once run-document data frozen into the child node at boot. It is
  now **re-forgeable live** through a `condition.*` console verb family (the same
  `IOverworldControlHost` / `CreatorFrameSource` control-plane seam the
  `reveal`/`link`/`cart` verbs ride): `condition.show <cabinet>` echoes the
  cabinet's current exit + victory gate; `condition.set <cabinet> exit
  <0xADDR><op><value>` sets/replaces the exit gate; `condition.set <cabinet>
  victory solo target=<guid>` / `… meta target=<guid> share=<guid> [group=<g>]`
  sets/replaces the victory gate; `condition.clear <cabinet> exit|victory`
  removes one. The child node's condition fields are now mutable
  (`GamingBrickChildNode.SetExitCondition`/`SetVictoryCondition`): a set
  re-parses the address/target/share, CLEARS the fired one-shot (a re-edited
  cabinet can win again), and for a meta victory RE-SEEDS the new share into the
  running machine's WRAM slot (0xC0F0) so the change takes effect on the running
  game with no reboot. The per-frame polls re-read the fields, so a swapped gate
  applies the next frame; a victory edit REBUILDS the room-level
  `MetaVictoryWatch` over the synced console-source records. **Re-validation
  policy:** a live edit that leaves a meta group's shares non-XOR-consistent is
  ACCEPTED (the group simply never fires) and WARNED to stderr, never refused —
  the dev/authoring path is never gated, so a self-locking edit is always
  recoverable; editing the meta gate does NOT re-lock an already-revealed editor
  (`EditorRevealed` is a one-shot session flag). **Persistence is a SEAM only,
  unwired this stage** (USER DECISION: no persistence for now, cloud saves
  near-future): a re-forged condition would serialize onto the world document's
  `cabinet:<n>` placement (`WorldDocument.cs`), the same seam a cloud save syncs
  — but `world.save` does not yet carry conditions, and the run-document schema
  is unchanged (conditions already exist on `GamingBrickSource`; live editing
  changes no schema).

This doc = what exists, how to drive it, and the settled next steps.
`Puck.Demo` is ONLY the game prototype: it carries no legacy mode or
demo-resident engine gate (`--world*`, `--validate`, `--validate-world*`,
`--validate-determinism`, `--validate-bindings`, `--fuzz-seed`) — that coverage
lives in the `Puck.Post` battery as mirrored stages. The demo is a
**game, not a test suite** — the one gate it keeps is its own
(`--validate-overworld`, which `Puck.Post` cannot host because the battery must
not reference the composition root).

## Status

The overworld's core capabilities — verified by `--validate-overworld`, the
Post battery, and the Humble battery — are:

- **The IMMERSED boot (the fourth wall).** `--rom <path> [--rom-exit
  "0xDA22>=1"]` (or `OverworldNode { immersed: true }`) opens INSIDE the game —
  the world compositor has a fifth viewport slot so the room plus FOUR
  console panes coexist; each connecting pad auto-seats its player at (boots
  + takes over) its own stand, panes tiling 1→2→3→4; when any machine's exit
  condition holds, the panes ease away and the ROOM is revealed — every
  active player standing at their machine, the games continuing on the
  diegetic screens, nothing reset. Seating/ownership is host-side
  routing; the determinism hash is untouched.
- **The reveal into a SCULPTED TOWN (the zoom-out).** The room the fourth-wall
  reveal eases you out INTO can be an authored `puck.world.v1` world, not the
  bare room — this is **rung 2** of the reveal ladder above. The world is named
  by the RUN DOCUMENT (the data file), on the `OverworldNode.World` field —
  `"world": "puckton"` — and is resolved + committed at boot, so the fourth-wall
  reveal reframes the camera OUT of the intro machines and INTO the world the
  data file defines. The former `PUCK_OVERWORLD_WORLD=<handle>` env var is
  REMOVED; the boot-load path is unchanged (`OverworldNode.World` →
  `OverworldFrameSource.LoadBootWorld` → the first tick-boundary
  `ConsumePendingWorldLoad`), only its source moved from the env var to the
  document (the live mid-session equivalent is the `world.load` console verb).
  Win the intro cartridge and the wall breaks to reveal you standing in the
  town. The reference town is
  **"Puckton"** (`src/Puck.Demo/Town/`, built + materialized by `--forge-town`
  — a cozy dusk block: an arcade with a glowing marquee behind the cabinets,
  storefronts across the street, a fountain plaza, lamp rows, trees; the
  flagship trio inhabit it as roaming companions). The reveal camera frames the
  loaded world's whole lot (`ScreenLayoutDirector.RoomFramingSource`; the
  default room's reveal is byte-unchanged). Verified by RUNNING: `--forge-town`
  then `--run docs/examples/overworld-town.json` loads the town (the boot-world
  narration prints on stderr), `world.verify` proves the committed walk grid ==
  a live save's, buildings block, the street is walkable, and the `reveal`
  console verb captures the reveal-into-the-town headlessly. A COMPACT block by
  necessity (~±9 bounds) — far geometry falls outside the reveal overview's
  reliable SDF render range.
- **Multiplayer console mode.** Connected pads beyond the first join as
  world players (pad index = slot, up to 4); each ACTIVE player has their
  OWN binding bar in the overlay (`BarCount` scales with players) and
  dispatches debug verbs at their own nearest console; pad-count drops evict
  with leaver hygiene.
- **Proximity takeover.** Any player's interact at a machine — unbooted:
  boots (and claims) it; booted + unowned: takes it over (their pad alone
  drives that brick; the brick leaves the shared timeline; a choir
  dissolves). The dedicated **Left bumper = Leave** disengages a seated
  player back to free room movement, releasing the brick to the timeline at
  the head — a bumper is not a GB joypad line, so disengage never collides
  with the machine the player is driving (interact does not double as
  release). Ownership is HOST-SIDE input routing, never hashed sim state —
  the overworld determinism hash is unaffected; verify via
  `--validate-overworld` (the printed hash is a stability signal only, not a
  documented constant — it churns roughly every arc).
- **Battery saves persist**: `<romPath>.sav` = SRAM + clock footer (MBC3:
  the standard 48-byte RTC layout; HuC3: a 16-byte own-convention block —
  no cross-emulator standard exists for it). Resume is deterministic — the
  footer's wall timestamp is foreign-emulator interop only, ignored on
  load. `runAs` is a BOOT-TIME cartridge-move policy (the save travels,
  the machine reboots), while the Bricks page's live DMG/CGB/AGB mode verbs
  snapshot-migrate the running machine without resetting it. The Bricks debug
  page also has a **"Clear save"** verb (North), which deliberately deletes
  the save and reboots fresh. Gate: the Humble battery's
  `battery-save` stage.
- **`--rom <path>` boots straight into a cartridge** — an IMMERSED overworld
  (`OverworldNode { immersed: true }`) whose four cabinets are all pre-inserted
  with that ROM, synthesized by `DemoRunDocuments` (a developer/CI convenience
  for the rung-1 immersed start; the same shape is expressible in a `--run`
  document). Gaming-brick sources carry data-driven FOURTH-WALL `exit`
  conditions (work-RAM address 0xC000–0xDFFF as an `"0x…"` string + op +
  value + label; the host polls after each stepped frame; first hold → the
  reveal). The condition targets a work-RAM flag the running cartridge sets —
  e.g. a save-progress byte going nonzero.
- **Victory conditions (`BrickVictoryCondition`).** A gaming-brick source may
  carry an optional 128-bit `victory` gate. After each stepped frame the host
  reads the top 16 bytes of the cartridge's highest SRAM address (bank `0x0F` of
  a 128 KiB MBC5 cart, read bank-independently via `ICartridge.ReadExternalRam`).
  `solo` wins a cabinet alone when its region reaches the gate constant; `meta`
  wins the room when the XOR of a group's cabinets reaches the target (shares
  authored so no single cabinet wins alone). Both break the fourth wall like
  `exit`. Gate math (order-independent bit-fill, subset-proof XOR) and the
  region read (highest-address, bank-independence) are covered today by the
  `victory-gate` / `victory-region` Post stages; see
  [examples/overworld-victory.json](examples/overworld-victory.json).
- **The default binding profile is purely overworld + debug**: the pages are
  Movement + Debug: Engine (RT→LT) + Debug: Bricks (LT→RT). Engine = actual
  SDF debug view modes; the LAST controller page holds all
  GamingBrick/fleet/capture controls.
- The three prototype-arc workstreams below (diegetic screens, the
  cartridge library, animated controls) are in place — labeled inline, kept
  as the design record (the cartridge-library item is itself historical: its
  shelf/carry mechanic was retired in favor of cabinet cart types). The
  screen-source seam is document DATA too (scene
  `screenSlab` + top-level `screenSources`, pinned by the Post
  `world-screen` stage — see
  [sdf-world-render-centralization-plan.md](sdf-world-render-centralization-plan.md)).

### Creator mode + the on-screen dev console

Both are host-side PRESENTATION — the deterministic sim/hash never sees any of
it:

- **Creator mode — the in-engine SDF editor.** Open the backtick console, type
  `creator` (a `DemoCommandModule` verb → `ICreatorModeHost` on the overworld
  root), and the mode takes over player slot 0 with the rich editor
  (`src/Puck.Demo/Creator/`): up to **64** placed shapes over a 16-slot
  material palette, authored inside the ±4-unit `WorkbenchRegion`, driven by
  four verb pages **Back cycles** — **SCULPT** (bumpers cycle the primitive,
  South places, East undoes, West resets, North exits), **SELECT** (bumpers
  cycle the selection, South duplicates, East deletes, West links into a
  composition group, North deselects), **STYLE** (bumpers material, South/East
  blend op, d-pad vertical smooth radius, West bake-style toggle), **ANIMATE**
  (bumpers step the frame-snapshot timeline — frame 0 = the rest pose — South
  records, East deletes the frame, West plays/stops, North rests). The global
  layer never changes: sticks/triggers always move/rotate/raise the TARGET
  (the selected shape, else the ghost), d-pad vertical scales, right-stick
  click is CAMERA MODE (orbit/zoom/pan), left-stick click toggles shape↔group
  scope. Verbs act on the selection; each shape carries its own blend op +
  smooth radius. Emission (`CreatorSceneRenderer`): ungrouped shapes are
  per-shape dynamic instances (plain Union by construction); a GROUP is ONE
  static instance bounded by the whole workbench region — the instance-cull
  contract made structural (never smooth-blend across a maskable instance
  boundary). Capacity rides the frame source's worst-case PROBE envelope
  (every screen lit, the creator pool in its largest form) — any new optional
  emission must join the probe. The overlay swaps to a creator binding bar
  (`BindingBarAdapter.PublishCreator` — the bumper/face icons remap to the
  active verb page) while the mode is up.
- **The workpiece camera + the live bake preview.** The engaged view is
  `ScreenLayoutDirector.CreatorCameraSource`: OBJECT intent orbits the workbench;
  SPRITE intent locks HEAD-ON from +Z against the matte backdrop —
  what-you-see-is-what-bakes. Beside the workbench stands the preview EASEL
  (a post + `ScreenSlab` borrowing screen-surface slot **index 3**; cabinet
  3's diegetic screen degrades to its lit flat material while the mode is up),
  whose panel shows the LIVE bake: `BakePreviewService` polls
  `CreatorScene.Revision` (12-produced-frame debounce, no wall clock),
  rasterizes one view per frame on the render thread, quantizes on a worker
  through the real `BakePipeline`, and publishes the brick-target image the
  slab samples (`ICreatorBakePreview` is the editor→bake seam; the easel
  reads as a powered-off panel until the first bake lands).
- **Creations are data.** The scene round-trips as a **`puck.creation.v1`**
  document under `./creations/<name>.creation.json` — name/intent/bakeStyle
  knobs, palette, groups, and the animation timeline frames all persist
  (legacy `.avatar.json` imports transparently), and `--forge-avatar-from`
  bakes a creation's timeline frames into an avatar cartridge's walk poses.
  The console-assist verbs (`CreatorCommandModule`) cover every edit exactly:
  `creator.list/new/save/load/select/name/material/palette/op/smooth/move/
  rot/scale/group/ungroup/intent/style/frame/play/stop/anim/baketarget/
  bakeoverlay`. Open the mode + load a creation live with the `creator` /
  `creator.load <name-or-path>` console verbs (the `--scenario` review harness
  is their headless twin — it opens creator and loads its `Scenario:Creation`).
- **The on-screen developer console.** `src/Puck.Demo/DevConsole/`: a
  GDI-rasterized monospace glyph atlas (`ConsoleGlyphFont`, Windows-only via
  System.Drawing.Common, degrades to the terminal console elsewhere) whose
  coverage AND the per-frame character grid both ride ONE storage buffer, so
  the `ConsoleOverlayNode` keeps the single-sampler + one-storage-buffer
  shape of the binding-bar overlay (no second texture). `DemoConsole`
  publishes its input line + output history to a `PublishBuffer<T>` the overlay
  renders; the backtick console open/close (or the `console` verb) drives its
  visibility.
- **Deferred (converge later):** authoring the action bar + console THROUGH
  the SDF VM (a screen-space/ortho path, eventually an MSDF glyph op mining
  Puck.Text) so one renderer owns all UI, retiring the separate overlay
  shaders.

### Native-panel camera framing

- **Square Trinitron CRT.** The diegetic screen shader (`sdf-world.hlsli` CRT
  constants) is FLAT and SQUARE — no pincushion curvature, near-square corners,
  a thin crisp bezel, no vignette, no glint, only a hint of scanline — so a game
  on a screen reads almost exactly like a real GB/GBA panel scaled up.
- **Native screen-fill framing.** A fully-close pane camera sits head-on at
  exactly the distance that makes the flat screen fill the viewport HEIGHT (the
  vertical FoV × the screen's half-height), pillar-boxed on wide panes — the
  "you're playing it on a big screen" look, for the immersed start AND the
  engaged secondary slices.
- **Reveal as a zoom-out.** The fourth-wall reveal eases the room camera OUT
  of the triggering machine's native-screen framing into a fixed, centered
  isometric-ish overview of the whole room (`ScreenLayoutDirector.BeginReveal`), so it
  reads as "pull back from the game you were inside to the whole room, everyone
  standing at their machines."
- **Engage → split.** After the reveal the room is the big PRIMARY slice across
  the top; each ENGAGED player (standing at a cabinet, controls routed to the
  brick — the takeover; Left bumper disengages to roam) gets a
  native-filled SECONDARY slice in the bottom strip. Nobody engaged = the room
  fullscreen. The layout eases smoothly as players engage/disengage.

Still open (unchanged direction): the live-camera child render node re-host
and cross-backend `graph.produce` (the sdf-world-render plan's phase 5), the
camera-as-GB-camera peripheral seam (next-step 1), and power-off/unboot
(next-step 4 — the takeover RELEASE is not a power-off; the layout does not
walk backward yet). Realtime promote/demote mode migration (next-step 2) has
its first form: the Bricks page changes a running machine's `ConsoleModel`
through Humble snapshot/restore, preserving emulated state and the timeline
cursor.

## What the demo is

`Puck.Demo` with no flags opens the OVERWORLD: a controller-driven player in a
room of **four bootable console cabinets** (`OverworldNode.MaxConsoles` = 4) —
the `dmg`/`cgb`/`agb` costumes of the ONE GamingBrick SM83 machine. Cabinets
start EMPTY: there is no shelf, no carrying, no pre-loaded showcase cartridge.
**North** (interact) inserts the cabinet's currently-selected cart and boots
it (a second North at a booted, unowned cabinet ejects it back to empty); the
**Right bumper (Cycle)** rotates the nearest cabinet's SELECTED cart type
through the roster (live-swaps it if the cabinet is already running) — eleven
types today: world-lens / camera / showcase / the player's forged avatar /
Volley / Brickfall / Chroma / Solitaire / Poker / the forged jukebox tune / the
forged SDF-art scene (`OverworldWorld.CartTypeCount` = 11; the last three —
avatar (3), jukebox (9), scene (10) — are the in-session FORGED subjects, baked
lazily so a forged type is Cycle-reachable but never a boot default; see
"Controls & running" below for the full type table and the Post/forge seams
behind each). Booting a cabinet in-world:

1. is **simulation state** — an interact press-edge near a cabinet sets a bit in
   `OverworldWorld.BootedMask` and appends to `BootOrder`; both are folded into
   `StateHash`, so boots (and cycles/ejects) replay bit-for-bit;
2. lights the cabinet's screen DIEGETICALLY (program rebuild at IDENTICAL
   instruction and material count — the boot swaps the powered-off dark box
   for a screen-surface slab whose face samples the brick's live framebuffer
   in-world);
3. powers the cabinet's `GamingBrickChildNode` (the MACHINE assembles when its
   cabinet's cartridge is known — at startup for a pre-inserted `--rom` boot,
   at insert time otherwise — and does not step a cycle until booted);
4. eases the screen layout through the staged keyframes (0.6 s smoothstep):
   **fullscreen → side-by-side → big-top/two-bottom → the 2×2 quad** as more
   cabinets boot. Pane order = boot order.

The room player's movement mirrors into every booted brick (directions + A on
jump) — walking the room walks the games; the carry-forward thesis on one
screen. The overworld game lives in `src/Puck.Demo/Overworld/`.

## Lockstep: the shared input timeline

Brick input rides ONE `OverworldBrickTimeline`: one `(ticks, joypad)` segment
per engine frame, recorded from the FIRST boot (the epoch). Every powered
brick consumes the same stream from its own cursor — a machine whose stand
booted late replays from the epoch, fast-forwarding up to
`GamingBrickChildNode.MaxSegmentsPerFrame` (4 ≈ 4× realtime) segments per
frame until it converges. Because a machine is a pure function of its
consumed stream, **same-costume machines end bit-identical no matter how far
apart their stands booted** — proven by a staggered-boot capture (CGB booted
at tick 480, AGB at 1500: panes pixel-identical after catch-up).

**The DMG pane drifts IN-GAME and that is CORRECT**: a dual-mode cartridge's mono code path
spends different frame counts on scenes than the color path, so identical
inputs land on different game states. Bit-lock is promised only where the
cartridge code path is identical (Cgb ↔ Agb). Do not "fix" the DMG drift.

### Fairness debuffs: `"speed": "dmg"` and `"runAs": "dmg"`

Every costume runs the ONE SM83 core at the DMG's 2²² T-cycles/s; the only
thing that ever makes a Color machine "faster" is the KEY1 double-speed
latch, where the tick→cycle bridge doubles the budget to match hardware
wall-clock pacing. A gaming-brick source's `speed` field selects the policy:
`"hardware"` (default, authentic) or `"dmg"` — the budget is PINNED to the
DMG rate regardless of KEY1, so every machine in the run consumes identical
cycle counts per engine tick (the budget becomes a function of config, never
of emulated state). Under fairness a double-speed section runs at half
wall-rate instead of gaining ground. A cartridge that never enables KEY1
behaves identically under either policy; the mode exists for double-speed
cartridges and the promote/demote arc below.

The FULL debuff (the great equalizer) is `"runAs": "dmg"`: the
costume (`model`, the stand's identity + accent) stays what it is, but the
machine BOOTS as the runAs capability — a Color stand seeds the DMG boot
handoff, so a dual-mode cartridge takes its **monochrome code path**. Every
demoted machine then runs the SAME code path as a real DMG, which means the
uniform debuff also restores full-fleet bit-lock: dmg ≡ demoted-cgb ≡
demoted-agb, pixel-exact (capture-proven across staggered boots —
`artifacts/overworld/mono-60.png`; even the authentic DMG drift vanishes because
everyone IS a DMG while debuffed). Any supported model is accepted, so a
promotion is just as expressible.

**Document-model gotcha (load-bearing):** a polymorphic-derived record
deserialized through the run-document parse path does NOT run property
initializers (the out-of-order-metadata handling creates instances without
the parameterless constructor) — an omitted member arrives NULL regardless of
any initializer. Optional document fields must be declared NULLABLE,
validated only when present, and normalized at consumption (`speed` and
`runAs` follow this pattern; see the note in `GamingBrickSource`).

## Presentation invariants (hard-won — keep)

- The render origin is the room's SPAWN ANCHOR, always. The room is bounded,
  so a fixed anchor keeps floats small AND immovable; an anchor that follows
  the players (the unbounded-world snap-grid pattern) flips across grid lines
  at the room's center and makes the smoothed camera race the jump.
- Stick-up must map to world −Z in `PlayerIntent` (the chase camera looks
  toward −Z) — the same negation `LocalIntentSource` applies — while the
  brick MIRROR keeps the raw stick sense (stick-up = joypad Up).

## Controls & running

Console mode rides the binding-page system (the on-disk profile is the
source of truth — `[bindings]` logs its path; the binding bar renders the
active page). The default profile:

- Left stick = walk (always-on) · d-pad = walk only where the active page
  leaves it unbound · **South** = jump · **North** = interact/boot (North,
  not East — East is the GB joypad's B, so a boot press never leaks into a
  running game) · **West** = CONTEXTUAL: held away from stands it is the
  hold-to-run (tuning `SprintMultiplier`, default 1.6×); pressed at
  an unbooted stand it activates (boots) it — the bar's icon swaps to
  interact while in range. West is not a GB joypad line either.
- **Left bumper** = **Leave**: a player seated at / driving a console
  disengages and rejoins free room movement (releasing the brick to the
  timeline at the head). A bumper is not a GB joypad line, so the way out
  never collides with the machine being driven. Held off while immersed and
  not yet revealed (no room to walk into until the fourth wall breaks).
- **Right bumper** = **Cycle**: advance the nearest cabinet's selected cart
  TYPE (at a booted cabinet the swap is live). Eleven types cycle — 0
  world-lens / 1 camera / 2 showcase / 3 the player's forged avatar /
  **4 Volley / 5 Brickfall / 6 Chroma** (the three genuine hand-authored SM83
  arcade carts) / **7 Solitaire / 8 Poker** (the card games) / **9 the forged
  jukebox tune / 10 the forged SDF-art scene** (the two new in-session forged
  subjects — `tracker.forge` and `forge scene`); North inserts the selection
  into an empty cabinet and ejects a running one. Types 3, 9, 10 are the FORGED
  subjects — baked lazily the first time a cabinet wants one (`ForgeSubject`
  registry), Cycle-reachable but never a boot default. The sim tracks only the
  type index — the ROM bytes are host-side in the render node's cart table. Brickfall is battery-backed: its
  high-score SRAM persists at `%LOCALAPPDATA%\Puck\Demo\brickfall.sav`
  (`BrickfallRom.PrepareDefaultSavePath`), and it SDF-bakes its title screen
  on the live GPU (the hand-authored banner is the no-GPU fallback).
- LT/RT participate only in the two debug chords below.
- **Hold RT then LT** = **Debug: Engine** — actual SDF debug view modes:
  d-pad up = depth, right = ray direction, down = iteration count, left =
  material id, West = normals, North = final/off.
- **Hold LT then RT** = **Debug: Bricks** — the LAST controller page. West
  toggles the FAIRNESS speed pin on the nearest booted console (debuff/buff;
  unparks choir members first — the divergence event); d-pad left/down/up is
  the LIVE device swap to dmg/cgb/agb — dmg↔cgb↔agb, every direction, NO boot.
  With a per-ROM recipe it is a GENUINE hardware swap: `Machine.SwitchModel`
  re-gates the emulated color path live (mutable `m_supportsColor` behind an
  `IModeSwitchable` seam), and on a Color→mono demote repages the switchable
  RAM to its DMG-equivalent banks (SVBK=1/VBK=0, the Color banks survive
  un-paged, cartridge-move style) and drops double speed. Then it POKES the
  game's cached hardware-detection flag (the "boot shim" — a dual-mode cartridge's
  cached colour-detection byte, e.g. HRAM 0xFFE8, found by the differential-boot finder) so the running
  game re-detects and re-renders in the new mode's OWN authored art — its
  shared-RAM progress untouched. Validated on a real dual-mode cartridge: CGB→DMG→CGB drops
  to exactly the four DMG shades and returns to color, reversibly, and the
  swapped model survives Snapshot/Fork. A ROM with no recipe degrades to a
  presentation-only re-interpretation (the framebuffer desaturates). A swap TO
  color runs a short monochrome "bridge" until the game re-draws its palettes.
  North CLEARS the persisted battery save (delete + reboot, by
  design); South logs the world state hash, East logs fleet status, Right
  Shoulder captures the next frame to `artifacts/overworld/`.
- Console mode is MULTIPLAYER: extra pads join as world players (pad index =
  slot, up to 4), each with their own binding bar and their own debug-verb
  targeting; interact at a booted stand is the proximity-takeover claim, Left
  bumper is the disengage (Status above).
- Live: `dotnet run --project src/Puck.Demo -c Release` (the default auto-exit
  is 30 s; `--exit-after-seconds 0` or less runs until the window is closed;
  the headless smoke is `--exit-after-seconds 2`). Explicit document:
  [examples/overworld.json](examples/overworld.json); graph shape
  `{"$type": "overworld", "consoles": [{model, romPath?} × ≤4], "library": [{title, romPath} × ≤8]?}`
  (a console omitting `romPath` starts EMPTY until a cart is inserted at the
  cabinet).
- The synthesized default degrades to the **bare room** (with a stderr note)
  when the showcase ROM path is absent on the machine; an explicit `--run`
  document stays strict. The bare room (`"consoles": []`) is the plain
  multi-controller mode — 4-player pools, controller join/leave, roster-churn
  replay all live there.

## Key seams (where to change what)

| Concern | Where |
|---|---|
| Room/stand geometry, interact range | `OverworldRoom` (authored floats) → `FixedRoom.From` (fixed-point clamps + expanded stand keep-outs) |
| Boot rules, hashing, replay | `OverworldWorld` (`Boot`, `TryBootNearest`, `HashState`) |
| Layout keyframes + transition feel | `ScreenLayoutDirector` (`BuildTargets`, `PaneRect`, `TransitionSeconds`) |
| Stand visuals / accents | `OverworldFrameSource.BuildProgram` (accents passed per console model) |
| The editor-reveal WORKBENCH (rung 3 diegetic form) | prop + eased glow: `OverworldFrameSource.EmitWorkbench` / `AdvanceWorkbenchGlow` (lit on `EditorRevealed`); gated entry: `OverworldWorld.IsPlayerNearWorkbench` + `OverworldRenderNode.TryEnterWorkbench` → `SetWorldSculptMode`. Room-only (never a paned cabinet); joins the capacity probe |
| Boot switch + pane rendering | `GamingBrickChildNode.PowerSource` + fixed allocation extent (alloc once at full frame; per-frame dispatch at the live rect — animated regions must NEVER reallocate GPU images) |
| Input routing / mirror | `GamingBrickPadService` (sole gamepad drainer) + `OverworldRenderNode.AdvanceConsoleMode` |
| Lockstep timeline / catch-up rate | `OverworldBrickTimeline` (segments + cursors) + `GamingBrickChildNode.SegmentSource` / `MaxSegmentsPerFrame` |
| Choir park/mirror (identical-machine consoles share one stepped machine once converged; `ContentEquals`-verified at park) | `OverworldRenderNode.ParkConvergedChoirMembers` + `GamingBrickChildNode.TryParkBehind`; example [examples/overworld-choir.json](examples/overworld-choir.json) |
| Document model | `OverworldNode` in `Puck.Scene/NodeDocument.cs` |

## Verifying

`--validate-overworld` (determinism + replay + planet-scale invariance, now
including console boots in the hash stream); everything else is the
`Puck.Post` battery — engine determinism, paged bindings, fixed-point and
world-coordinate self-tests, cross-backend world parity, and the differential
fuzzer are all Post stages now.

Headless deterministic captures and scripted verification are **console-driven**
(rule 3 of the unification contract): pipe a verb script into a run over stdin
(`cart 0 4`, `boot 0`, `step 240`, `reveal`, `capture shot.png`,
`world.verify`, `companion.add lantern-fish`, …) and read the echoed results on
stdout. The demo's ENTIRE `PUCK_*` env recipe was removed — every former var is
now one of these verbs or a run-document field (see the migration table in the
unification contract above).

## The prototype arc (the design record)

The demo's loop today: **walk the room, insert/cycle a cart at a cabinet,
play it on the device's own screen** (there is no shelf or carrying — that
mechanic was tried and retired; item 2 below is its historical record). Three
workstreams, each independently shippable — all three are in
place (the "What the demo is" section above describes the behavior; the designs
below are the record):

1. **Diegetic screens — bricks render in-world.** *(In place — the shading
   half plus the document-data seam: scene `screenSlab` + top-level
   `screenSources`, pinned by the Post `world-screen` stage. CRT GLOW + FACE:
   each booted screen renders a CRT glass face — barrel curvature, rounded dark
   bezel, native-line scanlines, vignette, fresnel glint, bloom knee — in
   `sampleScreenSurface`, AND emits colored light into the room, its per-frame
   framebuffer average summed with the sun in the world shade loop via the
   binding-11 `sdfScreenLights` buffer (`SetScreenLight` +
   `SdfFrame.AmbientScale/SunScale` dimming for mood). Additive; the shade
   funnel is `float3` radiance. Deferred: CRT/ambient params as document
   data, and screen-light shadows.)* The room is
   SDF-VM-rendered end to end (`OverworldFrameSource.BuildProgram` →
   `SdfEngineNode` → `SdfWorldEngine`), and every stand carries a
   `ScreenSlab` shape with the reserved screen material
   (`SdfProgramBuilder.ScreenMaterialId`). The shading design: extend
   **Stage 1's shading** (not the distance field — the SDF geometry never
   needs the texture) so a `ScreenSlab` hit maps its hit point through the
   slab's local frame to a UV and samples the brick's child storage image,
   which `SdfWorldEngine` binds per frame for Stage 2
   (`SetChildSource`/`BindSources`). Unbooted slabs keep the dark screen
   material. The 2D pane easing (`ScreenLayoutDirector`) coexists as the
   zoom/focus view — diegetic screens are how the room looks; panes are how
   you play seriously. Verification: a `Puck.Post` Tier-B/C stage that
   renders a world with a synthetic child image on a screen slab and asserts
   sampled pixels (the `WorldChildStage` pattern, one step further in).
2. **HISTORICAL — the cartridge library / shelf-and-carry mechanic (RETIRED,
   superseded by cabinet cart types).** *This whole item describes a
   physical pick-up/carry/insert loop that was designed, then abandoned in
   favor of the simpler per-cabinet Cycle button — CLAUDE.md is explicit:
   "there is no shelf or carrying." Kept here only as design provenance, not
   as a description of current behavior.* The cartridge roster survives as
   the cabinet cart-type cycle instead: eleven types today
   (`OverworldWorld.CartTypeCount`), cycled at the cabinet with the Right
   bumper (see "Controls & running" above; types 4–8 are the five
   hand-authored SM83 games — Volley/Brickfall/Chroma/Solitaire/Poker; types 3,
   9, 10 are the in-session forged avatar/jukebox/scene subjects); the
   `ShelfSlot` geometry, where it still exists, is optional static furniture
   with no carried cartridges. The abandoned design: `OverworldNode` would
   carry a `library` (cartridge entries: id, title, `romPath`, and later a
   `peripheral` field) and a shelf along a wall (`OverworldRoom` slots, same
   keep-out treatment as cabinets); the sim loop would extend the interact
   seam (`OverworldWorld.TryBootNearest` generalizing to nearest-interactable:
   shelf slot vs. cabinet): pick up (near shelf, hands empty) → carry (the
   cartridge rides the player's dynamic-transform slot, visible in-world) →
   insert (near cabinet, hands full; cabinet's cartridge becomes sim state) →
   boot. Carried-item and per-cabinet inserted-cartridge ids would fold
   into `StateHash` so the whole loop replays bit-for-bit.
3. **Animated controls — buttons and sticks that move.** *(In place — each
   stand's control cluster rides per-stand dynamic-transform slots, driven
   by the joypad state the machine consumes.)* Each brick's
   buttons/d-pad/stick are small SDF shapes on `TransformDynamic` slots
   (32 bytes each, per-frame data) driven by the same joypad state
   the machine consumes — the mirror made visible. Pure content + a handful
   of dynamic slots; no engine work.

### Performance reality check

The prototype content exposes the world renderer's scaling wall: 206 ms/frame
at 1280×800 for ~45 SDF instructions + ~24 dynamic slots, 96% in the
per-pixel views kernel. The diagnosis: the original
avatars VM had per-object **instancing** (instance table, per-tile bitmasks,
march chord clamp per instance) — a ray evaluates only objects overlapping
its tile, not the full VM. The port kept the outer shell (tile prepass) but
dropped the load-bearing mechanism. Two fixes address it: (1) exact
Union bounding-sphere skip + host-baked bounds table (206 ms → 71 ms); (2)
the instancing layer (`BeginInstance`/`BeginInstanceDynamic`/`EndInstance` on
the builder, 1024-instance ceiling with a derived ceil(count/32)-word per-tile
mask from the beam prepass, world set always evaluated via a merged
world-segment list — map() costs O(world + visible instances' segments) —
zero-instance programs byte-identical) with the `world-instanced` Post stage
proving instanced ≡ flat pixels (bit-identical on Vulkan) on a 76-instance,
3-mask-word scene. `OverworldFrameSource.BuildProgram` declares ~23 instances (one per
stand incl. its control cluster, per shelf slot, per player, per cartridge).
⚠️ All numbers above are from the SURFACE (Iris Xe — the box that exposed
the wall); the dev-box (RTX 4070) acceptance run against the ≤10 ms views
target is OPEN (details: machine-fleet-plan lever 0). On the Surface the
remaining playability lever is internal render scale, not more culling. This
instancing substrate is what the fleet arc scales on.

## Next steps (carried arcs, unchanged direction)

0. **The zoom-out arc.** *(The reveal-ladder CORE is DONE — see rule 4 "The
   reveal ladder" above and the self-editing arcade: a session opens IMMERSED
   inside a ROM (rung 1), the fourth wall breaks into the data-file world
   (rung 2), and completing X arcade games unlocks the editor via the 128-bit
   meta victory (rung 3). What REMAINS of this arc:)* ROMs as world items
   ("insert" = a host-side mechanic, e.g. a watched mount point), in-ROM pickups
   that promote the real device (needs the machine→engine event seam), and
   link-cable trading. Workload + seam consequences live in
   [machine-fleet-briefing.md](machine-fleet-briefing.md) §1.
1. **PC camera as a GB Camera.** The camera feed reaches the emulated
   machines through a **cartridge-sensor seam** — a future `peripheral` field
   on an `OverworldNode.Consoles` entry — NOT another viewport pane (the four
   view slots are fully spoken for: room + three panes). Engine side: the
   existing camera-capture service already delivers CPU ARGB frames; brick
   side: the Pocket Camera mapper is in the Humble cart backlog
   ([ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) — cart
   completeness). **The custom ROM that reads the sensor is deliberately not
   designed yet.**
2. **Device promote/demote (the "system power-up").** *(First form in place —
  the Bricks debug page's DMG/CGB/AGB mode verbs snapshot
  the running Humble machine, rebuild it under the requested `ConsoleModel`,
  restore the snapshot, and keep the timeline cursor/cycle remainder in
  place. This is live state migration, not reboot-as.)* The long-term arc
  is larger: a player's device upgrades or downgrades in REALTIME —
  mushroom-style — moving their running game between costumes as a game
  mechanic. The pieces it builds on are in place: one shared core
  parameterized by `ConsoleModel`, the Humble machine's full mid-frame
  `Snapshot()/Restore()`, the epoch timeline (cycle-aligned machines), and
  the fairness knobs (`speed`/`runAs` are the boot-time half). Still open:
  game-design rules for ROMs that intentionally probed hardware at boot,
  large fleets, diegetic world-lens machines (ROMs found in the world; one
  renders a top-down view of the player THROUGH the device, revealing hidden
  things), and recursive machine-feeds-machine composition. This is what the
  fleet performance work must serve:
   [machine-fleet-briefing.md](machine-fleet-briefing.md).
3. **Multi-controller console mode.** *(Done — see the Status: pads beyond
   the first join as world players with their own binding bars; the proximity
   takeover goes further than this step asked, giving any player exclusive
   control of a booted brick.)* The pad-service extension lets extra pads
   join the room while bricks stay routed.
4. **Power-off / re-boot.** Booting is one-way; unbooting and
   re-transitioning the layout downward is the inverse walk of the same
   staged keyframes. (A re-boot also needs a timeline-cursor reset
   policy: rejoin at the head, or replay from the epoch again — the
   takeover RELEASE settles the head-rejoin precedent for a
   machine returning to the shared timeline, but power-off itself is still
   open.)
5. The pre-overworld deferred list (record/replay CLI, network intent source)
   carries over unchanged from the foundation game's design. Jumbotron
   diegetic screens graduate into prototype-arc item 1 above.

Provenance: the four-quad showcase (commit f846073) is the static precursor;
its quad layout is the overworld's end state.
