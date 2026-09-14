# Cartridge backend contracts

Read this when a change touches memory addresses, register bits, the cost model, or either native
compiler's instruction-level behavior. `SKILL.md` covers the authoring surface (the `.puck` DSL, the
verify-in-a-world loop); this file covers what `HgbCartridgeCompiler`/`AgbCartridgeCompiler` do with a
lowered document. None of this is author-facing DSL syntax — it has no DSL spelling to give.

- A variable's declared `max` decides its representation: <= 255 is one byte, more is
  two, little-endian, and the window bounds the total BYTES rather than the slot
  count. A wide slot is admitted only as a set step's target or value and as a
  comparison operand — every other field is a byte on both machines and refuses one
  by name. Only assignment, `Add` and `Subtract` have sixteen-bit forms; SM83 gets
  add through `add hl, de` and subtract through a borrow chain, and the comparison
  walks a byte at a time with `>`/`<=` swapping their operands. Seeding writes both
  bytes.
- CGB variables: `0xC200..0xC27F`; `0xC280` retains the prior held byte before
  `InputModule.EmitTick`, whose own previous field is advanced inside its call;
  `0xC281` stages a value for a step that needs one address at a time, `0xC282` is the out-of-range discard sink, and
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
  the dearest is charged — the phase-machine shape. For an INFERRED guard the
  saving is given up entirely if anything writes that variable between the first
  such rule and the last, a counted loop's index included, so a phase must name
  its successor in a staging variable and a single ungated rule adopts it after
  every arm; put that advance step last, or the estimate silently triples.
- Declaring the variable as the document's `scene` removes that condition: the
  frame snapshots it before any rule evaluates and every guard on it compares
  against the snapshot, so a write names the NEXT frame's scene and can never
  open a second one in this frame. The advance step may then live inside its own
  arm, and the partition holds wherever it sits. One byte is reserved for the
  snapshot on each machine — 0xC283 on cgb (arrays move up to 0xC284) and
  0x020000C1 on agb. `RuleCount` is 1024: rules cost CODE, which capacity
  refuses, and `scene` is what keeps per-frame work flat as the count grows.
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
- AGB document output does not require BIOS calls/IRQs. World play uses the
  bundled Puck cold startup by default; explicit `fast` skips its presentation
  while retaining firmware services. The lower-level builder accepts an optional
  caller-supplied logo for retail BIOS boot; document output does not claim it.
- A rule's comparison and a `set` step's operation are named from `Puck.State`
  (`puck declarations ActionStateComparison`/`ExpressionOp` list the exact members), not from a forge
  list. An ABSENT operation assigns — the one combination no opcode spells, so the field
  is omitted rather than carrying a name. `CartridgeOperations` holds the
  emittable subset; KEEP IN SYNC with both backends' operation switches.
- Two admitted sets exist and they are not the same: `CartridgeOperations.Combines`
  is what a write may combine with, and `CartridgeExpressions.Reads` is what an
  expression may evaluate (the combining ten plus the six comparisons, minimum,
  maximum, clamp, select, sign and bitwise complement). The cost model prices both
  from one weight table but admits them separately, so a `minimum` inside an
  expression is modelled while a `minimum` as a write's operation is not.
- An expression evaluates in the operand width, every step. On SM83 that falls out
  of the accumulator; the Thumb evaluator masks each arithmetic result back to a
  byte on purpose, because thirty-two bit registers would otherwise disagree with
  the other machine at the first intermediate rather than at the store. Operands in
  flight live on the machine stack (`CartridgeExpressions.MaxDepth` bounds it), and
  a single-token read emits exactly the one load it names, so nothing composed pays
  for what is not composed.
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
