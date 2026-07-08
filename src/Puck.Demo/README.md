# Puck.Demo — the overworld and the composition root

`Puck.Demo` wears two hats. It is Puck's **only composition root** — the one
project that parses a command line, synthesizes or loads a `puck.run.v1`
document, resolves the node graph against real GPU services, and wires platform
windowing + both backend presenters — and it is **the overworld**, the game
prototype that composition root exists to run. With no flags at all it opens the
overworld: a controller-driven player in a walled room of bootable console
cabinets, each cabinet a costume of the one GamingBrick SM83 machine.

This README is the handoff doc for future agents: what the project is, what it
can do today, and where it is going. The deep, current design record lives in
[docs/overworld-demo-plan.md](../../docs/overworld-demo-plan.md) (the plan of
record) — this file is the front door; that file is the detail. That plan opens
with **the unification contract**, the north star this README is written
against: one experience reached entirely from inside a running session, with
flags/env demoted to CI/proof and developer twins. Read it before deep work.

> ### The Demo is GREENFIELD — read this first
>
> `Puck.Demo` is a **playground**, not settled precedent. It is expected to
> churn and be rewritten. Three rules follow from that, and they are not
> negotiable (CLAUDE.md rule 3, [agent-guide anti-calcification
> doctrine](../../docs/agent-guide.md#anti-calcification-doctrine) rule 5):
>
> 1. **Verify demo changes by RUNNING the demo**, never by a gate:
>    `dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2`
>    (the headless smoke; `0` or less runs until the window is closed).
> 2. **Never promote a demo feature into Post** — a stage, a hash, a
>    `--validate-*` flag, a `*DeterminismNode` hook — unless the user explicitly
>    asks. That calcification is exactly what is not wanted here.
> 3. **`Puck.Post` gates the shared *engine* contract**, not the game. The demo
>    keeps exactly one self-gate, `--validate-overworld`, only because Post is
>    forbidden from referencing the composition root.
>
> New capability belongs folded *into* the one overworld, not bolted on as a
> separate `--flag` strip-down mode.

## Running it

```
# The overworld (the default; Vulkan host; auto-exits after 30 s):
dotnet run --project src/Puck.Demo -c Release

# The headless smoke (auto-exit after 2 s; 0 or less runs until closed):
dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 2

# Boot straight INTO a cartridge (the immersed start; each pad seats a player):
dotnet run --project src/Puck.Demo -c Release -- --rom path/to/game.gbc
```

If the synthesized default cannot find its showcase ROM on the machine it
degrades to the **bare room** with a stderr note (an explicit `--run` document
stays strict). The bare room is the plain multi-controller mode — 4-player
pools, controller join/leave, roster-churn replay.

## The one experience — how to reach capabilities in-game

Per the unification contract, everything below is reached from *inside* a
single running session — no process restart, no separate mode. The table in
the next section is the headless/CI/developer appendix; start here.

| Capability | In-game path |
|---|---|
| Play a cartridge | Walk to a cabinet, press **North** to insert + boot its selected cart; the layout eases fullscreen → side-by-side → big-top/two-bottom → 2×2 quad as more cabinets boot (there are four cabinets total, `OverworldNode.MaxConsoles`). |
| Try a different game on a cabinet | The **Cycle** action rotates that cabinet's cart type in place — there is no shelf and no carrying a cartridge between machines. |
| Join as another player | Connect a pad; it seats a new player in the room (up to 4), each with their own binding bar. |
| Take over a running machine | Interact at a booted cabinet claims exclusive control; **Left bumper** disengages back to roaming. |
| The immersed start + fourth-wall reveal | Boot straight inside a cartridge (rung 1 of the reveal ladder); on the win/exit condition the fourth wall breaks and eases you OUT into the room the run document defines, everyone standing at their machines, games still running (rung 2). |
| The editor reveal (rung 3) — the workbench | Complete X arcade games (the 128-bit `meta` victory across cabinets), or `reveal editor`: the room's **workbench** — a dark terminal prop against the east wall — POWERS ON, its screen glowing with a CRT teal eased in over a transition ("the workshop opens"). The unlock is the in-session `EditorRevealed` state (`state` reports `editor=locked\|revealed`). |
| Author a creation | Press **Start** to enter creator mode (a diegetic controller act, not just a console command) — sculpt SDF primitives, then commit to forge + hot-swap the running avatar cart with no restart (create → commit → hot-swap). The in-game forge is **lossless**: both the commit hot-swap and the `forge` verb route the live scene's FULL `puck.creation.v1` document (animation frames + bake style included) through the same rich bake `--forge-avatar-from` uses, so forging a creation in-game and forging the same saved creation headlessly produce a **byte-identical** cart. The forge is now **subject-neutral**: `forge [avatar\|scene]` bakes the same creation into either the walking-avatar overworld (default) or an SDF-art creature **scene** cart, hot-swapped into the nearest cabinet — one registry (`Forge/ForgeSubject`), not a per-subject copy. Console verbs (`creator.*`) drive the same session for scripting/proof. Always-on (never gated on the editor reveal). |
| Sculpt the world | Once the editor reveal has lit the **workbench**, walk up to it and press **North** to enter world-sculpt — the diegetic door into "you can shape this world" (a controller act at last). The `world.*` console verbs (`world.place`, `world.wire`, `world.save`, `world.load`, …) place, wire, and persist creations into the live `puck.world.v1` world, and remain the always-on dev/agent entry regardless of the reveal; only the workbench entry is gated. |
| Compose music | `tracker.*` console verbs build/play a `puck.audio.v1` tune live (`tracker.new`, `tracker.note`, `tracker.play`, …), and **`tracker.forge`** compiles the working tune into a JUKEBOX cart (GPU-free) and hot-swaps it into the nearest cabinet in-session — the tune half of the subject-neutral forge. A diegetic (pad) entry point is a TODO (unification). |
| Populate the world with companions | `companion.*` console verbs (`companion.add`, `companion.face`, …) load creations as roaming companions with wired feeds/faces; a diegetic entry point is a TODO (unification). |
| Drive it all headlessly / from a script | See "Driving the demo over stdin" below. |

## Driving the demo over stdin

The on-screen console and process **stdin** drive the *same* command registry
(`Puck.Commands.CommandRegistry` via `TextCommandSource`/`CommandShell`) — a
verb typed in the backtick console and a verb piped in over stdin run the exact
same handler and produce the exact same `CommandResult`. This is the
agent-facing control + testing surface: pipe verb lines in, read the echoed
`line → result` pairs on stdout, and script the whole engine over a pipe. This
is how demo changes are verified per the unification contract (Puck.Post owns
determinism; scripted-console running is how the demo is exercised).

**Today it already works** (verified by running): the launcher's
`StandardInputReaderService` (registered through `AddLauncherTerminal`, which the
demo's host composition pulls in) reads OS stdin line-by-line into
`TextCommandSource`, and results echo to stdout — piping `console`/`creator` into
a live demo executes them and prints `[console open]` / `[creator …]`. So a
script drives the running session over a pipe today:

```
printf 'console\ncreator\n' | dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 6
```

What this arc ADDS is verb COVERAGE and observability, not the transport: the
driving/observability verbs — `reveal`, `boot`, `cart`, `capture`, `step`/`settle`,
`player.add`, `link`, `state`, `condition.*` — so a script can drive a full
session (boot, wait for a settled frame, capture, assert) without a GUI, plus
retiring the `PUCK_*` env surface (migration table in the plan of record) so
nothing is reachable only at launch.

**The recursion** — `condition.show`/`condition.set`/`condition.clear
<cabinet> …` re-forge a cabinet's exit + victory gate LIVE (including the meta
gate that unlocks the editor), routed through the same control-plane seam:
`condition.set 0 victory meta target=<guid> share=<guid> [group=<g>]`,
`condition.set 1 exit 0xC004>=1`, `condition.clear 0 victory`. A bad spec or a
non-existent cabinet echoes a usage line, never throws; a meta edit re-seeds the
running game's WRAM share slot and rebuilds the room XOR watch. Persistence is a
seam only (unwired — no conditions in `world.save` yet; the schema is unchanged).

Runnable, self-documenting example scripts live in
[docs/examples/scripts/](../../docs/examples/scripts/) — the smoke loop, the
reveal ladder (rungs 1→2 into the data-file town), and in-game authoring. A
script is a list of verbs, one per line; blank and `#` lines are comments. Pipe
one in and read the echoed `[verb: …]` results:

```
dotnet run --project src/Puck.Demo -c Release -- --exit-after-seconds 10 < docs/examples/scripts/smoke.console
```

`help` (a built-in registry verb) lists every available command.

## Headless CI/proof + developer entry points

These are not separate modes — the plan of record's migration table maps each
one to its in-game reflection. Everything still flows through **one**
data-driven path: a `--run` document, or the launch flags synthesized into the
same document model. There is no second imperative code path.

| Flag | What it does | In-game reflection |
|---|---|---|
| *(none)* / `--overworld` | The overworld — the default game. | *(this is the in-game path)* |
| `--rom <path>` | Boot straight into a `.gb`/`.gbc` cartridge — the immersed overworld, all four cabinets pre-inserted with it; each connecting pad seats a player at their own machine. A developer/CI convenience for the reveal ladder's rung-1 start. | The immersed start, above. |
| `--rom-exit "<0xADDR><op><val>"` | Fourth-wall condition over work RAM (`0xC000–0xDFFF`), e.g. `"0xDA22>=1"`; first hold reveals the room. | The fourth-wall reveal, above. |
| `--run <run.json>` | Load a `puck.run.v1` document; its graph + scene + viewports drive everything (`overworld` and `world` graph kinds; hosts on Vulkan). | The data file *is* the durable config path per the unification contract; this is its developer/CI entry point. |
| `--backend <vulkan\|directx>` | Graphics backend to start with (default `vulkan`; the launcher can switch live). | Engine-contract concern, out of scope for in-session reachability this arc (unification contract item 5). |
| `--present-mode <vsync\|mailbox\|immediate\|adaptive>` | Swapchain present mode, both backends (default `vsync`; `adaptive` = VRR). | Engine-contract concern; same as above. |
| `--surface-format <r8g8b8a8\|b8g8r8a8>` | Back-buffer format, both backends (default `r8g8b8a8`). | Engine-contract concern; same as above. |
| `--exit-after-seconds <n>` | Auto-exit after `n` seconds (default 30; `0` or less runs until closed). | CI/headless-smoke convenience only. |
| `--capture <png>` | Read the first rendered frame back from the GPU and write it (real render-target readback, not a desktop scrape). | Reflection is the planned `capture` console verb (being added this arc — see stdin section). |
| `--validate-overworld` | The demo's one self-gate: pure-CPU determinism + replay self-check; exits (0 pass, 1 divergence, 2 infra-fail). | No in-game path (this is the demo's one intentional self-gate, kept only because Post cannot reference the composition root). |
| `--emit-schema <path>` | Headless: write the run-document JSON Schema and exit. | No in-game path — a schema dump, not a session capability. |
| `--forge <path>` | Headless: forge a `.gbc` from SDF-authored art (a centred creature sprite over a forged room) (+ preview PNG). | In-game twin: `forge scene` bakes the creator's live creation through this SAME creature-cart path (type 10) and hot-swaps it into the nearest cabinet in-session (distinct from the walking-avatar cart the same creation also forges). |
| `--forge-camera <path>` | Headless: forge a real Pocket Camera `.gbc` (authentic M64282FP protocol) and self-verify it. | Cycle a cabinet to the camera cart type in-game (no in-game *forging* path — forging a fresh cart from scratch is a developer/CI action; TODO — unification: an in-game forge act). |
| `--forge-avatar <path>` `[--forge-avatar-from <creation.json>]` | Headless: forge a playable overworld `.gbc` a walking avatar sprite inhabits (built-in demo avatar, or a saved creation/avatar JSON — a creation document's timeline frames become the walk poses). Also writes the bake pipeline's `<out>.bake.bin` asset blob. | Exact proof twin of creator mode's commit step + the `forge` verb: both bake the SAME `puck.creation.v1` document through `AvatarForge.FromCreation`, so `--forge-avatar-from <saved>` is **byte-identical** to forging that same creation in-game (frames + bake style included). |
| `--forge-flagships <path>` | Headless: regenerate the three flagship avatars (lantern-fish, crt-robot, adventurer) from their recipes, assert byte-identical content determinism, then forge the adventurer through the avatar-forge path. | Proof twin; the flagships appear in-game as `companion.add`-able roaming companions. |
| `--forge-volley <path>` / `--forge-brickfall <path>` / `--forge-chroma <path>` / `--forge-solitaire <path>` / `--forge-poker <path>` | Headless: build one of the five five-star framework games (genuine SM83 machine code — title/attract/pause/battery high scores/sound), self-verify on a real machine, and write the `.gbc` (+ emulated preview PNG + asserted audio WAV; the card games also write a dealt-board `.play.png` proof). Each SDF-bakes its title art on the GPU first (hand-authored fallback without one). | Cycle a cabinet to that cart type in-game and play it; forging a fresh one from scratch has no in-game path yet (TODO — unification). |
| `--forge-tune <path>` `[--forge-tune-from <tune.audio.json>]` | Headless: build the minimal framework jukebox `.gbc` from an authored `puck.audio.v1` document — the whole music loop comes from `AudioDocumentCompiler`. Boots straight into the loop; Start toggles play/stop. | In-game twin: `tracker.*` composes the tune live and **`tracker.forge`** forges + hot-swaps the jukebox cart (type 9) into the nearest cabinet in-session — no restart. This is the proof twin. |
| `--forge-bake <dir>` / `--forge-bake-stress <dir>` | Headless: the SDF→brick bake pipeline's proof (8 preview PNGs, both styles × both targets) and its palette-pressure stress scene. | No in-game path — this is a rendering-pipeline proof, not a session capability. |
| `--forge-bake-calibration <dir>` | Headless: bake SDF stand-ins for Volley's hand art at native sizes, write a hand-vs-baked comparison PNG, and print a per-tile pixel-match report — a calibration report, never a gate. | No in-game path — a calibration report. |
| `--forge-town` | Headless (no GPU): build + verify **Puckton**, the flagship sculpted TOWN — regenerate every town creation byte-identically, assemble + walk-grid-bake the `puck.world.v1` world, prove determinism + round-trip, and MATERIALIZE it into the CAS store + `worlds/puckton.world.json`. Run it once, then walk the town. | Reflection is the reveal ladder: a won intro ROM eases the fourth wall into the town the run document names, once materialized (see below). |

Every forge tool writes a real cartridge you can boot with `--rom`, or cycle to
at a cabinet in the overworld.

### The town (Puckton) — the zoom-out

Run `--forge-town` once to materialize the town, then reach it the way the
overworld intends — from **inside a game**. Boot immersed into a run document
that NAMES the town, win, and the fourth wall breaks to reveal you standing in
it:

```
dotnet run --project src/Puck.Demo -c Release -- --run docs/examples/overworld-town.json
```

The world the overworld loads and reveals INTO comes from the DATA FILE: the
`OverworldNode.World` field (`"world": "puckton"`) names a saved world, resolved
+ committed at boot, so the room the reveal eases you out INTO is that sculpted
place. Its sibling `OverworldNode.Cell` (`"cell": 4000000`) places the whole room
at a far world cell (the planet-scale coordinate-stability demo). Omit either
field (every other document) for the bare room at the origin — zero effect on
every other run. The live mid-session equivalent of `world` is the
`world.load <handle>` console verb. Make it a LIVING town by loading the flagship
trio as roaming companions with the `companion.add` verb (one per name).

> **The demo's entire `PUCK_*` env surface is REMOVED** (unification contract
> item 2). There is no `PUCK_OVERWORLD_*`, `PUCK_COMPANION_*`,
> `PUCK_CREATOR_LOAD`, `PUCK_LINK_CABLE_PROBE`, `PUCK_WORLD_ROUNDTRIP`, or
> `PUCK_CONSOLE_OPEN` — setting one is inert. Reach every former capability
> through a **console verb** or a **run-document field**:
>
> - Deterministic headless screenshots: pipe a verb script over stdin —
>   `cart <i> <type>` / `boot <i>` / `reveal` / `step <n>` / `settle` /
>   `capture <png>` (and `state` to assert). `--capture` alone now grabs frame 0.
> - Creator scenes: the `creator` / `creator.load <name>` verbs (the `--scenario`
>   review harness is their headless twin — it opens creator and loads its
>   `Scenario:Creation`, and is the recipe for a 3D room shot rather than the
>   fullscreen brick pane).
> - Companions / feeds: `companion.add` / `world.wire` / `companion.face`; the
>   world round-trip proof is the `world.verify` verb.
> - Durable config: the overworld node's `world` / `cell` fields (and each
>   console's cart — set live with `cart`).
>
> The authoritative migration table is in
> [docs/overworld-demo-plan.md](../../docs/overworld-demo-plan.md). (`PUCK_TIMING`
> and other engine/launcher diagnostics are a separate, untouched concern.)

## What lives in this folder

| Path | What it is |
|---|---|
| `Program.cs` | The composition root: parse CLI → build/load run document → resolve host, presenters, windowing, allocator, backend switch, command modules, gamepad routing → run. |
| `DemoRunRegistrar.cs`, `GraphBuilder.cs`, `DemoRunDocuments.cs` | Document load/synthesis, graph pre-flight (deferred affordances become attributed exit-2 errors), and flag→document synthesis (the CLI flags flow straight into `DemoRunDocuments.Synthesize`; there is no separate flag-bundle type). |
| `SdfWorldRenderSpec.cs`, `SdfWorldRenderBuilder.cs` | The shared render assembly both graph kinds (`overworld`, `world`) build through — backend selection, world-render wiring. |
| `Overworld/` | **The game.** The deterministic world (`OverworldWorld`, `OverworldRoom`), intent sources (local/scripted/router/network), the lockstep brick timeline, the screen-layout director, the frame source, the determinism node + snapshot projection. |
| `Forge/` | **ROM forging.** SDF-art → `.gbc` (`RomForge`, `SceneForge`), the SM83 emitter, the Pocket Camera / avatar / world-lens cartridges. `Forge/Framework/` is the shared SM83 game framework (kernel, WRAM map, saves, PRNG, input, text, OAM, link, sound); `Forge/Volley`, `Forge/Brickfall`, `Forge/Chroma`, `Forge/Solitaire`, `Forge/Poker`, `Forge/Cards`, `Forge/Tune` are the five-star framework games plus the shared card layer and the audio-document-driven jukebox; `Forge/Bake/` is the SDF→brick bake pipeline. |
| `Town/` | **Puckton.** `TownWorld`, `TownBuildings`, `TownProps`, `TownForge` — the flagship sculpted town's content + the `--forge-town` build/verify/materialize path. |
| `Camera/` | `WebcamCameraSensor` — a PC webcam driving the emulated Pocket Camera sensor seam. |
| `Audio/` | `CabinetAudioOutput`, `WaveOut` — the demo's live audio output path for a booted cabinet. |
| `BindingBar/` | The on-screen, per-player action-bar overlay (glyph atlas in a storage buffer; scales with active players; swaps for creator mode). |
| `DevConsole/` | Puck's on-screen GPU developer console (GDI-rasterized monospace atlas, single storage buffer). |
| `Replay/` | `HashTrace` — the record/replay hash stream. |
| `DemoCommandModule.cs`, `DemoConsole.cs` | The core console verbs (`creator`, `forge`, `debug.view`, line editing) and the controller haptics proofs. Authoring verbs live across five command modules: `DemoCommandModule` (core/debug), `Creator/CreatorCommandModule.cs` (`creator.*` sculpt/animate/rig), `World/WorldCommandModule.cs` (`world.*` place/wire/save), `Tracker/TrackerCommandModule.cs` (`tracker.*` compose/play), and `Creator/CompanionCommandModule.cs` (`companion.*` roaming companions). |

## What the demo can do today

The overworld's settled capabilities (each described in full, with the seam that
owns it, in the [plan of record](../../docs/overworld-demo-plan.md)):

- **Bootable console cabinets** (0–4, document-driven; the room spacing adapts).
  Interact (**North**) at a cabinet inserts + boots its cart and lights its
  diegetic CRT screen; the layout eases fullscreen → side-by-side →
  big-top/two-bottom → 2×2 quad as more boot. Boots are deterministic
  simulation state folded into the world hash — they replay bit-for-bit.
- **The immersed boot (the fourth wall).** `--rom` opens *inside* the game; each
  connecting pad seats a player at their own machine; when an exit/victory
  condition holds, the panes ease away and the room is revealed with everyone
  standing at their cabinets, games still running.
- **Multiplayer + proximity takeover.** Extra pads join as world players (up to
  4), each with their own binding bar; interact at a booted cabinet claims
  exclusive control of that machine; **Left bumper** disengages back to roaming.
- **One machine, every costume.** The dmg/cgb/agb costumes of one GamingBrick;
  a **live device swap** (Bricks debug page) re-gates the color path on a
  running machine via snapshot/restore, and a per-ROM "boot shim" recipe pokes
  the game's cached hardware-detection flag so it re-renders in the new mode's
  own art — progress untouched.
- **The shared input timeline (lockstep).** Every powered brick consumes one
  recorded `(ticks, joypad)` stream from its own cursor; late boots
  fast-forward and converge, so same-costume machines end bit-identical however
  far apart they booted.
- **Fairness debuffs** (`speed`/`runAs`) and **battery saves** that persist to
  `<rom>.sav` and resume deterministically.
- **Creator mode + the on-screen dev console** (both host-side presentation; the
  sim hash never sees them). Press **Start** to enter creator mode with the pad
  (or type `creator` in the backtick console) to author SDF primitives; commit
  (Start again) bakes + hot-swaps the running avatar cart with no restart —
  create → commit → hot-swap, the genuinely diegetic path. World-sculpt
  (`world.*`) and a tracker/jukebox composer (`tracker.*`) exist alongside it as
  console-verb-driven authoring surfaces (see "What lives in this folder").
- **The forge subsystem** — SDF art, an authentic Pocket Camera, avatars, the
  flagship companions, and seven genuine hand-authored SM83 games (Volley,
  Brickfall, Chroma, Solitaire, Poker, plus the world-lens cart and the tune
  jukebox), each emitted as a real `.gbc` and self-verified on an emulated
  machine.
- **Data-driven `world` graphs** — the document's scene + viewports run live on
  the host backend (`--run docs/examples/world-*.json`); `--rom` synthesizes an
  immersed overworld document with all four cabinets pre-inserted.
- **Puckton, the sculpted town** — a flagship hand-authored `puck.world.v1`
  world (`--forge-town` materializes it), reachable in-game via the reveal
  ladder from a won intro ROM.

## Where it's going

The demo bends toward the unification contract's reveal ladder — **walk the
room, insert/cycle a cart at a cabinet, and play it on that device's own
screen**, with sessions that open immersed inside a ROM and disclose the room,
then the editor, later, no restart in between. (There is no shelf and no
carrying a cartridge between cabinets — cycling in place replaced that idea.)
The carried arcs (unchanged direction; full detail in the plan of record and
[machine-fleet-briefing.md](../../docs/machine-fleet-briefing.md)):

- **Finish the reveal ladder's third rung** — a diegetic entry into creator /
  world-sculpt / tracker / companion authoring from inside the room, not only
  console verbs.
- **Retire the `PUCK_*` env surface** into run-document fields and console
  verbs (migration table in the plan of record), and wire stdin into the
  console's command registry so a script can drive a full session headlessly.
- **PC camera as a real GB Camera** through the cartridge-sensor peripheral seam
  (the ROM that reads the sensor is deliberately not designed yet).
- **Device promote/demote as a game mechanic** — a player's device upgrades or
  downgrades in realtime, moving their running game between costumes
  (mushroom-style). The live-migration half is in place; the game-design rules
  and large fleets are the open half.
- **The zoom-out arc** — ROMs as world items, in-ROM pickups that promote the
  real device, link-cable trading, world-lens machines.
- **Power-off / re-boot** — the inverse walk of the staged layout keyframes.
- **The fleet performance arc** — the instancing substrate the world renderer
  now carries is what scaling to many live machines builds on.

## Orientation for deeper work

- [docs/overworld-demo-plan.md](../../docs/overworld-demo-plan.md) — the plan of
  record: the unification contract, every capability, the key-seam table, the
  presentation invariants, the controls, and the next steps in full.
- Skills to load before touching an area: `sdf-world` (the world renderer + SDF
  VM), `run-document` (the document + graph), `gaming-bricks` (the emulators),
  `rom-forge` (the ROM forge), `verifying-puck-changes`.
- [docs/project-map.md](../../docs/project-map.md) — where `Puck.Demo` sits in
  the layering and why it is the sole composition root.
- [src/Puck.Input/README.md](../Puck.Input/README.md) — the controller input
  layer every pad flows through.
