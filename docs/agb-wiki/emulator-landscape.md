# Emulator Landscape

External emulators serve different engineering roles. Puck uses them as independent
evidence sources, not as specifications: implementation agreement is meaningful only
when combined with hardware-derived tests and a clear account of each oracle's
model.

## Reference roles

| Project | Relevant strength | Use in Puck |
|---|---|---|
| [mGBA](https://github.com/mgba-emu/mgba) | Mature compatibility, diagnostics, and a cycle-count-oriented event scheduler | Live co-simulation oracle and a practical comparison for bus, audio, DMA, and cartridge behavior |
| [ares](https://github.com/ares-emulator/ares) | Independent accuracy-oriented architecture and device timing | Live lockstep oracle for timing boundaries; disagreements still require hardware evidence |
| [NanoBoyAdvance](https://github.com/nba-emu/NanoBoyAdvance) | Cycle-focused CPU, DMA, timer, prefetch, and PPU work | Source comparison for narrowly scoped timing questions |
| [SkyEmu](https://github.com/skylersaleh/SkyEmu) | Per-pixel rendering and detailed timing documentation | Reference for mid-scanline effects and the cost of a dot-level PPU |
| [Hades](https://github.com/hades-emu/Hades) | Modern implementation balancing accuracy, performance, and usability | Additional implementation cross-check |
| [VBA-M](https://github.com/visualboyadvance-m/visualboyadvance-m) | Broad compatibility with an older timing model | Compatibility comparison, not a timing oracle |
| [DSHBA](https://github.com/DenSinH/DSHBA) | GPU-side PPU experiment | Evidence for the difficulty of mapping GBA layer selection and blending to GPU fixed-function paths |

No single emulator is authoritative. mGBA intentionally distinguishes cycle-count
accuracy from full component interleaving; ares provides an independent model but
may have less depth in particular GBA subsystems; specialist cores can be valuable
without becoming general-purpose lockstep dependencies.

## Evidence hierarchy

Use evidence in this order:

1. Repeatable measurements from real hardware or hardware-derived conformance ROMs.
2. Agreement among independent emulators whose timing models are understood.
3. Hardware documentation, checked against errata and executable evidence.
4. Game behavior as a compatibility signal, not proof of a hardware rule.

The primary public suites and their use are recorded in
[Test ROMs and Evidence](test-roms-and-evidence.md). AGS, gba-suite,
ARMWrestler, and FuzzARM cover different parts of the machine and should not be
collapsed into a single compatibility score.

## Documentation

[GBATEK](https://www.gbadev.org/docs.php?showinfo=5) is the broadest commonly used
hardware reference. [Tonc](https://gbadev.net/tonc/hardware.html) is particularly
useful for software-visible programming behavior. Neither replaces hardware tests:
published descriptions have contained errors that were reproduced by multiple
independent implementations.

Peripheral work may also consult shonumi's
[GBE+](https://github.com/shonumi/gbe-plus) and
[Edge of Emulation](https://shonumi.github.io/) research, especially where public
device specifications are incomplete.

## BIOS profile

The core executes a supplied BIOS rather than providing a complete high-level SWI
replacement. `AgbBiosProfile` identifies known retail and replacement images for
diagnostics. Accuracy and co-simulation runs require the expected retail profile
unless the caller explicitly permits a replacement BIOS. BIOS images remain
user-supplied and are never committed to the repository.

The open-source [Cult-of-GBA replacement BIOS](https://github.com/Cult-of-GBA/BIOS)
is useful for legally distributable development workflows, but it is not evidence
of retail-BIOS timing parity. See
[Determinism, Savestate, and Replay](determinism-savestate-replay.md) for the
pre-flight and replay contract.
