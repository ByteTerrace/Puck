# PPU Rendering Models

The GBA PPU draws 240×160 over a 308-dot / 1232-cycle scanline (240 visible dots
+ 68 HBlank dots; 228 lines/frame). The central accuracy axis is *granularity*:
a per-scanline batch renderer re-reads registers once per line, while a per-dot
renderer honors register writes at the exact pixel. Puck's PPU is per-scanline
with per-line register re-evaluation — the mGBA-class model, which the research
agrees captures "the overwhelming majority" of raster effects because HBlank-DMA
operates at scanline granularity. The gaps are the OBJ cycle budget, affine
reference-register latching, and — at the far end — true per-dot rendering.

Provenance: `digest-2` (gatherer), `review-b` PART 1 (deep review), with credit
facts from `digest-0`.

---

### Scanline-batch vs per-dot (mid-scanline) rendering

- **Source:** NanoBoyAdvance PR #258 (*implement a cycle-accurate renderer*),
  https://github.com/nba-emu/NanoBoyAdvance/pull/258 , v1.7 release notes,
  https://github.com/nba-emu/NanoBoyAdvance/releases/tag/v1.7 ; SkyEmu accuracy
  doc, https://github.com/skylersaleh/SkyEmu/blob/main/docs/Accuracy.md ; mGBA
  video internals (DeepWiki),
  https://deepwiki.com/mgba-emu/mgba/4.1-gba-video-rendering ; GBATEK,
  https://problemkaputt.de/gbatek.htm.
- **Finding:** mGBA runs a classic per-scanline software renderer
  (`GBAVideoSoftwareRendererDrawScanline`, once per visible line, cached I/O
  snapshots per line). NanoBoyAdvance rewrote to a cycle-accurate per-dot renderer
  (PR #258 "removed all per-scanline rendering code," shipped v1.7). SkyEmu is
  genuinely per-pixel, capable of mid-scanline effects. Titles cited as needing
  sub-line fidelity: Iridion 3D and Star Wars Episode II (corrupt on scanline
  renderers), plus Prehistorik Man, NES Classics, Golden Sun, Hello Kitty Miracle
  Fashion Maker. The higan/ares design philosophy is the classic articulation:
  even the accuracy-first core renders one scanline at a time as a deliberate
  performance concession.
- **Determinism fit:** fully compatible — dot rendering is integer/cycle-driven,
  no float/RNG/wall-clock. It *strengthens* determinism.
- **Puck status: not implemented — deferred accuracy frontier.** The PPU is a
  per-scanline batch renderer (`RunEvent` alternates `EnterHBlank`→`RenderScanline`
  and `NextScanline`); true mid-scanline register-write effects are explicitly
  deferred (Post README §7.E). **Verdict (review-b P1 / survey #21): adopt later /
  test-first** (XL — a ground-up `AgbPpu.cs` rewrite to a cycle-stepped state
  machine driven from `AgbScheduler`). Do *not* start with the rewrite: first
  adopt the candidate proving ROMs as render-hash evidence and quantify how many
  games we care about actually break at scanline granularity. Reserve the XL
  rewrite for a co-sim-proven need.
- **Calibration (review-b §b4/b5):** the "only released emulator with
  mid-scanline" and "2nd vs 3rd to pass AGS" framings are SkyEmu/Hades
  self-reported and internally inconsistent (2nd per one source, 3rd per another);
  do not cite as fact. The mid-scanline game list originates largely from SkyEmu
  marketing + a community wiki — use them as *candidate* proving ROMs to verify
  each actually breaks on our renderer, not as established requirements. The
  gatherer could not locate a dedicated mGBA per-pixel-timing blog post nor a
  Hades PPU-technique post; no claim depends on them.
- **See also:** HBlank-DMA raster effects below,
  [emulator-landscape.md](emulator-landscape.md).

### OBJ per-line cycle budget + sprite dropout

- **Source:** GBATEK, https://problemkaputt.de/gbatek.htm ; mGBA video internals
  (DeepWiki), https://deepwiki.com/mgba-emu/mgba/4.1-gba-video-rendering.
- **Finding:** sprites have a per-line rendering-cycle budget — **1210 cycles**
  (`304*4-6`) with H-Blank-Interval-Free (DISPCNT bit 5) = 0, **954 cycles**
  (`240*4-6`) with bit 5 = 1. Cost model: normal OBJ = 1 cycle/pixel; affine
  (rotate/scale) OBJ = 10-cycle setup + 2 cycles/pixel. Hardware stops rendering
  sprites for the line once the budget is exhausted — modeled by mGBA's
  `spriteCyclesRemaining` counter, matching real sprite-dropout instead of drawing
  every OBJ regardless.
- **Determinism fit:** pure integer counter — compatible.
- **Puck status: not implemented — we over-render.** `RenderSprites` renders all
  128 OAM entries with no cycle accounting, so overcommitted scenes show *more*
  sprites than hardware. **Verdict (review-b P2 / survey #10): test-first → adopt**
  (M, add a per-line cycle counter to `RenderSprites`, decrement by the 1 /
  (10+2) cost model, break when exhausted; needs correct bit-5 plumbing). Cheap
  and self-contained — a per-line quantity that works fine while staying
  scanline-batched. Good ROI; gate on a sprite-overcommit ROM + render-hash.
- **See also:** VRAM/OAM access conflicts below.

### Affine internal reference-point latch (VBlank reload + mid-frame immediate write)

- **Source:** Tonc, *Affine Backgrounds*,
  https://www.coranac.com/tonc/text/affbg.htm ; VBA-M fix commit,
  https://github.com/visualboyadvance-m/visualboyadvance-m/commit/b96da415c1c482a0ce713d527afda1a53648b008.
- **Finding:** BG2X/Y/BG3X/BG3Y copy into internal working registers at VBlank,
  then increment by dmx/dmy each scanline. A write *outside* VBlank immediately
  overwrites the internal register — the new value becomes the origin for the
  *current* scanline, not queued to the next VBlank. A scanline-batch renderer
  that snapshots BGxX/Y at the wrong boundary gets per-line affine effects (Mode
  7-style floors) subtly wrong; VBA-M shipped exactly this bug.
- **Determinism fit:** integer accumulators — compatible.
- **Puck status: unclear → treat as partial.** `RenderAffineBackground` exists but
  the digest says nothing about internal-vs-external latching or the VBlank-reload
  / immediate-write semantics, so there is real risk the batch model snapshots at
  the wrong boundary. **Verdict (review-b P4 / survey #11): test-first** (S–M, add
  `bgxRefInternal` accumulators, reload at VBlank, increment per line, overwrite on
  a mid-frame write) — verify against the Tonc affbg demo *before* assuming a bug.
- **See also:** HBlank-DMA raster effects below.

### Mosaic sampled from the unmosaiced source

- **Source:** mGBA issue #1008 (*vertical sprite mosaic bug*),
  https://github.com/mgba-emu/mgba/issues/1008 ; Tonc `mos_demo.gba`,
  https://www.coranac.com/tonc/text/gfx.htm.
- **Finding:** mosaic must snap sample coordinates to block origins computed from
  the *unmosaiced* per-pixel/per-affine-sample source, not from an
  already-composited buffer, or vertical block boundaries drift. Mosaic-on-affine
  is a known trap even for shipping emulators (mGBA's vertical-sprite-mosaic bug
  was demonstrated against `mos_demo.gba`; VBA-M broke horizontal sprite mosaic
  post-1.7.2; no$gba effectively disables vertical mosaic).
- **Determinism fit:** integer snapping — compatible.
- **Puck status: partial (right technique, unverified edges).** Mosaic (BG + OBJ)
  is implemented "via sample-coordinate snapping" — the correct approach in
  principle — but the vertical-boundary and affine-interaction cases are
  unverified. **Verdict (review-b P5): test-first** (S to validate, M if the
  affine interaction is missing) — run `mos_demo.gba` under render-hash; fix only
  what diverges.
- **See also:** the affine latch above.

### Window coordinate clamp (X2>240 / X1>X2 → X2=240)

- **Source:** GBATEK (rust-console mirror), *Windows*,
  https://rust-console.github.io/gbatek-gbaonly/ ; RadDad772, *Notes on GBA PPU
  Windows and Blending*,
  https://raddad772.github.io/2025/01/02/notes-on-GBA-PPU-windows-and-blending.html.
- **Finding:** GBATEK states garbage WINxH (X2>240 or X1>X2) is interpreted as
  X2=240, and WINxV (Y2>160 or Y1>Y2) as Y2=160 — i.e. clamp to full extent.
  Windows are evaluated fresh per scanline, which is what enables the HBlank-DMA
  non-rectangular-window trick. Window logic is genuinely fiddly — RadDad772 needed
  three iterations to get four window types (WIN0/WIN1/OBJ-window/WINOUT) each
  gating BG visibility, OBJ visibility, and color-effect enablement.
- **Determinism fit:** integer compares — compatible either way.
- **Puck status: partial / possibly divergent — FLAG.** `InWindow` implements
  *wraparound* (left>right / top>bottom) semantics, which is **not** the same as
  GBATEK's clamp-to-240 for inverted bounds. One of them is wrong for X1>X2. But
  hardware behavior for inverted window bounds is *contested across emulators*.
  **Verdict (review-b P6, dubious-claim §b2): test-first** (S, one comparator in
  `InWindow`/`WindowMaskAt`) — do **not** blind-flip to the GBATEK clamp; write a
  window edge-case probe and match hardware / ares-mGBA co-sim first.
- **See also:** blend fine rules below.

### Blend fine rules (semi-transparent OBJ, brightness suppression, no OBJ self-blend)

- **Source:** GBATEK (rust-console mirror), *Special Effects*,
  https://rust-console.github.io/gbatek-gbaonly/ ; RadDad772,
  https://raddad772.github.io/2025/01/02/notes-on-GBA-PPU-windows-and-blending.html.
- **Finding:** (a) a semi-transparent OBJ is *always* 1st target and *always*
  alpha, regardless of BLDCNT bits 4/6-7, and works even when color SFX are
  window-disabled. (b) if a semi-transparent OBJ overlaps a 2nd-target pixel,
  semi-transparency wins and brightness inc/dec is suppressed on both targets. (c)
  OBJ cannot self-blend — only the top-most OBJ pixel participates. (d) priority
  tie-break: sprite → BG0 → BG1 → BG2 → BG3 → backdrop. mGBA precomputes four
  512-entry palette variants (normal/greenswap/highlight/highlight-variant) and
  blends by LUT, dispatching one of four composite functions per line — a fast
  path that still applies the rules.
- **Determinism fit:** integer — compatible.
- **Puck status: partial (structure right).** `Composite` resolves the top-two by
  (priority, draw-order, OBJ-before-BG), which matches (d), and semi-transparent
  (blend) OBJ mode exists; the override rules (a), brightness-suppression (b), and
  self-blend exclusion (c) are not explicitly called out as handled. **Verdict
  (review-b P7): adopt later / test-first** (S–M, refinements to `Composite`, not
  a rewrite) — low structural risk since the compositor already picks
  priority-ordered top-two.
- **See also:** window clamp above, the palette-LUT perf tier below.

### HBlank-DMA scanline-granular raster effects — credit

- **Source:** agb book, *DMA*, https://agbrs.dev/book/articles/dma.html.
- **Finding:** a 160-entry HBlank-DMA array feeds one value per scanline into a
  register automatically each HBlank at ~zero CPU cost — the canonical mechanism
  for per-scanline palette changes, X/Y scroll, affine coefficients, and window
  bounds (the last builds non-rectangular windows). A renderer that snapshots
  registers per *frame* misrenders these; per-*scanline* re-evaluation honors them.
- **Determinism fit:** integer, event-driven — compatible.
- **Puck status: already at SOTA for scanline-granular effects.** HBlank DMA
  (`OnHBlank`) plus per-line `RenderScanline` re-reads registers each line, which
  the research says captures the vast majority of real raster effects. Only
  *sub-line* writes (the per-dot entry above) are uncovered. **Verdict (review-b
  P8): skip** — already SOTA-equivalent at scanline granularity.
- **See also:** scanline-vs-per-dot above.

### VRAM / OAM access-conflict waitstates + OAM HBlank gate

- **Source:** GBATEK memory-timing section, https://problemkaputt.de/gbatek.htm.
- **Finding:** (a) when the CPU touches VRAM/Palette/OAM while the PPU is
  accessing it, hardware inserts a one-cycle CPU waitstate — GBA *stalls*, never
  corrupts (unlike original Game Boy). (b) OAM is CPU-accessible during HBlank
  *only* if DISPCNT bit 5 (H-Blank Interval Free) is set; otherwise it's locked
  through HBlank too — which couples into the sprite budget (1210 vs 954).
- **Determinism fit:** integer stall — compatible.
- **Puck status: partial.** Palette-RAM contention is modeled (`ChargeData`
  one-cycle stall at dot phase `(hcounter&3)==2` in the visible window), but VRAM
  and OAM conflict windows and the bit-5 OAM gate are not clearly modeled.
  **Verdict (review-b P3): adopt later** (M, widen the existing
  `PramContention`/`ChargeData` mechanism to VRAM/OAM and honor bit 5) — we
  already have the PRAM shape; widening is incremental. Prioritize behind the OBJ
  budget.
- **See also:** OBJ cycle budget above.

### VRAM background-fetch open bus

- **Source:** ares issue #1113, https://github.com/ares-emulator/ares/issues/1113
  (fleroviux research, implemented in NanoBoyAdvance).
- **Finding:** the internal BG-VRAM tile-fetch bus is itself open-bus — reading
  tile data beyond a layer's configured map/tile range returns the last-latched
  fetch (from the previous tilemap entry with one BG active, or from another
  active layer's fetch with several enabled). ares built a custom `sbb_reg` ROM
  validated against real-hardware captures.
- **Determinism fit:** integer latch — compatible.
- **Puck status: not implemented.** No BG-fetch open-bus modeling. **Verdict
  (review-a A15, handed to the PPU partition): defer** — surveyed, not
  deep-reviewed for a concrete seam; it collides with the scanline-batch (not
  per-dot) limitation, so it defers with the per-dot arc.
- **See also:** scanline-vs-per-dot above,
  [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md).

### PPU perf: dirty-flag scanline cache + palette-variant blend LUT

- **Source:** mGBA video internals (DeepWiki),
  https://deepwiki.com/mgba-emu/mgba/4.1-gba-video-rendering.
- **Finding:** (a) a `scanlineDirty` bitmap skips re-rendering unchanged lines by
  diffing cached per-line I/O snapshots — memoize-until-invalidated, cheap on
  static screens. (b) the four precomputed 512-entry palette variants blended by
  LUT (above) cut per-pixel blend branches.
- **Determinism fit:** compatible *only if* invalidation is exact — every
  VRAM/OAM/PRAM/IO/affine change must invalidate, or render-hash silently breaks.
  The invalidation logic is the whole correctness burden.
- **Puck status: not implemented (perf-only, accuracy-neutral).** **Verdict
  (review-b P9): adopt later, only if profiling shows PPU-bound** (M each) —
  digest-0 quotes no AGB throughput figure, so there's no evidence we're
  PPU-bound; the subtle invalidation state is exactly the kind that silently
  breaks render-hash, not worth the risk without a measured need.
- **See also:** blend fine rules above.

---

**Already at SOTA in this partition (credit, per review-b §a):** integer
per-scanline rendering *with per-scanline register re-evaluation* (captures the
overwhelming majority of raster effects); priority-ordered top-two blend
selection matching the GBATEK/RadDad772 tie-break; the palette-RAM one-cycle CPU
contention stall (the correct "GBA stalls, never corrupts" behavior — needs
widening to VRAM/OAM); and full feature coverage (all bitmap + tiled modes,
affine BG/OBJ, windows, mosaic via sample-coordinate snapping, semi-transparent
OBJ). The gaps above are edge-case fidelity, not missing features.

**Dubious claim carried forward (review-b §b1):** the digest-2 statement "ares
has no GBA support" is **stale/wrong** — our harness runs `ares-cosim.exe` as a
live GBA oracle and our timer boundary "matches ares exactly." It came from
Wikipedia/Grokipedia summaries; trust the implementation. Do not let its
"philosophy-transfer only" framing weaken the ares co-sim evidence.
