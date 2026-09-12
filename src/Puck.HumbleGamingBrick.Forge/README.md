# Puck.HumbleGamingBrick.Forge

`HgbCartridgeCompiler` compiles `puck.cartridge.v1` data into a native CGB ROM.
Players edit documents through Puck's in-engine console. The shared
[cartridge authoring guide](../Puck.GamingBricks.Forge/README.md) documents
commands, schema, limits and the host-independent compiler/editor APIs.
The compiler emits a frame loop, SM83 rules, 2bpp tiles, a background map,
RGB555 palettes and shadow-OAM sprites. Variables occupy bytes from
`0xC200`; the returned compilation names their addresses. Two-byte variables precede byte variables and
persist both bytes in saves. Byte arithmetic wraps modulo 256; wide assignment, addition and subtraction use
sixteen bits. It does not emit
the old games' save, state-machine or victory behavior. A world screen whose
`contentPath` names cartridge source is compiled through this compiler at
bind by `Puck.World.Machines` through `IMachineContentProvider`; the guide above
describes the read-back. The host receives neutral image and symbol metadata.

The package remains `ByteTerrace.Puck.HumbleGamingBrick.Forge`. It depends
on the shared cartridge document package, Assets and the HGB emulator;
it has no World dependency. There are no embedded sample games. The
existing Tune audio compiler and authored boot-ROM tools remain available.

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
- `BootRomBuilder` — the boot ROM a revision executes from reset
  (`Build(ConsoleModel)`): 256 bytes for a monochrome revision, 2304 for a
  Color one. `BootRomLayout` carries what differs per revision,
  `BootRomProgram`/`BootRomColorTiming` emit it, `BootRomProbeCartridge` builds
  the throwaway cartridges the timing is solved against, and `BootRomHandoff` /
  `BootRomHandoffCases` are the equivalence surface. The POST's
  `boot-rom-handoff` stage is that comparison's only runner; the forge tests pin
  each revision's image to a recorded hash instead.

## The authored boot ROMs

`BootRomBuilder.Build(model)` emits Puck's compatible firmware: a real boot
program with an original, centered `PUCK` pixel wordmark that settles over 32
frames, and a short rising-fifth pulse chime. Native Color startup uses the
same ink-and-ivory colors as the Advanced firmware. The companion-console revisions
retain their silent startup. Pixels and audio come from instructions running on
the machine; there is no frontend overlay or host audio clip.

The default `BootRomMark.Compatible` policy accepts Puck, ordinary commercial,
and independently authored cartridge logos. It still verifies the header
checksum and stops rather than executing a corrupt header. The companion
console's boot contract checks neither the logo nor checksum, and its images
preserve that behavior. Cartridge acceptance never changes the wordmark into
the cartridge's artwork.

The explicit `Era` and `House` policies retain strict comparison against
`CartridgeHeader.Logo` or `CartridgeHeader.HouseLogo`. These diagnostic images
display a compact Puck monogram, reserving room for their 48-byte comparison
table inside the original mapping limits. A matching logo is only an editable
header convention, not authentication: these policies do not make a secure
Puck-only distribution.

For a cartridge without the color flag, the program writes the
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
the counter and the scanline. Only the straight line is solved: a revision's own
offset from the shared Color tables (`BootRomLayout.HandoffCounterExtra`) is
added by the emitted table walk, so the solved tail cannot absorb it.

That solve reads only the tables and the emulated machine, so the same counter
would come out of a wrong table. What breaks the circle is the
`boot-rom-handoff` stage's reference cartridges: the mooneye `boot_div-*` ROMs
assert, per revision, the counter their own header hands off with, and the stage
checks both `BootDivPrediction` and the authored image against those numbers.
That covers one header on every revision; every other row of the tables is
unpinned by anything outside this repository, and the stage's pass detail says
so.

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
high-page register, high RAM through `0xFFFE`, the interrupt-enable register, and
Color palette RAM where the hardware runs natively (the data ports read sealed in
compatibility mode, so there is nothing to compare there). High RAM is in the
comparison because the image clears it: the boot program's stack residue and the
Color program's staged scratch are unwound before the handoff. Every register the
seeded state carries is a register the image writes — the wave-RAM pattern and
the deselected joypad included — so a capability question like
`SeedsWaveRamOnBoot` describes both paths rather than only one.

What does not agree, and cannot: the sub-register phase of the picture
processor's pixel pipeline and of the audio generators. The seeded handoff sets
those to captured constants no executing program reaches — the seeded
square-channel timer exceeds its own reload period, and the seeded dot phase is
odd where every instruction boundary lands on a multiple of four dots. Video RAM
and the framebuffer also differ, because the boot program drew something.

**Machine identity.** `MachineIdentity` fingerprints the selected boot ROM image.
Cold and fast startup with the same image share an identity: a full snapshot can
restore the live boot overlay as well as the cartridge state. A firmware-free
diagnostic seeded machine has a different identity, as does a machine selecting
another boot image. Those snapshots do not interchange.

## Verification and low-level tooling

The document compiler validates source and emits native bytes; arbitrary
player cartridges do not run a hidden game verifier during compilation.
The shared compiler tests execute authored input/rule/graphics behavior on
both emulators and compare ROM identity after canonical JSON round-trips.
Boot-ROM tests pin all 14 compatible images to recorded revision hashes and
check checksum refusal, independent-logo acceptance, native wordmark pixels,
animation movement, and audio waveforms before the cartridge begins running.

```powershell
dotnet test tests/Puck.HumbleGamingBrick.Forge.Tests -c Release
dotnet test tests/Puck.AdvancedGamingBrick.Forge.Tests -c Release
```

Native tooling that uses the legacy framework PRNG seeds it from input timing,
never a wall clock. Its save module checks magic/version/checksum and restores
ROM defaults on mismatch. These capabilities are not yet exposed by the
cartridge document schema. Tune keeps its separate audio-document path.

Always settle with `VerifyMachineSettle.SettleOutOfOamDma` after stepping
CGB frames before reading the bus. A fixed-size run can stop inside OAM DMA,
where memory reads are gated; `Framework.VerifyMachineDriver` handles this.
