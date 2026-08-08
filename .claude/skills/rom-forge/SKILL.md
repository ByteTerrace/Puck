---
name: rom-forge
description: Working on the ROM forge — src/Puck.Forge (the SM83 game framework in Framework/, the authoring document families and sculpt model in Authoring/, Sm83Emitter, HgbImage, AudioDocumentCompiler, the Tune cart, VerifyMachineSettle). Use whenever hand-authoring SM83 cartridge ROMs, touching the game framework (kernel, WRAM map, saves, PRNG, input, text, OAM, sound), or the cart/save/title-art seams.
---

# The ROM forge: hand-authored games onto real brick hardware

Factual and procedural only: settled contracts and how to verify. The user's
current instruction outranks this file — if it argues against a demanded
change, it is stale; update it in the same change.

## What exists, and what does not

**`src/Puck.Forge` is a normal solution project and it is live.**
`src/Puck.World` carries a `ProjectReference` to it, and
`src/Puck.World/Audio/TuneMachineSource.cs` calls `TuneRom.Build` to synthesize
a CGB cartridge that a Humble core steps cycle-exactly for world audio. That is
the forge's one live consumer, and it is a real one.

**Everything that turned art into cartridges is gone.** The following lived only
in `Puck.Demo`, which is quarantined under `experimental/` and **off limits** —
do not open it, do not port from it, do not cite it:

| Absent | What it did |
|---|---|
| `Bake/` (`BakePipeline`, `BakeStyle`, `TileAssembler`, `BakeCalibration`) | SDF render → CGB tiles/palettes → PBAK blob |
| `RomForge`, `ForgeCliSeams` | every `--forge-*` command and its option surface |
| `AvatarForge`, `AvatarSheet` | creation document → 12-view walker sprite sheet |
| `HgbCartridge` | the overworld cart (`BuildOverworld`) |
| `CameraRom`, `WorldLensRom` | the camera and world-lens carts |
| `LinkModule`, `MovementModule` | serial plumbing; walker movement modes |
| the `Cards/` substrate and the games built on it | gone |

**The forge CLI is not coming back**, and no plan schedules it. When asked to
"forge" something, say plainly that the CLI does not exist rather than looking
for it.

**Hand-authored games are NOT gone, though.** `src/Puck.Forge/Games/` holds
`ArcadeQuest*` (`ArcadeQuestGame`, `ArcadeQuestRom`, `ArcadeQuestProtocol`,
`ArcadeQuestVerify`, plus its own README), and its built 32 KiB cartridge is
committed at `src/Puck.World/Assets/roms/arcade-quest.gbc`. That is the live
worked example of the framework below — read it before authoring a new cart.

**One consequence worth stating outright:** `Framework/PbakBundle.Parse` and
`AssetLinker` are READERS of the PBAK wire form, and **nothing in the tree can
produce a PBAK blob any more** — the baker was the only producer.
`GameManifest.DefineSpriteArt` and the art-backed title-screen path therefore
have no reachable input. The code is correct and unreachable; treat baked art as
an absent capability.

## The map (`src/Puck.Forge`)

- `Framework/` — the SM83 game framework in `Puck.Forge.Framework`, dependent
  only on `Sm83Emitter` + the `HgbImage` encoders: `FrameworkCartridge`,
  `FrameworkKernel`, `FrameworkMemoryMap` (the WRAM source of truth),
  `GameFramework` (the facade — wires every module, `BuildRom` assembles),
  `RomDataBuilder`, `Hw`, `InputModule` (edges + attract-script override),
  `BgModule` (queue + LCD-off paints), `OamManager` (shadow OAM +
  metasprites), `PrngModule`, `SaveModule`, `TextModule` (39-glyph font),
  `GameStateMachine` (pending-state dispatch), `VictoryModule`.
  The LINKER LAYER: `PbakBundle` (the PBAK wire-form READER — raw bytes, no
  bake types; see the note above, it has no producer), `AssetLinker` (on the
  facade as `GameFramework.Assets`: allocates the 256-tile bank + 8/8 palette
  slots in declaration order, relocates PBAK sections — map cells/OAM tiles
  rebased, palette bits shifted — seals the composed `tile-bank`/
  `bg-palette-table`/`obj-palette-table` blocks the boot spec consumes),
  `GameManifest` (declarative: tiles/font/palettes/screens/tables/records/
  texts/scripts/sprite art → `Link(framework)` → `LinkedManifest` name lookups;
  `FontTileBase` is known at declare time, before the framework ctor). Sprite
  sets link as relocated `(dy,dx,tile,attr)` frame rows + a 4-byte-stride
  runtime frame table (addr lo, addr hi, entry count, 0). A game's identity —
  rules, layouts, decks, art — is manifest DATA, never copied code.
  And SOUND: `ISoundDriver` (boot/tick/effect-by-id/`EmitLibrary` hooks),
  `NoOpSoundDriver`, the real `ApuSoundDriver` (three sequencer voices —
  pulse-1 SFX, noise SFX, pulse-2 music loop; driver WRAM 0xC0A8..0xC0B2,
  `Scratch` starts 0xC0B4; `Bind(linked)` resolves its streams after `Link`),
  and the `SoundTables` catalog (deal/flip/shuffle/win + cursor/thud/sweep/over
  effects, `MusicLoop`/`MusicStop` ids) whose `DefineIn(manifest)` declares
  every stream as an ordinary manifest table — the REUSABLE surface games
  trigger via `Sound.EmitEffect`.
- `Authoring/` — the authored-content half, in `Puck.Forge.Authoring`. It is
  where the forge's INPUT is defined, and `Puck.World` consumes it directly
  (the world document embeds these documents inline; there is no file store —
  both folder-backed stores were dead and were deleted when this folder merged
  in from its own project). Three document families, each a record plus a static
  canonicalizer: `puck.creation.v1` (`CreationDocument`/`CreationCanonicalizer`
  — shapes, palette, hold-style timeline frames, IK chains, anchored camera
  eyes, faces, sounds, engraved text runs), `puck.audio.v1`
  (`AudioDocument`/`AudioCanonicalizer` — what `AudioDocumentCompiler` and the
  Tune cart consume), and `puck.synth.v1`
  (`SynthPatchDocument`/`SynthPatchCanonicalizer`, embedded inline in a
  creation's sounds). All three ride ONE document-neutral core,
  `DocumentCanonicalizer`: canonical bytes + SHA-256 from the same call, the
  strict-schema rule, the extensions-shadowing rule, the raise rule
  (`ThrowIfInvalid`), and the single shared `DocumentJsonOptions.Shared`
  (`IncludeFields = true` is LOAD-BEARING — `Vector3`/`Quaternion` expose
  fields, and omitting it silently zeroes every transform). Alongside them:
  `CreationGeometry` (the canonical primitive dimension table every stamp,
  workbench and bake emits through — changing a value changes the meaning of
  every persisted creation), `SculptModel` + `SculptChain`/`ChainSolver` (the
  frame-rate editor model and its analytic two-bone/spine IK),
  `EditHistory<T>` (the bounded undo/redo ring), and `GridSnap`. Host-side
  float on purpose: this is authoring/presentation math, outside the
  simulation-state determinism contract.
- `Tune/` — `TuneRom`/`TuneGame`/`TuneProtocol`/`TuneVerify`: the live cart.
  `TuneRom.Build(AudioDocument, string)` is what `Puck.World` calls.
- Top level: `Sm83Emitter` (the machine-code emitter everything shares),
  `HgbImage` (pure-C# RGBA8 → 2bpp/RGB555 encoders — every byte layout the
  INVERSE of the emulator's `Ppu.cs` decode; no external image library, on
  purpose), `AudioDocumentCompiler` (compiles a `Puck.Forge.Authoring`
  `AudioDocument` into driver streams; shares `ApuNotePeriod`'s integer math
  exactly, so the document path and the hand-authored path cannot drift),
  `VerifyMachineSettle`.

## The framework cartridge (`FrameworkCartridge`)

- 32 KiB **MBC1+RAM+BATTERY**: header 0x0143=0xC0 (Color REQUIRED),
  0x0147=0x03, 0x0148=0x00, 0x0149=0x02 (8 KiB SRAM at 0xA000). Both 16 KiB
  banks are visible without a single bank-switch write (MBC1's primary bank
  resets to 1): code window 0x0150..0x3FFF (16,048 B), data window
  0x4000..0x7FFF (16,384 B) — `Build` throws past either.
- Vectors: 0x0040 = `jp 0x0153` (the VBlank handler address is FIXED by the
  prologue convention); the other four vectors are bare `reti`. The routine
  MUST open with the 3-byte `jp boot` at 0x0150 — `Build` rejects it
  otherwise.
- A cart built here is booted with no boot ROM (A = 0x11 seeded at the
  post-boot handoff), so logo/checksums aren't needed to boot; they are written
  anyway so the `.gbc` is valid on real hardware.

## Kernel + WRAM convention (`FrameworkMemoryMap.cs` is the source of truth)

Interrupt-driven `halt` main loop. The VBlank handler: push → `call 0xFF80`
(the 10-byte OAM-DMA trampoline copied to HRAM at boot) → drain the 24-entry
BG write queue → FrameCounter16++ → `reti`.

- 0xC000–0xC0FF is framework state: frame counter 0xC000/01, input pipeline
  0xC003–06 (held/pressed/previous/raw), PRNG 0xC007/08, state machine
  0xC009/0A, BG queue 0xC00B+ (count + 24 × 3-byte entries; a push past
  capacity is dropped), attract script 0xC054–59, save mirror 0xC060 (≤72 B),
  scratch 0xC0A8..0xC0FF.
- 0xC100 = the 160-byte shadow OAM page the HRAM trampoline DMA-copies every
  VBlank. **0xC200+ (`GameRam`) is game-owned — the framework never touches
  that page or above.** SP grows down from 0xFFFE.

## Doctrine: PRNG, saves, title art

- **PRNG seed = input entropy, nothing else.** 16-bit LCG ×5+1 (the high byte
  is the output); seed = FrameCounter16 XOR 0xA5C3, sampled at the
  title-screen START edge (`EmitSeedFromFrameCounter` at the moment of
  commitment). No RNG hardware, no wall clock: the same press frame is a
  bit-identical game — verified across machines.
- **NEVER trust SRAM.** Save block: `magic('P','F') | version | payload |
  sum16-of-payload LE`; the game only ever reads/writes the WRAM mirror at
  0xC060. Any magic/version/checksum mismatch loads ROM defaults — a fresh
  cartridge, corruption, and a version bump all land on defaults (bump the
  version byte to orphan old saves on a layout change). SRAM enable
  (0x0A → 0x0000)/disable only inside the load/store subroutines; attract
  never writes SRAM.
- **Title art** installs only through a PARSED PBAK background linked as the
  art-backed title screen, and the menu-text contract is the MANIFEST'S overlay
  contract (`ScreenText` overlays swap cells into the font AND zero their
  attributes on art-backed screens — art can never make the menu unreadable);
  the linker owns all relocation (tiles after the font, palettes into slots
  1..7 — slot 0 stays gameplay's because the gameplay palette is declared
  first). **This path is currently unreachable**: the SDF emblem bakes that
  produced those blobs went with the bake pipeline. A new cart gets a
  hand-authored banner or nothing.

## Verify on a real machine

A cart self-verifies by driving a REAL Humble machine and asserting observable
WRAM/framebuffer behavior BEFORE writing bytes — that discipline is the forge's
whole verification story, and it survives: `TuneVerify` is its live instance.

**Every verify driver MUST call the shared
`VerifyMachineSettle.SettleOutOfOamDma` after stepping frames.** A fixed-size
`Run` can phase-lock its boundary inside the VBlank handler's OAM DMA, where the
emulated bus conflict returns the transfer's in-flight bytes — a battery that
skips the settle passes or fails by code-layout luck (once misdiagnosed as a ROM
wild jump; the ROM was correct, the READS were gated).

Verifier timing realities: press 8 frames / release 6 (edge-triggered, a long
hold acts once); LCD-off board repaints span multiple frames, so settle a few
frames after state flips before pressing.

The eight game batteries that set this bar are gone with their games, and the
design record that described what they asserted was deleted 2026-08-02. What
they proved — independent C# checksums over SRAM, cross-machine seed-replay
determinism, deal prediction by walking the PRNG backwards, byte-for-byte
evaluator oracles — is recorded nowhere now except Git history. Reconstruct the
technique from `TuneVerify`, not from memory of those batteries.

## House style

Named arguments, `m_` fields, parenthesized expressions, XML docs on publics.
The CA1502/CA1506 ceilings in `CodeMetricsConfig.txt` are real — extract
helpers and split registration iterators rather than raising a ceiling.
