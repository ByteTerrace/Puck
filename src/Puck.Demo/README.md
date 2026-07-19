# Puck.Demo — a library being retired into Puck.World

> **STATUS (2026-07-19): LIBRARY, DOES NOT RUN — being retired.** `Puck.Demo` was
> flipped from a composition root to a **library** at Beat B of the
> [Demo → World port](../../docs/demo-to-world-port-plan.md) (commit `686d009`,
> R0/OQ-11). Its `Program.cs` composition root and the three god files are gone;
> there is no `dotnet run --project src/Puck.Demo`, no overworld default run, and
> no CLI/stdin surface. The project now **compiles only** so the surviving pinned
> files build until their consumers are deleted. **Verify all work by running
> `Puck.World`** (`dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2`).
>
> The port carries this project's capabilities into `Puck.World` and the shared
> libraries across twelve arcs (seven landed). The **plan of record is the single
> source of truth** for what is ported, what remains, and what is held:
> [docs/demo-to-world-port-plan.md](../../docs/demo-to-world-port-plan.md) — start
> at its **State of execution** block. The set of files still standing (pinned by
> the OQ-14 survivors and the held `Museum/MuseumRenderer.cs`) is the plan's
> **deletion ledger** and each arc's "Demo deletion — (c)-disposition" note; Arc 12
> deletes the project outright.

Everything below is **port-reference** for the unstarted arcs — a map of what still
lives in this folder and the forge/cartridge material Arc 8 (RomForge) ports. It
describes historical Demo structure, not a runnable product.

## What lives in this folder (held-files map)

| Path | What it is |
|---|---|
| `DemoRunRegistrar.cs`, `GraphBuilder.cs`, `DemoRunDocuments.cs` | Document load/synthesis and flag→document synthesis. The `--emit-schema` emitter was relocated to `tools/` (OQ-16, Beat A); `SchemaEmitter.cs` is the extracted island. |
| `Overworld/` | The deterministic world (`OverworldWorld`, `OverworldRoom`), intent sources, the lockstep brick timeline, the screen-layout director, the frame source, and the determinism/replay node + snapshot projection. **Pinned by OQ-14** (the replay + tick-introspection class), which gates the whole (c)-disposition deletion ledger. The shared render assembly lives in `src/Puck.SdfVm`. `ConsoleFeed.cs`/`ProceduralFeed.cs` are pinned by OQ-13's deferred console-feed port (Arc 5). |
| `Forge/` | **ROM forging — Arc 8's port source.** SDF-art → `.gbc` (`RomForge`, `SceneForge`), the SM83 emitter, the camera / avatar / world-lens cartridges. `Forge/Framework/` is the shared SM83 game framework (kernel, WRAM map, saves, PRNG, input, text, OAM, link, sound); `Forge/{Volley,Brickfall,Chroma,Solitaire,Poker,Cards,Tune}` are the framework games plus the shared card layer and the audio-document jukebox; `Forge/Bake/` is the SDF→brick bake pipeline. `Puck.Forge` (born on the audio arc) already lifts the tune chain + framework closure; the Demo keeps its copies as the byte-compare oracle until it dies. |
| `Town/` | **Puckton** — `TownWorld`, `TownBuildings`, `TownProps`, `TownForge`: the flagship sculpted town's content + the build/verify/materialize path. |
| `Camera/` | `WebcamCameraSensor` — a PC webcam driving the emulated camera cartridge sensor seam. |
| `Audio/` | `CabinetAudioOutput`, `WaveOut` — a booted cabinet's live audio output path. |
| `BindingBar/`, `DevConsole/` | The Demo's on-screen action-bar overlay and GPU dev console (superseded by `Puck.Overlays` in World; Arc 3 Beat B's R17 survey covers their disposition). |
| `Garden/`, `Rts/`, `Gravity/` | Deterministic proof scenarios. `Gravity/`'s planetoid walker is the successor subject Arc 2 ported (`WorldSolidField`); `FieldWalkerBody`/`AdvanceFieldWalker` sim state lives on `Overworld/OverworldWorld.cs`. |
| `Museum/` | `MuseumRenderer` — the replay museum + Droste door. **HELD** (OQ-2): preserved as a capability, ported by Arc 11 (Nested Worlds), never deleted before then. |

## Forge cartridges and carts (port-reference for Arc 8 · RomForge)

Arc 8 ports this cartridge surface into `Puck.World` as the `Cartridges` document
section, the `WorldCartridgeSource` union, `ISm83GameSource`, and the frame-spread
materializer. The Demo's forge subjects, described as data (the former `--forge-*`
entry points are gone):

- **SDF-art creature cart** — a centred creature sprite over a forged room (`RomForge`/`SceneForge`, cart type 10).
- **Camera cart** — an authentic M64282FP-protocol camera `.gbc`, self-verifying.
- **Avatar cart** — a playable overworld `.gbc` a walking avatar inhabits; a `puck.creation.v1` document's timeline frames become the walk poses (`AvatarForge.FromCreation`; also writes the `<out>.bake.bin` asset blob).
- **Flagship avatars** — lantern-fish, crt-robot, adventurer, regenerated from recipes with byte-identical content determinism.
- **Framework games** — Volley, Brickfall, Chroma, Klondike Solitaire, five-card Poker: genuine SM83 machine code (title/attract/pause/battery high scores/sound), each self-verifying on a real Humble GamingBrick machine before writing the `.gbc`, SDF-baking its title art on the GPU first.
- **Tune jukebox cart** — the minimal framework jukebox `.gbc` compiled from a `puck.audio.v1` document (`AudioDocumentCompiler`; cart type 9).
- **Puckton** — the flagship sculpted `puck.world.v1` town, built/verified/materialized (no GPU) into the CAS store + `worlds/puckton.world.json`.

Whether the seven deferred games port or are dropped is **OQ-4**, a scheduled owner
decision at Arc 8's exit (`Forge/{Volley,Chroma,Solitaire,Poker,CritterSwap,Oracle,Cards}` + `SceneForge.cs` + `Flagship*.cs` ≈ 16 970 lines).

## Orientation for deeper work

- [docs/demo-to-world-port-plan.md](../../docs/demo-to-world-port-plan.md) — the
  plan of record: State of execution, the twelve arcs, the deletion ledger, the
  open questions, and the carried tracks.
- Skills to load before touching an area: `rom-forge` (the ROM forge — Arc 8),
  `sdf-world` (the world renderer + SDF VM), `run-document` (the document + graph),
  `gaming-bricks` (the emulators), `verifying-puck-changes`.
- [docs/project-map.md](../../docs/project-map.md) — where `Puck.Demo` sits in the
  layering.
- [src/Puck.Input/README.md](../Puck.Input/README.md) — the controller input layer.
