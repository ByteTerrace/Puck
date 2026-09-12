---
name: rom-forge
description: Guides Puck's data-first cartridge authoring in Puck.GamingBricks.Forge, its CGB and AGB native document compilers, and the in-engine forge console. Also covers SM83/Thumb emitters, hardware kernels, Tune audio documents, boot-ROM tools and emulator-driven verification. Use for ROM source schemas, cartridge editing/build/play/export, native rule or graphics generation, and cartridge/boot/save seams.
---

# ROM documents and native compilers

The user's current instructions outrank this file. Update stale instructions
in the same change rather than preserving an obsolete workflow.

## Authoring in the DSL

`src/Puck.GamingBricks.Transpiler` compiles a `.puck` source to a cartridge document and decompiles one back.
Both artifacts are committed side by side (`tetris.cgb.puck` and `tetris.cgb.cartridge.json`); the regeneration
gate in `tests/Puck.GamingBricks.Transpiler.Tests` compiles the source and compares bytes, so they cannot drift.
`puck compile <source> --validate` runs the forge's own `CartridgeDocuments.Validate` over the lowered JSON.
Read that project's README for the grammar. Never hand-edit the generated JSON: regenerate it from the source.

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
byte state, addressable byte arrays, 8x8 sprites, runtime background writes,
cartridge audio, battery-backed state, and rule bodies that are step TREES
(`set`/`if`/`repeat`/`break`/`map`/`blit`/`play`/`stop`/`save`/`load`), not flat
action lists. It does not claim DMG or ROM banking. EVERY primitive runs on both
targets; nothing is target-gated, and adding a gate would be a design decision
rather than a shortcut.

Colour is per-palette, not global: `palettes.background` / `palettes.object` hold
up to 8 palettes on cgb and 16 on agb, `mapPalettes` picks one per background
cell, and a sprite picks an object palette. cgb carries the cell attributes in
video bank one (low three bits) and agb packs them into the screen entry's top
four bits; a runtime `map` write carries its own optional `palette`, and the cgb
queue drains twice — once a bank — to land the tile and the attribute.

A sound is EITHER a music track or a one-shot effect. A track is 1-4 voice
parts, one to a channel; validation refuses an effect on any voice a track
occupies. The four sequencers are identical and a loop start is the only thing
that separates a track from a one-shot: a voice carrying one rewinds at the
terminator, a voice without one stops and mutes. Stream step widths are
`duration` + FIVE register bytes for pulse one (NR10-NR14) and for wave
(NR30-NR34), FOUR for pulse two (NR21-NR24) and noise (NR41-NR44) — keep the
encoder and both drivers' register counts in sync. A music part's hold SUSTAINS
where a one-shot's mutes, which is why the two cannot share a row appender.

`AudioDocumentCompiler` and `ApuNotePeriod` live in the SHARED forge package, not
the humble one: the advanced machine's legacy sound channel takes the same four
registers, so one compiled track drives both. `AgbSoundDriver` remaps them to the
halfword pair at 0x04000068 / 0x0400006C — KEEP IN SYNC with the humble driver's
port order. `AgbSaveModule` writes the byte-wide window at 0x0E000000 and the
image must carry its identifier text, which is how a host recognizes the backup
kind; without it the window reads open bus and nothing persists. Add those by extending data, validation, both relevant backends and
native execution tests, not by a hidden special case.

## Backend contracts

- CGB variables: `0xC200..0xC27F`; `0xC280` retains the prior held byte before
  `InputModule.EmitTick`, whose own previous field is advanced inside its call;
  `0xC281` is the operand spill, `0xC282` the out-of-range discard sink, and
  arrays pack upward from `0xC283` to `0xDFFF`. AGB mirrors this with the sink at
  `0x020000C0` and arrays from `0x02000900`.
  The compiler uses `FrameworkKernel` and input primitives directly, with no
  save, victory or C# game-state callbacks in generated player cartridges.
- CGB shadow OAM: `0xC100`; the VBlank handler invokes the HRAM DMA trampoline
  and advances the frame counter. Fixed code/data windows are 0x0150..0x3FFF
  and 0x4000..0x7FFF. The header is Color-required MBC1+RAM+battery.
- AGB variables: `0x02000040..0x020000BF`; kernel state is the preceding 64
  bytes. Above them: `0x020000C4` queue count, `0x020000C8` the queue's 24
  entries of (row, column, tile, palette), `0x02000130` the four voices' state,
  `0x02000200` the save mirror. Mode-0 background map uses screenblock 31; tiles and object graphics
  use their native VRAM regions. Code starts at `0x080000C8`, data at
  `0x0800C000`, image size 64 KiB.
- Thumb literal pools are DATA. `EmitLiteralPool` does not branch around them;
  emit a branch yourself wherever the instruction stream can fall through.
- The AGB per-pixel surface is mode 4 drawn by BG2, so DISPCNT needs BG2's
  enable bit (0x400) or the surface never appears. Its memory ignores byte
  writes: plot by reading and rebuilding the containing halfword. A full-screen
  clear must be a DMA fill from a held source word (38400 bytes is far past what
  a per-frame loop can cover), and the tile and map copies must be skipped,
  because the surface occupies the same memory.
- A cartridge carrying a clock or another general-purpose device overlays its
  registers on ROM at 0x0C4-0x0C9, and an instruction fetched from there reads
  pin state rather than code. `EntryStubOffset` and `CodeOffset` are placed
  clear of that window for this reason; moving either back under it stops every
  clock-bearing image from booting at all, with no diagnostic.
- The AGB clock is bit-banged over those pins: bit 0 clock, bit 1 data, bit 2
  select; the direction register (0x0C6) says which the cartridge drives, and
  the control register's (0x0C8) low bit must be set or every read returns zero.
  Command bytes go out least significant bit first, taken on rising edges, and
  the reply comes back on falling edges in decimal-coded nibbles. Emit the bit
  work as a runtime loop — fifty-six unrolled exchanges put their constants out
  of reach of a program-counter-relative load.
- The estimate does not simply sum rules. Rules each guarded by one equality of
  the same variable against a different constant cannot share a frame, so only
  the dearest is charged — the phase-machine shape. The saving is given up
  entirely if anything writes that variable between the first such rule and the
  last, a counted loop's index included, so a phase must name its successor in a
  staging variable and a single ungated rule adopts it after every arm. Put that
  advance step last; moving it before any guarded rule silently triples the
  estimate.
- Cost is advice, not a gate. Validation refuses what makes an image wrong — a
  shape the hardware has no room for — never what merely makes it slow: a
  cartridge that misses frames still runs, and the emulator already absorbs that
  worst case. `CartridgeDocuments.Estimate` reports the per-frame work and the
  target's reservation; nothing refuses on it. Do not reintroduce a cost
  refusal.
- The one hard refusal is capacity. Every way an image can outgrow its machine —
  the Color image's bank windows, the advanced image's code and data windows,
  and the Thumb literal pool's reach — raises `CartridgeCapacityException` with a
  message naming what overran.
- Cost weights are per machine, on `CartridgeCostProfile`, not shared. A shared
  table has to take the worse of the two machines' ratios for every shape, which
  fits neither; each machine's weights and reservation are solved from its own
  capacity table. Units are not comparable between the two.
- Both machines run their processors at full speed: the Color one switches to
  double speed at boot (arm KEY1 bit 0 with interrupts off, then `stop`), and the
  advanced one sets WAITCNT to 0x4017 (prefetch on, first wait state 3/1, save
  window left at 8 cycles) before anything else, since the routine runs from the
  cartridge.
- AGB sampled voices carry a 16.16 position and step, so one recording covers a
  range of pitches; a voice record is 16 bytes (base, position, length, step)
  and a zero step is what marks it free. A document sequences sampled
  instruments from its own state — an array of rates plus a frame counter — so
  do not add a tracker primitive.
- One writer owns AGB's display control: the frame accumulates it in r4 from a
  base word (mode, objects, BG0) plus one bit per surface that is drawn, then
  stores it once. Never write 0x04000000 from a feature block: a second writer
  silently drops every other surface's bit.
- AGB background surfaces: BG0 is the document's map (screenblock 31), BG1 the
  window panel (30), BG2 the turning background (28) or the first declared
  layer (29), BG3 the second layer (27).
- The AGB window unit gates the blend unit as well as the layers: WININ/WINOUT
  bit 5 must be set in every region a `blend` should act in, or the blend
  silently does nothing exactly where the panel is. Blend targets are BLDCNT
  (0x04000050) first-target bits 0-5 (BG0,BG1,BG2,BG3,OBJ,BD), mode 01 in bits
  6-7, second target in bits 8-13; weights are BLDALPHA (0x04000052), EVA in
  bits 0-4 and EVB in bits 8-12. `blend` and `fade` share BLDCNT.
- Per-scanline scroll takes a different road on each machine: the Color machine
  arms a scanline-match interrupt whose handler waits for mode 0 before writing
  SCX/SCY, so the match is aimed one line ABOVE the band; the advanced machine
  arms DMA0 from an EWRAM table (0x02000400, 160 pairs of halfwords) into
  0x04000010 with control 0xA260. A burst runs in line n's horizontal blank and
  governs line n+1, so a band starting at line L is written from entry L-1.
  Rows republish in the vertical blank, never mid-picture — a burst must not
  read the table while it is being written.
- AGB document output uses direct boot without BIOS calls/IRQs. World play
  supplies `stub` explicitly. The lower-level builder accepts an optional
  caller-supplied logo for retail BIOS boot; document output does not claim it.
- Rules execute in source order; later steps read earlier writes. Arithmetic
  wraps modulo 256, comparisons are unsigned, and key edges are frame-local.
  A `repeat` count is a literal, never a variable, because the cost walk
  multiplies the body by it; `break` skips the increment, so the index is left at
  the iteration that broke while a completed loop leaves it at `count`.
  `mul`, `div`, `mod`, `shl` and `shr` come from emitted helpers, not silicon,
  and are total: a runtime zero divisor or a shift of eight or more yields zero.
  Array indices are bytes; an index past the declared length reads zero and
  discards the write. Both backends must agree on every one of these results,
  which is what `CartridgeMemoryTests` pins on the real emulators.
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

- **The mark decides which cartridges an image runs.** `BootRomBuilder.Build(model, mark)` emits the same program
  with one of two 48-byte bitmaps: the era one, or `CartridgeHeader.HouseLogo` — the ByteTerrace wordmark every
  cartridge `HgbCartridgeCompiler` forges carries. It is a SUBSTITUTION, not an addition: the Color image spends
  nearly all of its 0x700 bytes, with no room for a second table, so a house image refuses era cartridges and an era
  image refuses forged ones. The solve always runs on the era image, because it works by booting against probe
  cartridges that carry the era bitmap. The emitted program draws its own one-tile mark and never expands the
  cartridge's bitmap into video memory, so the header logo is verified but not displayed.
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

## Per-frame cost is measured, never estimated

`CartridgeCost` prices a document in abstract work units and `CartridgeValidation`
refuses one that exceeds the per-frame reservation. The units name no processor,
clock or instruction count; one unit is an eleventh of a `set` step writing a
literal to a variable. Weights come from `CartridgeCostMeasurement`, which boots
documents on both real machines and bisects the largest per-frame iteration count
each sustains at full frame rate; cost per iteration is inversely proportional to
that capacity. Never hand-count an emitter's instruction sequence into a weight,
and never adjust a weight to make a document fit.

Each weight is the WORSE of the two targets' ratios, and the reservation is the
Color machine's, which is the smaller. That is what lets a document carry one
portable unit count and still be safe on either backend, so flipping `/target`
cannot change whether it holds frame cadence. The two targets genuinely disagree
on relative cost — a multiply is expensive on SM83 and nearly free in Thumb — so
a per-target weight table would not be portable.

A blit SUSPENDS frame production rather than costing work: with the display off no
vertical blank arrives, so the frame counter does not advance. Measured, a
rectangle degrades cadence steeply past 120 tiles and stalls the document entirely
near 168, which is what `CartridgeLimits.BlitCellCount` pins. Never raise it
without re-measuring.

`map` writes queue and drain in the next frame's vertical blank on BOTH targets —
AGB stages and drains at its frame sync deliberately, so a cell changed this frame
appears the frame after on either machine. Validation bounds a frame's map writes
to the queue's 24 entries so a write can never be silently dropped; the push
subroutine's drop path must stay unreachable. `blit` is implemented and validated
on both targets but is deliberately UNMODELED: a small one measures near a fixed
cost while a full-screen one exceeds a whole frame on the Color machine and barely
registers on AGB, so it is both uncharacterized and a cadence-divergence hazard.
Do not give it a weight without a model that spans both observations.

A primitive with no measured weight prices as `CostBound.Unmodeled` and its
document is refused; nothing is admitted against an invented number. Adding a
primitive means measuring it. To re-measure, raise the reservation so the harness
can probe past it, run the harness with `PUCK_FORGE_MEASURE=1`, fold the reported
capacities in, and restore the reservation. `CostBound` and `CostModelProfile`
live in `Puck.Maths` and are shared with `Puck.State`'s rule cost; each subsystem
owns its own coefficients.

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
