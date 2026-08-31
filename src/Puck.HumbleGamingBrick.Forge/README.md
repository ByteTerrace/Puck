# Puck.HumbleGamingBrick.Forge

The SM83 ROM forge: hand-author CGB cartridges in C# and prove them by running
them. Everything a cart needs — machine-code emission, the game framework, art
and audio encoding, linking, verification — lives here, beside the machine it
targets. Packs as `ByteTerrace.Puck.HumbleGamingBrick.Forge`.

## The map

- `Sm83Emitter` — the machine-code emitter every routine is written through:
  labels, forward-reference fixups, byte-exact deterministic output.
- `Framework/` — the game framework: `FrameworkCartridge` (32 KiB
  MBC1+RAM+BATTERY, Color-required header, code window 0x0150..0x3FFF and data
  window 0x4000..0x7FFF, both banks visible without a bank switch),
  `FrameworkKernel` (interrupt-driven `halt` main loop, HRAM OAM-DMA
  trampoline, BG write queue), `FrameworkMemoryMap` (the WRAM source of truth
  — 0xC000..0xC0FF framework state, 0xC100 shadow OAM, 0xC200+ game-owned),
  `GameFramework` (the facade that wires every module; `BuildRom` assembles),
  the modules (`InputModule`, `BgModule`, `OamManager`, `PrngModule`,
  `SaveModule`, `TextModule`, `GameStateMachine`, `VictoryModule`), the
  declarative `GameManifest`/`AssetLinker` layer, and the sound stack
  (`ISoundDriver`, `ApuSoundDriver`, `SoundTables`).
- `HgbImage` — pure-C# RGBA8 → 2bpp/RGB555 encoders; every byte layout is the
  inverse of the emulator's PPU decode. No external image library.
- `AudioDocumentCompiler` — compiles a `puck.audio.v1` document
  (`Puck.Assets.Documents.AudioDocument`) into driver streams; shares
  `ApuNotePeriod`'s integer math exactly, so the document path and the
  hand-authored path cannot drift.
- `Tune/` — the jukebox cart: `TuneRom.Build(AudioDocument, string)` compiles
  an audio document into a bootable CGB cart (`Puck.World` steps it for world
  audio).
- `Games/` — worked-example carts; read `ArcadeQuest*` before authoring a new
  game.

## Doctrine

- **PRNG seed = input entropy, nothing else.** Seed = FrameCounter16 XOR
  0xA5C3, sampled at the title-screen START edge. Same press frame →
  bit-identical game.
- **Never trust SRAM.** Magic/version/checksum-guarded save block; any
  mismatch loads ROM defaults; the game only touches the WRAM mirror.
- **Verify by running.** A cart drives a real Humble machine and asserts
  observable WRAM/framebuffer behavior before its bytes are handed out
  (`VerifyMachineDriver`; always settle with
  `VerifyMachineSettle.SettleOutOfOamDma` after stepping frames — a fixed-size
  run can phase-lock its boundary inside OAM DMA, where reads are gated).

Determinism note: forge output is byte-identical across runs and machines; the
carts it builds hold no wall clock and no RNG hardware.
