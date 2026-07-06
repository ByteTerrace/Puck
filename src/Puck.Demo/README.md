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
record) — this file is the front door; that file is the detail.

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

### Flags

Every run flows through **one** data-driven path: a `--run` document, or the
launch flags synthesized into the same document model. The flags are thin
document-building aliases — there is no second imperative code path.

| Flag | What it does |
|---|---|
| *(none)* / `--overworld` | The overworld — the default game. |
| `--rom <path>` | Boot straight into a `.gb`/`.gbc` cartridge (the immersed overworld); each connecting pad seats a player at their own machine. |
| `--rom-exit "<0xADDR><op><val>"` | Fourth-wall condition over work RAM (`0xC000–0xDFFF`), e.g. `"0xDA22>=1"`; first hold reveals the room. |
| `--run <run.json>` | Load a `puck.run.v1` document; its graph + scene + viewports drive everything (`overworld` and `world` graph kinds; hosts on Vulkan). |
| `--backend <vulkan\|directx>` | Graphics backend to start with (default `vulkan`; the launcher can switch live). |
| `--present-mode <vsync\|mailbox\|immediate\|adaptive>` | Swapchain present mode, both backends (default `vsync`; `adaptive` = VRR). |
| `--surface-format <r8g8b8a8\|b8g8r8a8>` | Back-buffer format, both backends (default `r8g8b8a8`). |
| `--exit-after-seconds <n>` | Auto-exit after `n` seconds (default 30; `0` or less runs until closed). |
| `--capture <png>` | Read the first rendered frame back from the GPU and write it (real render-target readback, not a desktop scrape). |
| `--validate-overworld` | The demo's one self-gate: pure-CPU determinism + replay self-check; exits (0 pass, 1 divergence, 2 infra-fail). |
| `--emit-schema <path>` | Headless: write the run-document JSON Schema and exit. |
| `--forge <path>` | Headless: forge a `.gbc` from SDF-authored art (+ preview PNG). |
| `--forge-camera <path>` | Headless: forge a real Pocket Camera `.gbc` (authentic M64282FP protocol) and self-verify it. |
| `--forge-avatar <path>` `[--forge-avatar-from <creation.json>]` | Headless: forge a playable overworld `.gbc` a walking avatar sprite inhabits (built-in demo avatar, or a saved creation/avatar JSON — a creation document's timeline frames become the walk poses). Also writes the bake pipeline's `<out>.bake.bin` asset blob. |
| `--forge-volley <path>` / `--forge-brickfall <path>` / `--forge-chroma <path>` / `--forge-solitaire <path>` / `--forge-poker <path>` | Headless: build one of the five five-star framework games (genuine SM83 machine code — title/attract/pause/battery high scores/sound), self-verify on a real machine, and write the `.gbc` (+ emulated preview PNG + asserted audio WAV; the card games also write a dealt-board `.play.png` proof). Each SDF-bakes its title art on the GPU first (hand-authored fallback without one). |
| `--forge-bake <dir>` / `--forge-bake-stress <dir>` | Headless: the SDF→brick bake pipeline's proof (8 preview PNGs, both styles × both targets) and its palette-pressure stress scene. |
| `--forge-bake-calibration <dir>` | Headless: bake SDF stand-ins for Volley's hand art at native sizes, write a hand-vs-baked comparison PNG, and print a per-tile pixel-match report — a calibration report, never a gate. |

Every forge tool writes a real cartridge you can boot with `--rom`, or cycle to
at a cabinet in the overworld.

Three env hooks make headless screenshots deterministic:
`PUCK_OVERWORLD_CAPTURE_FRAME=N` delays `--capture` until N produced frames
(the machines have booted and drawn by then); `PUCK_OVERWORLD_CREATOR=1` opens
straight into creator mode; `PUCK_CREATOR_LOAD=<name-or-path>` additionally
loads a saved creation into the scene first.

## What lives in this folder

| Path | What it is |
|---|---|
| `Program.cs` | The composition root: parse CLI → build/load run document → resolve host, presenters, windowing, allocator, backend switch, command modules, gamepad routing → run. |
| `DemoRunRegistrar.cs`, `GraphBuilder.cs`, `DemoRunDocuments.cs` | Document load/synthesis, graph pre-flight (deferred affordances become attributed exit-2 errors), and flag→document synthesis (the CLI flags flow straight into `DemoRunDocuments.Synthesize`; there is no separate flag-bundle type). |
| `SdfWorldRenderSpec.cs`, `SdfWorldRenderBuilder.cs` | The shared render assembly both graph kinds (`overworld`, `world`) build through — backend selection, world-render wiring. |
| `Overworld/` | **The game.** The deterministic world (`OverworldWorld`, `OverworldRoom`), intent sources (local/scripted/router/network), the lockstep brick timeline, the screen-layout director, the frame source, the determinism node + snapshot projection. |
| `Forge/` | **ROM forging.** SDF-art → `.gbc` (`RomForge`, `SceneForge`), the SM83 emitter, the Pocket Camera / avatar / Volley / Brickfall / Chroma cartridges, the world-lens ROM. |
| `Camera/` | `WebcamCameraSensor` — a PC webcam driving the emulated Pocket Camera sensor seam. |
| `BindingBar/` | The on-screen, per-player action-bar overlay (glyph atlas in a storage buffer; scales with active players; swaps for creator mode). |
| `DevConsole/` | Puck's on-screen GPU developer console (GDI-rasterized monospace atlas, single storage buffer). |
| `Replay/` | `HashTrace` — the record/replay hash stream. |
| `DemoCommandModule.cs`, `DemoConsole.cs` | The console verbs (`creator`, `forge`, `debug.view`, line editing) and the controller haptics proofs. |

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
  sim hash never sees them). Type `creator` in the backtick console to author
  SDF primitives with the pad; `forge` bakes your creation into a walkable
  overworld cartridge.
- **The forge subsystem** — SDF art, an authentic Pocket Camera, avatars, and
  three genuine hand-authored SM83 games, each emitted as a real `.gbc` and
  self-verified on an emulated machine.
- **Data-driven `world` graphs** — the document's scene + viewports run live on
  the host backend (`--run docs/examples/world-*.json`); `--rom` synthesizes a
  fullscreen one-machine `world` document.

## Where it's going

The demo bends toward one loop — **walk the room, take a game off the shelf,
carry it to a cabinet, play it on the device's own screen** — and one larger
reveal: sessions that open inside a humble ROM and disclose the room later. The
carried arcs (unchanged direction; full detail in the plan of record and
[machine-fleet-briefing.md](../../docs/machine-fleet-briefing.md)):

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
  record: every capability, the key-seam table, the presentation invariants, the
  controls, and the next steps in full.
- Skills to load before touching an area: `sdf-world` (the world renderer + SDF
  VM), `run-document` (the document + graph), `gaming-bricks` (the emulators),
  `verifying-puck-changes`.
- [docs/project-map.md](../../docs/project-map.md) — where `Puck.Demo` sits in
  the layering and why it is the sole composition root.
- [src/Puck.Input/README.md](../Puck.Input/README.md) — the controller input
  layer every pad flows through.
