---
name: rom-forge
description: Maps Puck's data-first cartridge authoring in Puck.GamingBricks.Forge, its CGB and AGB native document compilers, and the in-engine forge console. Covers authoring, compiling, and decompiling `.puck` cartridge sources (`puck.cartridge.v1`) and their diagnostics, plus SM83/Thumb emitters, hardware kernels, Tune audio documents, boot-ROM tools and emulator-driven verification. Use for ROM source schemas, `.puck` cartridge authoring or compile/decompile errors, cartridge editing/build/play/export, native rule or graphics generation, and cartridge/boot/save seams. The `.puck` language core (grammar, CLI verbs) belongs to the puck-dsl skill; emulator core behavior belongs to gaming-bricks.
---

# ROM documents and native compilers

A map of cartridge authoring: where it lives, the contracts that bite, and how
to see a cartridge run. When this file and the code disagree, the code wins and
this file is corrected in the same change.

## Where things live

| Thing | Where |
|---|---|
| The `puck.cartridge.v1` model, validation, canonicalization, the `CartridgeDraft` editor, `ICartridgeCompiler`, the cost model | `src/Puck.GamingBricks.Forge` (`CartridgeDocuments`, `CartridgeValidation`, `CartridgeCost`, `CartridgeLimits`); its README has the source contract |
| The cartridge `.puck` vocabulary | `src/Puck.GamingBricks.Transpiler` |
| Native CGB compiler (SM83 code, 2bpp tiles, maps, palettes) | `HgbCartridgeCompiler` in `src/Puck.HumbleGamingBrick.Forge` |
| Native AGB compiler (Thumb code, mode-0 4bpp graphics) | `AgbCartridgeCompiler` in `src/Puck.AdvancedGamingBrick.Forge` |
| Shared audio compilation | `AudioDocumentCompiler`, `ApuNotePeriod` in the shared forge package; `TuneRom.Build` for world audio |
| Boot ROMs | `BootRomBuilder`, `BootDivPrediction`, `BootRomLayout` in `Puck.HumbleGamingBrick.Forge`; the native ARM7TDMI firmware in [`Puck.AdvancedGamingBrick/Firmware`](../../../src/Puck.AdvancedGamingBrick/Firmware/README.md) |
| The in-engine console | `src/Puck.World/ForgeCommandModule.cs` (`forge.new/open/show/set/remove/undo/check/build/save/export/play`) |
| Shipped sources | `src/Puck.World/Assets/cartridges/*.cgb.puck`, `*.agb.puck`, each with its committed `.cartridge.json` |

Both compilers consume the same rules, variables, sprites and input conditions.
Source data determines behavior: never add a game-name switch or copy gameplay
into a C# template. `Puck.GamingBricks.Forge` depends on Abstractions, Assets and
`Puck.State`, never on World.

## From source to a running cartridge

1. Edit the `.puck` source.
2. `puck compile <name>.cgb.puck --output <name>.cgb.cartridge.json --validate`.
   `--validate` runs `CartridgeDocuments.Validate` over the lowered JSON;
   lowering alone proves only that the source was well formed.
3. In a running `Puck.World`, `forge.open <file>.cartridge.json` for a local
   draft, or `forge.play <screen-index> <file>.cartridge.json` to hot-load into a
   screen. **Both verbs take only compiled JSON**: `forge.play` refuses a path
   not ending in `.cartridge.json`, and there is no in-engine compile of a
   `.puck` cartridge.
4. Look at it: `capture.start`, or a screenshot. Cartridges are verified in a
   world, never in a bespoke driver.

The committed `.cartridge.json` beside each source is derived: regenerate it by
compiling the source and commit both halves.
`CartridgeRoundTripTests.TestCommittedSourceCompilesToTheCommittedDocument`
byte-compares them. Never hand-edit the JSON.

`forge.play` saves canonical source and submits a `WorldScreenOp.Insert` with
the acting principal through `IServerLink`; never bypass the server's screen
authority. Failed draft edits leave the draft intact; compile, save and play
always validate.

## Contracts that bite

- **Cartridge rules admit what world rules refuse.** `repeat <n> as <name> { }`
  with `break`, call-form gates (`key(<button>, held|pressed|released)`) and
  every compound assignment are legal in a cartridge rule; in a world document
  they are **PUCK037**, **PUCK038** and **PUCK039**. `key(...)` is a gate only,
  not an operand. A `repeat` index name must be a declared variable.
- **Rule bodies are step trees** (`set`/`if`/`repeat`/`break`/`map`/`blit`/
  `play`/`stop`/`save`/`load`). An operand is a `Puck.State.ExpressionProgram`;
  `CartridgeExpressions.Reads` is the admitted operation subset and anything
  outside it is refused at its own spelling.
- **Targets differ; validation says how.** cgb and agb; byte and two-byte
  state; no DMG and no ROM banking. Consult the validator before calling a
  feature portable. The light sensor `$light` exists only on `cgb`.
- **Colour is per-palette:** up to 8 palettes on cgb and 16 on agb,
  `mapPalettes` per background cell, a sprite picks an object palette.
- **Map writes land a frame late on both targets:** they queue and drain in the
  next vertical blank. Validation bounds a frame's map writes to the queue's 24
  entries.
- **A sound is a music track or a one-shot effect.** A track is one to four
  voice parts, one to a channel, and validation refuses an effect on any voice
  a track occupies, since the effect would cut that part off. Put the effect on
  a free voice or move the music part.
- **Keep the two sound drivers in register sync.** Stream step widths are
  `duration` plus five register bytes for pulse one and wave, four for pulse two
  and noise; `AgbSoundDriver` maps the same registers to the AGB's legacy sound
  ports.
- **Saves.** `AgbSaveModule` needs its identifier text in the image or nothing
  persists; saves persist every byte.

## Cost is measured, never estimated

`CartridgeCost` prices a document in abstract work units against
`CartridgeCostProfile`'s per-target weights; `CartridgeDocuments.Estimate`
reports the frame bound and nothing refuses on it. Weights come from
`puck cartridge-cost`, which boots `CartridgeCostMeasurement`'s probe documents
on both machines. Never hand-count an emitter's instructions into a weight or
adjust a weight to make a document fit; a new primitive is measured by adding
its shape to `CartridgeCostMeasurement.Shapes`. An unmeasured primitive prices
as `CostBound.Unmodeled`, which poisons the whole estimate. Never compare unit
totals across targets. A blit repaints with the display off, so frames stop
advancing while it runs; `CartridgeLimits.BlitCellCount` bounds its size.

## Boot ROMs

`BootRomBuilder.Build(ConsoleModel)` emits each revision's boot program. The
handoff counter (`BootDivPrediction`'s tables) is the contract, pinned from
outside by the `boot-rom-handoff` Post stage against the mooneye `boot_div-*`
cartridges; never move a table value to make a boot agree with it. The builder
solves its straight-line tail against the emitted image; never hand-count
instruction timings into its constants. Runtime packages embed generated images
(`HgbFirmware.GetImage`) and never reference Forge. Regenerate with
`puck firmware` and verify the bytes.

## Seeing it work

- Compile a cartridge and play it in a world (above). For World integration,
  drive the real host over stdin (`puck-world`); a build alone is not a play
  test.
- The forge suites (`tests/Puck.HumbleGamingBrick.Forge.Tests`,
  `tests/Puck.AdvancedGamingBrick.Forge.Tests`, whose `CartridgeCompilerTests`
  runs cartridges on both real emulators) are there to run when they reach the
  change.
- **Settle out of OAM DMA before reading the bus.** A fixed stepping boundary
  can land inside OAM DMA, where WRAM reads 0xFF. Every SM83 verifier calls
  `VerifyMachineSettle.SettleOutOfOamDma` after stepping frames; use
  `Framework.VerifyMachineDriver`, which already does.
- **Wait on the observable fact, not a duration.** `RunFramesUntil` advances
  until a condition holds and fails at a frame cap. Reach a shared starting
  position once and hand it out with `VerifyMachineDriver.Snapshot`/`Restore`.

[references/backend-contracts.md](references/backend-contracts.md) holds memory
addresses, register bits and the native compilers' instruction-level behavior.

## Route adjacent work

| Skill | Route there for |
|---|---|
| `puck-dsl` | `.puck` grammar, the compile/lint/format verbs, PUCKnnn diagnostics. |
| `gaming-bricks` | Emulator core behavior and the Post batteries. |
| `puck-world` | Driving the World over stdin; screens and authority. |
