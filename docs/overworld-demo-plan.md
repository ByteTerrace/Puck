# The overworld — Puck.Demo's plan of record

Status: **shipped and verified** (2026-07-02) · **purified** (2026-07-03) ·
**multiplayer, persistent saves, direct ROM boot** (2026-07-04 — status
block below) · this doc = what exists, how to drive it, and the settled
next steps.
`Puck.Demo` is now ONLY the game prototype: the purification deleted every
legacy mode and demo-resident engine gate (`--world*`, `--validate`,
`--validate-world*`, `--validate-determinism`, `--validate-bindings`,
`--fuzz-seed` and their ~28 backing files) — that coverage lives in the
`Puck.Post` battery, where it already had mirrored stages. The demo is a
**game, not a test suite** — the one gate it keeps is its own
(`--validate-overworld`, which `Puck.Post` cannot host because the battery must
not reference the composition root).

## Status (2026-07-04): console mode goes multiplayer; saves persist; ROMs boot direct

Landed this date, all green (`--validate-overworld` + the Post battery + the
Humble battery):

- **The IMMERSED boot (the fourth wall).** `--rom <path> [--rom-exit
  "0xDA22>=1"]` (or `OverworldNode { immersed: true }`) opens INSIDE the game —
  the world compositor grew a fifth viewport slot so the room plus FOUR
  console panes coexist; each connecting pad auto-seats its player at (boots
  + takes over) its own stand, panes tiling 1→2→3→4; when any machine's exit
  condition holds, the panes ease away and the ROOM is revealed — every
  active player standing at their machine, the games continuing on the
  diegetic screens, nothing reset. Seating/ownership stays host-side
  routing; the determinism hash is untouched.
- **Multiplayer console mode.** Connected pads beyond the first join as
  world players (pad index = slot, up to 4); each ACTIVE player has their
  OWN binding bar in the overlay (`BarCount` grew) and dispatches debug
  verbs at their own nearest console; pad-count drops evict with leaver
  hygiene. Next-step 3 below is DONE.
- **Proximity takeover.** Any player's interact at a machine — unbooted:
  boots (and claims) it; booted + unowned: takes it over (their pad alone
  drives that brick; the brick leaves the shared timeline; a choir
  dissolves). The dedicated **Left bumper = Leave** disengages a seated
  player back to free room movement, releasing the brick to the timeline at
  the head — a bumper is not a GB joypad line, so disengage never collides
  with the machine the player is driving (interact no longer doubles as
  release). Ownership is HOST-SIDE input routing, never hashed sim state —
  the overworld determinism hash is unchanged (0x47DA634C1658D2CE).
- **Battery saves persist**: `<romPath>.sav` = SRAM + clock footer (MBC3:
  the standard 48-byte RTC layout; HuC3: a 16-byte own-convention block —
  no cross-emulator standard exists for it). Resume is deterministic — the
  footer's wall timestamp is foreign-emulator interop only, ignored on
  load. `runAs` remains a BOOT-TIME cartridge-move policy (the save travels,
  the machine reboots), while the Bricks page's live DMG/CGB/AGB mode verbs
  snapshot-migrate the running machine without resetting it. The Bricks debug
  page also grew a **"Clear save"** verb (North), which deliberately still
  deletes the save and reboots fresh. Gate: the Humble battery's
  `battery-save` stage.
- **`--rom <path>` boots straight into a cartridge** — a fullscreen
  one-machine `world` document synthesized by `DemoRunDocuments` — and
  gaming-brick viewport sources carry data-driven FOURTH-WALL `exit`
  conditions (work-RAM address 0xC000–0xDFFF as an `"0x…"` string + op +
  value + label; the host polls after each stepped frame; first hold →
  clean shutdown). Example:
  [examples/pokemon-gold.json](examples/pokemon-gold.json) (starter
  selection = `wPartyCount` 0xDA22 ≥ 1).
- **The default binding profile is purely overworld + debug**: the placeholder
  "Actions I/II" pages and the `demo.action`/`demo.target`/`demo.interact`
  vocabulary were DELETED; the pages are Movement + Debug: Engine (RT→LT)
  + Debug: Bricks (LT→RT). Engine = actual SDF debug view modes; the LAST
  controller page holds all GamingBrick/fleet/capture controls.
- The three prototype-arc workstreams below (diegetic screens, the shelf,
  animated controls) landed 2026-07-03 — labeled inline, kept as the design
  record. The screen-source seam is now document DATA too (scene
  `screenSlab` + top-level `screenSources`, pinned by the Post
  `world-screen` stage — see
  [sdf-world-render-centralization-plan.md](sdf-world-render-centralization-plan.md)).

## Status (2026-07-05): creator mode + the first on-screen dev console

Landed this date (user-confirmed working end-to-end), all host-side
PRESENTATION — the deterministic sim/hash never sees any of it:

- **Creator mode — the in-engine SDF authoring surface.** Open the backtick
  console, type `creator` (a `DemoCommandModule` verb → `ICreatorModeHost` on
  the overworld root), and the mode takes over player slot 0: the left stick
  slides a bright ghost shape across the floor (triggers raise/lower), the
  bumpers cycle the primitive (sphere/box/torus/cylinder/capsule/ellipsoid),
  South places it, East undoes, North exits. Shapes are a reserved pool of
  dynamic-transform instances in `OverworldFrameSource` (1 ghost + 24 placed,
  present from frame 0, hidden below the floor when unused) so the engine's
  once-sized program buffers reserve their capacity up front — a cycle/place
  is a byte-length-constant program rebuild (every primitive is one SDF
  instruction of identical word size, the same discipline as the boot
  rebuild), a MOVE is just a per-frame transform. The overlay swaps to a
  creator binding bar (`BindingBarAdapter.PublishCreator` + five new procedural
  icons) while the mode is up.
- **The first on-screen developer console.** `src/Puck.Demo/DevConsole/`: a
  GDI-rasterized monospace glyph atlas (`ConsoleGlyphFont`, Windows-only via
  System.Drawing.Common, degrades to the terminal console elsewhere) whose
  coverage AND the per-frame character grid both ride ONE storage buffer, so
  the `ConsoleOverlayNode` keeps the proven single-sampler + one-storage-buffer
  shape of the binding-bar overlay (no second texture). `DemoConsole` now
  publishes its input line + output history to a `ConsoleTextStore` the overlay
  renders; the backtick console open/close drives its visibility.
  `PUCK_CONSOLE_OPEN=1` starts it open with seeded lines (a headless font check).
- **Deferred (user-chosen "converge later"):** authoring the action bar +
  console THROUGH the SDF VM (a screen-space/ortho path, eventually an MSDF
  glyph op mining Puck.Text) so one renderer owns all UI, retiring the separate
  overlay shaders.

## Status (2026-07-05): the OVERWORLD rename + a native-panel camera rework

- **The arcade is now THE OVERWORLD.** A full rename (`Puck.Demo.Arcade`→
  `.Overworld`, every `Overworld*` type, `$type: "overworld"`, `--overworld` /
  `--validate-overworld`, `PUCK_OVERWORLD_*`, `overworld.*` commands, these docs).
  `--validate-overworld` passes (determinism + replay + planet-scale). The
  emulator-side GB/GBA→GamingBrick rename is a separate concurrent effort.
- **Square Trinitron CRT.** The diegetic screen shader (`sdf-world.hlsli` CRT
  constants) went FLAT and SQUARE — no pincushion curvature, near-square corners,
  a thin crisp bezel, no vignette, no glint, only a hint of scanline — so a game
  on a screen reads almost exactly like a real GB/GBA panel scaled up.
- **Native screen-fill framing.** A fully-close pane camera now sits head-on at
  exactly the distance that makes the flat screen fill the viewport HEIGHT (the
  vertical FoV × the screen's half-height), pillar-boxed on wide panes — the
  "you're playing it on a big screen" look, for the immersed start AND the
  engaged secondary slices.
- **Reveal as a zoom-out.** The fourth-wall reveal now eases the room camera OUT
  of the triggering machine's native-screen framing into a fixed, centered
  isometric-ish overview of the whole room (`ScreenDirector.BeginReveal`), so it
  reads as "pull back from the game you were inside to the whole room, everyone
  standing at their machines."
- **Engage → split.** After the reveal the room is the big PRIMARY slice across
  the top; each ENGAGED player (standing at a cabinet, controls routed to the
  brick — the existing takeover; Left bumper disengages to roam) gets a
  native-filled SECONDARY slice in the bottom strip. Nobody engaged = the room
  fullscreen. The layout eases smoothly as players engage/disengage.

Still open (unchanged direction): the live-camera child render node re-host
and cross-backend `graph.produce` (the sdf-world-render plan's phase 5), the
camera-as-GB-camera peripheral seam (next-step 1), and power-off/unboot
(next-step 4 — the takeover RELEASE is not a power-off; the layout never walks
backward yet). Realtime promote/demote mode migration (next-step 2) has its
first shipped form: the Bricks page changes a running machine's `ConsoleModel`
through Humble snapshot/restore, preserving emulated state and the timeline
cursor.

## What the demo is

`Puck.Demo` with no flags opens the OVERWORLD: a controller-driven player in a
16×16 room with **three bootable console stands** along the far wall — the
showcase cartridge (Pokémon Gold) loaded into the `dmg`, `cgb`, and `agb`
costumes of the ONE GamingBrick SM83 machine. Booting a stand in-world:

1. is **simulation state** — an interact press-edge near a stand sets a bit in
   `OverworldWorld.BootedMask` and appends to `BootOrder`; both are folded into
   `StateHash`, so boots replay bit-for-bit;
2. lights the stand's screen DIEGETICALLY (program rebuild at IDENTICAL
   instruction and material count — the boot swaps the powered-off dark box
   for a screen-surface slab whose face samples the brick's live framebuffer
   in-world);
3. powers the stand's `GamingBrickChildNode` (every cartridge ROM loads
   eagerly at document load for fail-fast, but the MACHINE assembles only
   when its stand's cartridge is known — at startup for pre-inserted
   consoles, at insert time for shelf cartridges — and does not step a cycle
   until booted);
4. eases the screen layout through the staged keyframes (0.6 s smoothstep):
   **fullscreen room → room|pane side-by-side → big room top over two small
   panes → the 2×2 quad**. Pane order = boot order.

The room player's movement mirrors into every booted brick (directions + A on
jump) — walking the room walks the games; the carry-forward thesis on one
screen. "MiniAction" is a retired name: that game was the foundation and is
now simply the demo (`src/Puck.Demo/Overworld/`).

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

**The DMG pane still drifts IN-GAME and that is CORRECT** (settled
2026-07-02, re-verified here): Gold's mono code path spends different frame
counts on scenes than the color path, so identical inputs land on different
game states. Bit-lock is promised only where the cartridge code path is
identical (Cgb ↔ Agb). Do not "fix" the DMG drift.

### Fairness debuffs: `"speed": "dmg"` and `"runAs": "dmg"`

Every costume runs the ONE SM83 core at the DMG's 2²² T-cycles/s; the only
thing that ever makes a Color machine "faster" is the KEY1 double-speed
latch, where the tick→cycle bridge doubles the budget to match hardware
wall-clock pacing. A gaming-brick source's `speed` field selects the policy:
`"hardware"` (default, authentic) or `"dmg"` — the budget is PINNED to the
DMG rate regardless of KEY1, so every machine in the run consumes identical
cycle counts per engine tick (the budget becomes a function of config, never
of emulated state). Under fairness a double-speed section runs at half
wall-rate instead of gaining ground. Gold never enables KEY1, so today's
showcase behaves identically under either policy; the mode exists for
Crystal-class cartridges and the promote/demote arc below.
Example: [examples/overworld-fair.json](examples/overworld-fair.json).

The FULL debuff (the Mario-Kart lightning bolt) is `"runAs": "dmg"`: the
costume (`model`, the stand's identity + accent) stays what it is, but the
machine BOOTS as the runAs capability — a Color stand seeds the DMG boot
handoff, so a dual-mode cartridge takes its **monochrome code path**. Every
demoted machine then runs the SAME code path as a real DMG, which means the
uniform debuff also restores full-fleet bit-lock: dmg ≡ demoted-cgb ≡
demoted-agb, pixel-exact (capture-proven across staggered boots —
`artifacts/overworld/mono-60.png`; even the authentic DMG drift vanishes because
everyone IS a DMG while debuffed). Any supported model is accepted, so a
promotion is just as expressible.
Example: [examples/overworld-mono.json](examples/overworld-mono.json).

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
  Mario hold-to-run (tuning `SprintMultiplier`, default 1.6×); pressed at
  an unbooted stand it activates (boots) it — the bar's icon swaps to
  interact while in range. West is not a GB joypad line either.
- **Left bumper** = **Leave**: a player seated at / driving a console
  disengages and rejoins free room movement (releasing the brick to the
  timeline at the head). A bumper is not a GB joypad line, so the way out
  never collides with the machine being driven. Held off while immersed and
  not yet revealed (no room to walk into until the fourth wall breaks).
- The single-modifier "Actions I/II" placeholder pages were DELETED
  (2026-07-04) along with their `demo.*` command vocabulary — LT/RT now
  participate only in the two debug chords below.
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
  game's cached hardware-detection flag (the "boot shim" — Pokémon Gold's
  `hCGB` at HRAM 0xFFE8, found by the differential-boot finder) so the running
  game re-detects and re-renders in the new mode's OWN authored art — its
  shared-RAM progress untouched. Empirically proven on Gold: CGB→DMG→CGB drops
  to exactly the four DMG shades and returns to color, reversibly, and the
  swapped model survives Snapshot/Fork. A ROM with no recipe degrades to a
  presentation-only re-interpretation (the framebuffer desaturates). A swap TO
  color runs a short monochrome "bridge" until the game re-draws its palettes.
  North CLEARS the persisted battery save (delete + reboot, by
  design); South logs the world state hash, East logs fleet status, Right
  Shoulder captures the next frame to `artifacts/overworld/`.
- Console mode is MULTIPLAYER (2026-07-04): extra pads join as world
  players (pad index = slot, up to 4), each with their own binding bar
  and their own debug-verb targeting; interact at a booted stand is the
  proximity-takeover claim, Left bumper is the disengage (status block above).
- Live: `dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 0`
  (the default auto-exit is 30 s). Explicit document:
  [examples/overworld.json](examples/overworld.json); graph shape
  `{"$type": "overworld", "consoles": [{model, romPath?} × ≤3], "library": [{title, romPath} × ≤8]?}`
  (a console omitting `romPath` starts EMPTY and is fed from the shelf —
  [examples/overworld-shelf.json](examples/overworld-shelf.json)).
- The synthesized default degrades to the **bare room** (with a stderr note)
  when the showcase ROM path is absent on the machine; an explicit `--run`
  document stays strict. The bare room (`"consoles": []`) is the old
  multi-controller mode — 4-player pools, controller join/leave, roster-churn
  replay all live there.

## Key seams (where to change what)

| Concern | Where |
|---|---|
| Room/stand geometry, interact range | `OverworldRoom` (authored floats) → `FixedRoom.From` (fixed-point clamps + expanded stand keep-outs) |
| Boot rules, hashing, replay | `OverworldWorld` (`Boot`, `TryBootNearest`, `HashState`) |
| Layout keyframes + transition feel | `ScreenDirector` (`BuildTargets`, `PaneRect`, `TransitionSeconds`) |
| Stand visuals / accents | `OverworldFrameSource.BuildProgram` (accents passed per console model) |
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
fuzzer are all Post stages now. Headless layout captures:
`PUCK_OVERWORLD_DEBUG_BOOT=240,480,720` + `PUCK_CAPTURE_FRAME` + `--capture`
(env details in [agent-guide.md](agent-guide.md#environment-variables)).

## The prototype arc (LANDED 2026-07-03 — kept as the design record)

The purified demo bends toward one loop: **walk the room, take a game off
the shelf, carry it to a brick, insert it, play it on the device's own
screen.** Three workstreams, each independently shippable — **all three
landed 2026-07-03** (the "What the demo is" section above describes the
shipped behavior; the designs below are the record):

1. **Diegetic screens — bricks render in-world.** *(LANDED — the shading
   half shipped as designed; the document-data seam followed 2026-07-04:
   scene `screenSlab` + top-level `screenSources`, pinned by the Post
   `world-screen` stage. CRT GLOW + FACE also landed 2026-07-04: each booted
   screen now renders a CRT glass face — barrel curvature, rounded dark bezel,
   native-line scanlines, vignette, fresnel glint, bloom knee — in
   `sampleScreenSurface`, AND emits colored light into the room, its per-frame
   framebuffer average summed with the sun in the world shade loop via the
   binding-11 `sdfScreenLights` buffer (`SetScreenLight` +
   `SdfFrame.AmbientScale/SunScale` dimming for mood). Additive; the shade
   funnel is now `float3` radiance. Deferred: CRT/ambient params as document
   data, and screen-light shadows.)* The room is already
   SDF-VM-rendered end to end (`OverworldFrameSource.BuildProgram` →
   `SdfEngineNode` → `SdfWorldEngine`), and every stand already carries a
   `ScreenSlab` shape with the reserved screen material
   (`SdfProgramBuilder.ScreenMaterialId`). What's missing is the shading
   half: today emulator framebuffers are composited as 2D panes by Stage 2;
   nothing samples them inside the world render. The settled design: extend
   **Stage 1's shading** (not the distance field — the SDF geometry never
   needs the texture) so a `ScreenSlab` hit maps its hit point through the
   slab's local frame to a UV and samples the brick's child storage image,
   which `SdfWorldEngine` already binds per frame for Stage 2
   (`SetChildSource`/`BindSources` — the plumbing exists, it just isn't
   visible to the views kernel). Unbooted slabs keep the dark screen
   material. The 2D pane easing (`ScreenDirector`) SURVIVES this as the
   zoom/focus view — diegetic screens are how the room looks; panes are how
   you play seriously. Verification: a `Puck.Post` Tier-B/C stage that
   renders a world with a synthetic child image on a screen slab and asserts
   sampled pixels (the `WorldChildStage` pattern, one step further in).
2. **The cartridge shelf — a game library as world data.** *(LANDED —
   `library` + shelf shipped;
   [examples/overworld-shelf.json](examples/overworld-shelf.json).)* `OverworldNode`
   grows a `library` (cartridge entries: id, title, `romPath`, and later the
   `peripheral` field) and a shelf along a wall (`OverworldRoom` slots, same
   keep-out treatment as stands). The sim loop extends the existing interact
   seam (`OverworldWorld.TryBootNearest` generalizes to nearest-interactable:
   shelf slot vs. stand): pick up (near shelf, hands empty) → carry (the
   cartridge rides the player's dynamic-transform slot, visible in-world) →
   insert (near stand, hands full; stand's cartridge becomes sim state) →
   boot as today. Carried-item and per-stand inserted-cartridge ids fold
   into `StateHash` so the whole loop replays bit-for-bit. Machines can no
   longer all assemble eagerly at startup — assembly moves to insert time
   (ROM load stays fail-fast at DOCUMENT load: the library validates every
   `romPath` up front).
3. **Animated controls — buttons and sticks that move.** *(LANDED — each
   stand's control cluster rides per-stand dynamic-transform slots, driven
   by the joypad state the machine consumes.)* Each brick's
   buttons/d-pad/stick become small SDF shapes on `TransformDynamic` slots
   (32 bytes each, already per-frame data) driven by the same joypad state
   the machine consumes — the mirror made visible. Pure content + a handful
   of dynamic slots; no engine work.

### Performance reality check

The prototype content exposed the world renderer's scaling wall: 206 ms/frame
at 1280×800 for ~45 SDF instructions + ~24 dynamic slots, 96% in the
per-pixel views kernel. Investigation found the diagnosis: the original
avatars VM had per-object **instancing** (instance table, per-tile bitmasks,
march chord clamp per instance) — a ray evaluates only objects overlapping
its tile, not the full VM. The port kept the outer shell (tile prepass) but
dropped the load-bearing mechanism. Both fixes LANDED 2026-07-03: (1) exact
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
target is PENDING (details: machine-fleet-plan lever 0). On the Surface the
remaining playability lever is internal render scale, not more culling. This
instancing substrate is what the fleet arc scales on.

## Next steps (carried arcs, unchanged direction)

0. **The zoom-out arc.** The overworld room is ONE STAGE of a larger reveal:
   sessions may open INSIDE a ROM (fullscreen, an ordinary-looking GB game)
   with the room disclosed later — the avatar was playing a humble device
   all along. ROMs are also world items ("insert" = a host-side mechanic,
   e.g. a watched mount point), in-ROM pickups can promote the real device
   (needs the machine→engine event seam), and link-cable trading is a
   designed mechanic. Workload + seam consequences live in
   [machine-fleet-briefing.md](machine-fleet-briefing.md) §1; none of the
   ROMs are designed yet, deliberately.
1. **PC camera as a GB Camera.** The camera feed reaches the emulated
   machines through a **cartridge-sensor seam** — a future `peripheral` field
   on an `OverworldNode.Consoles` entry — NOT another viewport pane (the four
   view slots are fully spoken for: room + three panes). Engine side: the
   existing camera-capture service already delivers CPU ARGB frames; brick
   side: the Pocket Camera mapper is in the Humble cart backlog
   ([ideal-gaming-brick-plan.md](ideal-gaming-brick-plan.md) — cart
   completeness). **The custom ROM that reads the sensor is deliberately not
   designed yet.**
2. **Device promote/demote (the "system power-up").** *(FIRST FORM LANDED
  2026-07-04 — the Bricks debug page's DMG/CGB/AGB mode verbs now snapshot
  the running Humble machine, rebuild it under the requested `ConsoleModel`,
  restore the snapshot, and keep the timeline cursor/cycle remainder in
  place. This is live state migration, not reboot-as.)* The long-term arc
  remains larger: a player's device upgrades or downgrades in REALTIME —
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
3. **Multi-controller console mode.** *(DONE 2026-07-04 — see the status
   block: pads beyond the first join as world players with their own
   binding bars; the proximity takeover went further than this step asked,
   giving any player exclusive control of a booted brick.)* Console mode
   was single-player when this was written; letting extra pads join the
   room while bricks stay routed was the pad-service extension.
4. **Power-off / re-boot.** Booting is one-way this revision; unbooting and
   re-transitioning the layout downward is the inverse walk of the same
   staged keyframes. (A re-boot would also need a timeline-cursor reset
   policy: rejoin at the head, or replay from the epoch again — the
   takeover RELEASE (2026-07-04) settled the head-rejoin precedent for a
   machine returning to the shared timeline, but power-off itself is still
   open.)
5. The pre-overworld deferred list (record/replay CLI, network intent source)
   carries over unchanged from the foundation game's design. Jumbotron
   diegetic screens graduated into prototype-arc item 1 above.

Provenance: the four-quad showcase (commit f846073) was the static precursor;
its quad layout is now the overworld's end state. The `mini-action` graph kind,
viewport source, and box child node were deleted with this revision.
