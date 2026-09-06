---
name: rom-forge
description: Guides Puck's data-first cartridge authoring in Puck.GamingBricks.Forge, its CGB and AGB native document compilers, and the in-engine forge console. Also covers SM83/Thumb emitters, hardware kernels, Tune audio documents, boot-ROM tools and emulator-driven verification. Use for ROM source schemas, cartridge editing/build/play/export, native rule or graphics generation, and cartridge/boot/save seams.
---

# ROM documents and native compilers

The user's current instructions outrank this file. Update stale instructions
in the same change rather than preserving an obsolete workflow.

## Player authoring

`src/Puck.GamingBricks.Forge` owns `puck.cartridge.v1`, validation,
canonicalization, the `CartridgeDraft` JSON Pointer editor, graphics encoding,
and `ICartridgeCompiler`. It depends only on Assets and is packable.
Read its README for the source contract and runnable console walkthrough.

`HgbCartridgeCompiler` in `Puck.HumbleGamingBrick.Forge` emits native CGB
SM83 code, 2bpp tiles, maps and palettes. `AgbCartridgeCompiler` in
`Puck.AdvancedGamingBrick.Forge` emits Thumb code and mode-0 4bpp graphics.
Both consume the same rules, variables, sprites and input conditions. Source
data determines behavior; never add a game-name switch or copy gameplay into
a C# template. No sample games or built ROM assets remain.

`src/Puck.World/ForgeCommandModule.cs` provides local console/seat drafts and
`forge.new/open/show/set/remove/undo/check/build/save/export/play` in every
boot shape. JSON tails retain quotes through `WorldCommandArguments`.
Draft edits can temporarily violate the schema; compile/save/play always
validate. Failed edits leave the draft intact. Source paths are explicit;
there are no environment-variable settings. `forge.play` exports then submits
`WorldScreenOp.Insert` with the acting principal through `IServerLink`.
Never bypass the server's screen authority or replay/content hashing.

The current schema has explicit limits and omissions: cgb/agb targets,
byte state, static maps, 8x8 sprites, ordered straight-line rules. It does not
claim DMG, cartridge sound/save authoring, dynamic tile maps, or large banked
games. Add those by extending data, validation, both relevant backends and
native execution tests, not by a hidden special case.

## Backend contracts

- CGB variables: `0xC200..0xC23F`; `0xC240` retains the prior held byte before
  `InputModule.EmitTick`, whose own previous field is advanced inside its call.
  The compiler uses `FrameworkKernel` and input primitives directly, with no
  save, victory or C# game-state callbacks in generated player cartridges.
- CGB shadow OAM: `0xC100`; the VBlank handler invokes the HRAM DMA trampoline
  and advances the frame counter. Fixed code/data windows are 0x0150..0x3FFF
  and 0x4000..0x7FFF. The header is Color-required MBC1+RAM+battery.
- AGB variables: `0x02000040..0x0200007F`; kernel state is the preceding 64
  bytes. Mode-0 background map uses screenblock 31; tiles and object graphics
  use their native VRAM regions. Code starts at `0x080000C8`, data at
  `0x0800C000`, image size 64 KiB.
- Thumb literal pools are DATA. `EmitLiteralPool` does not branch around them;
  emit a branch yourself wherever the instruction stream can fall through.
- AGB document output uses direct boot without BIOS calls/IRQs. World play
  supplies `stub` explicitly. The lower-level builder accepts an optional
  caller-supplied logo for retail BIOS boot; document output does not claim it.
- Rules execute in source order; later actions read earlier writes. Arithmetic
  wraps modulo 256, comparisons are unsigned, and key edges are frame-local.
  The source validator bounds work as well as array sizes; compiler capacity
  failures must be errors rather than truncated ROMs.

## Other maintained tools

The existing `Framework/` low-level APIs remain for Tune/native tooling.
`GameFramework`, `GameManifest`, `AssetLinker`, sound/save/PRNG helpers and
PBAK readers are not the player editor. PBAK has no live producer; do not
present that legacy route as in-engine art authoring.

`TuneRom.Build(AudioDocument, string)` remains the live world-audio compiler.
Audio/synth documents live in Assets; keep the forge packages independent of
World. `HgbImage` contains native image encoders; `AudioDocumentCompiler`
shares the integer note-period calculations with the audio subsystem.

## The authored boot ROMs (`BootRomBuilder`)

`BootRomBuilder.Build(ConsoleModel)` emits the boot ROM a revision executes from
reset: 256 bytes for a monochrome revision, 2304 for a Color one (mapped
0x0000-0x00FF and 0x0200-0x08FF, the cartridge header showing through the gap).
It is a real boot program — logo and header-checksum verification with a wedge on
mismatch (the companion-console revisions check neither), a mark scrolled in, the
start-up chime, the compatibility-mode selector and compatibility-palette load for
a cartridge without the color flag, the revision's register handoff, and the unmap
at 0x00FE so the program counter falls into 0x0100.

- **The handoff counter is the contract.** `BootDivPrediction`'s tables are the
  budget; the Color image carries those same tables and computes its target from
  the cartridge header, plus the revision's own offset from those shared tables
  (`BootRomLayout.HandoffCounterExtra`, which carries `Cgb0Extra`/`AgbExtra`).
  The program resets the divider and then consumes exactly the predicted count,
  so everything before the reset is free and everything after it is straight
  line.
- **The tables are pinned from outside; the solve is not evidence.** The same
  `Compute(model, header)` call seeds the post-boot state, sets the emitted
  image's target, and steers the builder's solve, so a wrong table produces a
  matching boot. The `boot-rom-handoff` stage breaks that by booting the named
  mooneye `boot_div-*` reference cartridges and checking both the prediction and
  the authored image against the counter each cartridge asserts. That covers one
  header on every revision; every other table row is unpinned outside this
  repository, and the stage's pass detail says so. Never move a table value to
  make a boot agree with it.
- **The straight line is SOLVED, not counted.** `BootRomBuilder` boots the image
  it just emitted against `BootRomProbeCartridge` headers and adjusts two
  machine-cycle constants until the handoff counter and scanline land. Never
  hand-count instruction timings into these constants, and never let the solved
  tail absorb a per-revision offset — that is `HandoffCounterExtra`'s job, added
  by the emitted table walk.
- **The compatibility palettes are the index registers' cause.** The image carries
  `CompatibilityPalette`'s selection tables and loads the chosen background and two
  object palettes eight bytes at a time through BCPD/OCPD auto-increment, which is
  the only thing that lands BCPS/OCPS on 0x88/0x90 (read back 0xC8/0xD0). The data
  ports read sealed in compatibility mode, so palette RAM contents are not compared
  there (the capture is gated on the compatibility authority) and the seeded path
  carries no palette-RAM seed. Space for those tables comes from the low window
  (`BootRomLowWindow`, between the entry jump and the unmap) and from carrying the
  divider's checksum contributions as one common value plus a row per checksum
  that differs (`BootRomChecksumTable`).
- **Equivalence surface.** `BootRomHandoff.Compare` has ONE runner, the POST's
  `boot-rom-handoff` stage; the forge tests pin each revision's image to a
  recorded hash instead. The compared surface is the processor register file, the
  divider counter, every readable high-page register, high RAM through 0xFFFE,
  the interrupt-enable register, and Color palette RAM where the compatibility
  authority (`DmgCompatibilityState.IsActive`) says the silicon runs natively —
  never on the model alone. `Capture` mutates nothing: it snapshots the machine
  around the palette walk and restores it. Only the picture-pipeline and
  audio-generator sub-register phase is outside the surface — it is not reachable
  from an executing program. The LY-comparison bit is IN it and is not masked:
  a revision whose seeded handoff parks at LY==LYC==0 carries the latch set, so
  its status register reads 0x84.
- `MachineIdentity` fingerprints the boot image, so a booted machine's snapshots
  never interchange with a seeded machine's.

## Verification

Run both existing forge test projects. `CartridgeCompilerTests` builds JSON
source, exercises editing/refusals, pins deterministic output after round-trip,
and runs inputs, comparisons, arithmetic and sprite/graphics observations on
both real emulators. Emitter probes and boot-image hash tests remain separate.
Compiler validation cannot prove an arbitrary player's game correct; no
hidden demo verifier runs during document compilation.

Every SM83 verifier MUST call
`VerifyMachineSettle.SettleOutOfOamDma` after stepping frames before bus reads.
A fixed cycle boundary can land inside DMA and otherwise falsely read gated
WRAM as 0xFF. `Framework.VerifyMachineDriver` already applies this rule.

For World integration, run the actual host through stdin using the puck-world
skill. Verify source editing, canonical save/open, ROM export and accepted
screen insertion for both targets; a build alone is not a play test.

Keep XML API docs, package READMEs, the shared source guide, World's command
entry, project map and this skill synchronized. Named arguments, m_ fields,
and the repository analyzers apply; do not raise complexity ceilings.
