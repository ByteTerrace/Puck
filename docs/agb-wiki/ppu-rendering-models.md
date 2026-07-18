# PPU rendering models

## Current model

`AgbPpu` renders one scanline at a time and re-evaluates scanline-visible
register state. Scheduled HBlank and scanline events preserve DMA and interrupt
ordering. This represents scanline-granular raster effects without requiring a
per-dot renderer.

## OBJ budget and dropout

Hardware limits object evaluation by a per-line cycle budget. The budget differs
with HBlank interval-free mode and determines which later sprites drop out.
Puck charges visible OBJ work against this budget and applies the green-swap
behavior. Render hashes and the mGBA suite cover the observable result.

## Affine backgrounds

BG2X/Y and BG3X/Y load internal reference registers at frame start. Each
scanline advances those internal values by the affine increments. A mid-frame
write replaces the internal reference immediately, so the new origin affects
the current raster progression.

## Mosaic, windows, and blending

These details need focused evidence when changed:

- vertical and affine mosaic sample from the unmosaiced source;
- inverted or out-of-range window bounds have hardware-specific clamping;
- semi-transparent OBJ selection, brightness modes, and OBJ self-blending have
  precedence rules that a simple layer sort does not express.

Use small scene ROMs that isolate one rule and compare rendered pixels, not
screenshots with multiple effects active.

## Memory access conflicts

CPU VRAM and OAM availability changes across drawing and HBlank. Background
fetch open bus depends on the active fetcher and is difficult to represent in a
pure scanline renderer. Implement access-conflict waits from cycle-counted ROMs.
Defer fetch-open-bus behavior until a per-dot model or equivalent state machine
has supporting content evidence.

## Per-dot rendering

A per-dot PPU can represent mid-scanline register writes, fetch open bus, and
sub-scanline access conflicts. It is an architectural change, not an automatic
accuracy improvement. Preserve scheduled DMA/IRQ events and prove existing
render hashes before replacing the scanline model.

## Performance

Dirty-scanline caches and palette-variant blend tables are valid only after a
profile identifies PPU rendering as the bottleneck. Cache keys must include
every register and memory dependency that can change the rendered pixels.

## Sources

- [GBATEK display system](https://problemkaputt.de/gbatek-gba-lcd-videocontroller.htm)
- [Tonc affine backgrounds](https://www.coranac.com/tonc/text/affbg.htm)
- [GBAtek-derived Rust reference](https://rust-console.github.io/gbatek-gbaonly/)
