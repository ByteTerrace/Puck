# Puck.Demo overworld

> **STATUS (2026-07-19): PORT-REFERENCE — the Demo no longer runs.** `Puck.Demo`
> was flipped to a **library** at Beat B of the
> [Demo → World port](demo-to-world-port-plan.md) (R0/OQ-11): its composition root
> is deleted and the default run is gone. **The "verify by running the demo"
> instruction below is void** — the port's verification target is `Puck.World`
> (`dotnet run --project src/Puck.World`). This document is retained only as
> **port-reference** for the unstarted arcs that carry its experience into World —
> the unification contract and reveal ladder (Arc 9), cabinets/console screen
> (Arc 5), creator/inhabitation (Arc 7). It is retired and its residue re-homed at
> **Arc 12**. Read the running contract as historical intent, not current behavior.

`Puck.Demo` is a greenfield game and composition root. Its default run is one
continuous arcade-room experience with bootable cabinets, live screen content,
authoring tools, and a console control plane. Verify demo behavior by running
the demo; use `Puck.Post` only for shared engine contracts.

## Experience contract

1. **One session.** Capabilities are reached through play, a controller chord,
   or a console verb without restarting the process. Launch flags are developer
   conveniences or headless proof twins, not separate products.
2. **Data for durable choices.** `puck.run.v1` selects the world, cabinets,
   starting cartridges, immersion, saves, and other durable configuration.
   The demo has no `PUCK_*` configuration surface.
3. **One console.** The on-screen panel and process stdin use the same command
   registry. Commands and results are echoed to stdout, so scripts can drive
   and observe the entire session.
4. **Reveal ladder.** The player starts immersed in an intro cartridge, exits
   into the world described by the run document, and later discovers the
   authoring workbench. Developer and agent authoring verbs remain available
   from the start.
5. **Headless twins.** Forge, schema, validation, scenario, benchmark, and
   document launch options exercise the same capabilities for CI or inspection.
6. **Lossless authoring.** Creator, tracker, bake, and forge operations carry
   the complete `puck.creation.v1` or tracker data into a cartridge and hot-swap
   it into a cabinet in the running session.

## Reveal ladder

### Immersed start

The initial cartridge fills the presentation. Connected controllers can take
ownership of machines while the room remains hidden. The machine continues to
run after the reveal.

### World reveal

A cartridge exit or victory condition pulls the camera out of the machine and
reveals the world named by `OverworldNode.World`. Active players appear at their
machines, and cabinet screens continue to display the games they were running.
The world may be the built-in room or an authored `puck.world.v1` scene such as
Puckton.

### Editor reveal

The workbench is a room prop whose screen powers on when the editor reveal is
unlocked. Interacting near it enters world sculpting. `world`, `creator`, and
`tracker` console commands remain available before the narrative reveal for
development and automation.

## Control plane

Pipe a script into the demo or pass `--script <path>`. Blank lines and lines
beginning with `#` are ignored. `help` prints the registered verbs.

Common command families:

| Purpose | Commands |
|---|---|
| Observe and drive | `state`, `step`, `settle`, `capture`, `reveal`, `boot`, `cart`, `join`, `player.add` |
| Cabinet input and links | `press`, `link`, `serial.watch` |
| World authoring | `world`, `world.load`, `world.save`, `world.verify`, `world.wire`, `world.snap` |
| Creation authoring | `creator`, `creator.load`, `creator.place`, `creator.snap`, `companion.add`, `companion.face` |
| Forge and tracker | `forge`, `tracker`, `tracker.forge` |
| SDF inspection | `sdf`, `sdf.shape`, `sdf.op`, `sdf.gallery`, `sdf.carve`, `sdf.bench`, `sdf.normals` |
| Simulation proofs | `garden.*`, `rts.*`, `planet.*`, `replay.*` |
| Addons and features | `addon.*`, `feature.*`, `gpu.timing` |
| Benchmark | `bench.*` |
| Time-travel (machine-neutral, index-first cabinets or the AGB debug scene) | `rewind`, `rewind.status`, `runahead`, `fastforward` |
| SM83 debug (index-first cabinets) | `hgb.*` — `peek`, `poke`, `regs`, `status`, `pause`, `resume`, `step`, `frame`, `until`, `snap`, `restore`, `watch`, `watch.clear`, `watch.list`, `dis`, `tilt` |
| ARM debug (AGB debug scene) | `agb.*` — `peek`, `poke`, `dis`, `light`, plus `regs`/`status`/`pause`/`resume`/`step`/`frame`/`until`/`trace`/`io`/`snap`/`restore`/`debug`/`bios` |

`capture` records the outer presentation, including overlays. Startup
`--capture` targets the renderer before the outer overlay decorator.

## Run-document configuration

An `overworld` graph contains up to four console sources, an optional cartridge
library, a world handle, spawn cell, immersion policy, and per-console source
settings. Important console fields include:

- `model`: the cabinet costume (`dmg`, `cgb`, or `agb`);
- `romPath`: a cartridge inserted for that console;
- `startCart`: the initial cartridge type;
- `saveSlot`: a suffix that separates battery saves for two cabinets using the
  same cartridge;
- `speed`: hardware pacing or DMG-rate fairness;
- `runAs`: the machine capability used at boot;
- `exit` and `victory`: work-RAM conditions reported to the host;
- `peripheral`: an optional cartridge peripheral such as the Pocket Camera
  sensor feed.

The run-document validator checks model shape and engine-wide limits. Demo-owned
cartridge indices are clamped by the consumer because `Puck.Scene` does not
depend on the demo.

## Cabinets and cartridges

The room contains four cabinets. Interact inserts or ejects the selected
cartridge. Cycle advances the selected cartridge type and live-swaps a running
cabinet. The current roster has 13 types:

| Type | Cartridge |
|---:|---|
| 0 | World Lens, fed from the room-player sensor page |
| 1 | Pocket Camera viewfinder, fed by the host camera sensor |
| 2 | Configured showcase ROM |
| 3 | Creator-forged avatar |
| 4 | Volley |
| 5 | Brickfall |
| 6 | Chroma |
| 7 | Klondike Solitaire |
| 8 | Five-card draw Poker |
| 9 | Tracker-forged jukebox tune |
| 10 | Creator-forged SDF-art scene |
| 11 | Oracle text game |
| 12 | Critter-Swap link-trading game |

Types 3, 9, and 10 are forged lazily from live session content. Framework games
and Critter-Swap use battery-backed saves. `saveSlot` allows linked cabinets to
hold distinct saves for the same cartridge.

The Pocket Camera cartridge is a genuine camera mapper. Its emulated sensor
reads the host's CPU-pixel camera capture; it is not another viewport pane.

## Players and machine ownership

Up to four connected controllers join local world seats. Interacting with an
unbooted cabinet boots and claims it. Interacting with an unowned running
cabinet takes it over. The Leave binding releases ownership and returns the
player to room movement without sending a conflicting joypad input.

Ownership is host routing rather than emulated or authoritative world state.
Each player has a binding bar and resolves debug actions relative to their own
nearest cabinet.

When no player owns a machine, room movement can mirror into its joypad. A
player-owned machine consumes only that player's routed input.

## Shared machine timeline

`OverworldBrickTimeline` records one tick-budget and joypad segment per engine
frame. Unowned machines consume the shared stream from independent cursors. A
late-booted machine catches up from the epoch with a bounded number of segments
per frame. Machines running the same cartridge code path and configuration
become bit-identical after convergence.

A DMG machine can diverge visually from CGB/AGB on a dual-mode cartridge because
the cartridge executes a different code path. CGB and AGB costumes share the
SM83 color path and can remain bit-locked.

### Fairness controls

`speed: "dmg"` pins the tick-to-cycle budget to the DMG rate even when the
machine enters CGB double-speed mode. `runAs: "dmg"` boots the DMG capability
path while retaining the cabinet's visual costume. Using the same `runAs` value
across costumes makes a dual-mode cartridge execute the same code path.

The Bricks debug page can switch the live console model through snapshot,
rebuild, and restore. Compatible cartridge recipes can update cached hardware
detection state; unsupported cartridges fall back to presentation-only model
reinterpretation.

## Link play

`link <a> <b>` connects two compatible cabinets through
`SerialLinkSession`. `press` queues frame-sized joypad segments and
`serial.watch` reports completed transfers. Pair stepping treats the linked
machines as one deterministic unit.

Critter-Swap demonstrates the complete in-demo flow: two battery-backed carts
negotiate roles, exchange checksummed blocks with bounded retry, swap their
critters, and commit each result to its own save. A lone cart reaches a visible
no-link state instead of waiting forever.

## Screen layout and camera

- Immersed and engaged machine views frame the native display head-on and
  pillar-box wide regions.
- The reveal eases from the triggering machine into an overview of the loaded
  world.
- After the reveal, the room is the primary region and player-owned machines
  occupy secondary regions. No owned machine leaves the room fullscreen.
- Animated rectangles never cause image reallocation; machine surfaces are
  allocated at their maximum extent and dispatched into the live region.
- The render origin is the room spawn anchor. It does not follow players.

`ScreenSlotLedger` arbitrates diegetic screen surfaces for cabinets, creator
preview, companions, console terminal, and other named feeds. New screen
content must use this ledger rather than drilling raw callbacks through the
render node.

## Input defaults

- Left stick moves in the room; South jumps; North interacts.
- Left bumper leaves an owned machine.
- Right bumper cycles the nearest cabinet's cartridge.
- The engine-debug chord selects SDF debug views.
- The Bricks-debug chord controls fairness, live model switching, save clear,
  state and fleet diagnostics, and capture.

The binding profile document is the source of truth. The on-screen and
diegetic bars render the active page rather than duplicating a hardcoded legend.

## Authoring and simulation surfaces

- Creator edits `puck.creation.v1`, animates shapes, previews the bake, and
  forges avatar or SDF-art cartridges.
- Tracker edits tune data and forges a jukebox cartridge without requiring the
  GPU bake path.
- World sculpting loads, edits, verifies, and saves `puck.world.v1` content.
- Companions instantiate saved creations and can claim screen slots.
- Grid snapping supports world lattice, object frame, rotation, and captured
  shape-reference guides.
- WASM addons occupy deterministic virtual-player slots and emit pad commands.
- Garden, RTS, gravity, and replay verbs expose fixed-point simulation and
  query proofs without creating separate launch modes.

## Key implementation seams

| Concern | Owner |
|---|---|
| Room simulation, boots, cart selection, hashing | `OverworldWorld` |
| Render assembly and capacity probe | `OverworldFrameSource` and `OverworldRenderNode` |
| Layout and reveal camera | `ScreenLayoutDirector` |
| Machine host, framebuffer, peripherals | `GamingBrickChildNode` |
| Shared input history | `OverworldBrickTimeline` |
| Device draining and routed pad state | `GamingBrickPadService` and `InputArbiter` |
| Screen claims | `ScreenSlotLedger` |
| Run-document model | `OverworldNode` and `GamingBrickSource` in `Puck.Scene` |
| Creator and world authoring | `CreatorScene`, `WorldScene`, and their command modules |
| Subject-neutral forge registry | `ForgeSubject` and `ForgeCommands` |

Every optional emission must be represented in the frame source's worst-case
capacity probe. A live rebuild that exceeds the frozen renderer envelope is an
error.

## Verification

Run the demo smoke:

```powershell
dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2
```

Run `--validate-overworld` for the demo's document and record/replay sanity
check. Use piped console scripts for behavioral checks:

```text
cart 0 4
boot 0
step 240
reveal
settle
capture artifacts/overworld.png
state
```

Forge commands self-verify their output before writing it. Shared engine or
emulator changes additionally require the battery selected by the
`verifying-puck-changes` skill.

## Open product work

- Make ROM discovery and insertion a world-item mechanic rather than only a
  configured or cycled cartridge choice.
- Define cartridge-to-host events for in-ROM pickups and device upgrades.
  Seeded by the GB Printer's `PrintEmitted` machine-to-host event (a completed,
  palette-applied print buffer with fingerprint); the demo hookup — routing a
  print into a world/inventory consequence — is still open.
- Turn live model promotion/demotion from a debug control into a coherent game
  mechanic, including cartridge compatibility rules.
- Complete true per-player diegetic action bars and input presentation in every
  split-screen layout.
- Re-host general live-camera run-document sources and cross-backend world
  production in the shared render assembly.
- Continue converging creator, tracker, world sculptor, audio, and game data
  onto one lossless studio document workflow. See
  [game-studio-plan.md](game-studio-plan.md).
