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
- `BootRomBuilder` — the boot ROM a revision executes from reset
  (`Build(ConsoleModel)`): 256 bytes for a monochrome revision, 2304 for a
  Color one. `BootRomLayout` carries what differs per revision,
  `BootRomProgram`/`BootRomColorTiming` emit it, `BootRomProbeCartridge` builds
  the throwaway cartridges the timing is solved against, and `BootRomHandoff` /
  `BootRomHandoffCases` are the shared verification the forge tests and the
  POST's `boot-rom-handoff` stage both run.

## The authored boot ROMs

Each image is a real boot program. It verifies the cartridge logo against
`CartridgeHeader.Logo` and the header checksum, wedging on either mismatch the
way the hardware does (the companion console's boot ROM checks neither, and the
image for those revisions does not either). It scrolls a Puck mark in, plays the
start-up chime, and — for a cartridge without the color flag — writes the
compatibility-mode selector and loads the compatibility palettes, then hands over
the revision's register file and unmaps itself at `0x00FE` so the program counter
falls into `0x0100`.

**The handoff is timed.** The divider counter a cartridge reads at `0x0100` is
the boot program's running time, and `BootDivPrediction` holds the counter the
hardware produces for each revision and header — a constant on the monochrome
revisions, the forwarded set-bit count on the companion console, and a table walk
on Color. The Color image carries that prediction's own tables, so there is one
copy of the data: it computes its target from the cartridge header, resets the
divider, and consumes exactly the predicted count before unmapping. Everything
before the reset is free; everything after it is straight-line, and the builder
solves that straight line by BOOTING the image it just emitted and reading back
the counter and the scanline. The solve is deterministic, so the images are
byte-identical across builds.

**The compatibility palettes.** For a cartridge the Color hardware runs in
compatibility mode, the image carries `CompatibilityPalette`'s own selection
tables and performs the hardware's lookup: the title checksum gated on the
first-party licensee picks a row, the fourth title letter breaks the ties among
the rows that share a checksum, and the chosen combination names one background
and two object palettes in the shared pool. Those go out eight bytes at a time
through BCPD and OCPD's auto-increment, so the index registers land at `0x88` and
`0x90` — which is what a cartridge reads back as `0xC8` and `0xD0`, and what the
seeded handoff carries. The data ports themselves read sealed in compatibility
mode, so palette RAM's contents are outside the compared surface there and the
seeded path needs no palette-RAM seed of its own. The selection tables live in
the low window between the entry jump and the unmap (`BootRomLowWindow`), which is
what keeps the rest of the program inside the upper window; the divider's
checksum contributions are carried as their one common value plus a row per
checksum that differs (`BootRomChecksumTable`) for the same reason.

**What agrees with the seeded post-boot state.** Everything a cartridge can read
at `0x0100`: the processor register file, the divider counter, every readable
high-page register, the interrupt-enable register, and Color palette RAM. What
does not, and cannot: the sub-register phase of the picture processor's pixel
pipeline and of the audio generators. The seeded handoff sets those to captured
constants no executing program reaches — the seeded square-channel timer exceeds
its own reload period, and the seeded dot phase is odd where every instruction
boundary lands on a multiple of four dots. On the revisions whose seeded handoff
parks on the first line, the status register's LY-comparison bit is the same
kind of gap: the seeded state holds that latch clear while LY and LYC are both
zero, which the running processor cannot do because it recomputes the latch every
dot. Video RAM, high RAM and the framebuffer also differ, because the boot
program drew something.

**Machine identity.** `MachineIdentity` fingerprints the boot ROM image, so a
machine booted through an authored image has a different identity than a seeded
one and their snapshots do not interchange. That is the intended behaviour; do
not alias them.

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
