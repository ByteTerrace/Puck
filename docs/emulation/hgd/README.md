# Humble Gaming Deck

The Humble Gaming Deck is Puck's NES/Famicom core. Its implemented hardware is
the NTSC console: the RP2A03G CPU with its DMA unit and APU, the RP2C02G PPU,
two standard controllers, and NROM, MMC1 and MMC3 cartridges. It runs on an integer master
clock and captures snapshots at any tick, including the middle of an
instruction.

| Page | Covers |
|---|---|
| [CPU and timing](cpu-and-timing.md) | The master clock, the CPU, interrupts and RDY, power-up, snapshots, the bus, loader and cartridge boards, and the verification battery. |
| [Picture processing](ppu.md) | The PPU's background and sprite pipelines, registers, and nametable mirroring. |
| [Audio](apu.md) | The five channels, the frame counter, and the presentation mixer. |
| [Input and DMA](input-and-dma.md) | The controller ports and the DMA unit. |
| [Hosting](hosting.md) | The engine, the test cartridges, the queued host and synchronous core, and colour. |

## How accuracy is judged

The Deck's accuracy is judged by the community's conformance test ROMs (the
MMC3 IRQ, CPU interrupt, PPU and DMC timing suites and AccuracyCoin, run by the
[battery](cpu-and-timing.md#battery-and-corpora)) and by documented hardware
behaviour, as the [emulation overview](../README.md) states for every core.
Super Mario Bros. 3's status-bar split is the standing example: its first lines
carry a few garbage pixels that vary from frame to frame with where the IRQ
lands in the game's wait loop and with DMC DMA cycles stolen inside its
handler, as they do on a real console.

A World screen hosts the Deck through the `humble-gaming-deck` engine. Other
regions, the other cartridge boards, expansion audio, the Famicom Disk System,
and peripherals remain open work.

## Compiled cartridges

The Deck is a `puck.cartridge.v1` target: a document of target `hgd`
(`*.hgd.puck`) compiles to a native 6502 image through `DeckCartridgeCompiler`
in `Puck.HumbleGamingDeck.Forge`, as the Bricks' documents compile through
theirs ([cartridge forge](../shared/cartridge-forge.md)). It walks the rules
through the shared `CartridgeRuleLowering` and lays out state through
`CartridgeStateLayout`, and assembles with `DeckProgram`. A Deck machine binds
an authored source directly; `DeckContentProvider` compiles it at bind through
the derived-byte cache, and `puck cartridges deck --output <dir>` exports the
images. `worlds/debug/cartridges/deck/moth-valley.hgd.puck` is the first: the
Moth walks the valley floor, the camera following, and a dragonfly asks a
question in a box drawn over the sky.

What the document's sections become on the Deck:

| Section | On the Deck |
|---|---|
| `board` | NROM (32 KiB), MMC1 (to 256 KiB, PRG-RAM) or MMC3 (to 512 KiB, PRG-RAM); absent picks the smallest that fits. Code stays in the 16 KiB fixed at $C000; data fills 16 KiB pages at $8000, page 0 kept mapped for what the frame reads (sound, fades, save defaults) and the rest mapped around boot copies and blits. An image that outgrows its board is the one refusal (`CartridgeCapacityException`). |
| `palettes` | Four background and four object palettes of master-palette indices; colour zero of each is the backdrop. |
| `tiles`, `map`, `mapPalettes` | One tile bank in both pattern tables. The nametables mirror horizontally and the background draws from the first, so the map wraps at 256 pixels across and 240 down: rows 30 and 31 never show, and a cell's palette sets its 16-pixel quadrant's attribute. |
| `map`, `blit` steps | Map writes queue (cell, then any changed attribute byte) and the NMI drains them, landing the frame after; a blit repaints with rendering off and drops the queue. Text is glyph tiles written this way. |
| `sprites` | OAM entries in declaration order (hardware priority), published by OAM DMA in the NMI; palette, flips and `behindBackground` set the attribute byte. A Y of 240 or more hides one. |
| `rules`, `scene` | The main loop waits for the NMI's frame count, reads the pad, steps the sound driver, runs the rules and raises a ready flag; the NMI publishes only a ready frame, so an overrun frame is shown whole at the next one. |
| `sounds` | pulse1 and pulse2 on the pulse channels, wave on the triangle (no waveform), noise on the noise channel. `DeckSoundCompiler` unrolls each row's envelope into register steps the driver pumps. |
| `fade` | A baked table of five brightness levels per direction, published by the NMI. |
| `save` | A checksummed block at $7C00 in battery PRG-RAM (MMC1 and MMC3); arrays sit in internal RAM at $0400 while they fit 1 KiB and in PRG-RAM from $6000 otherwise. |

`window`, `raster`, `layers`, `affine`, `bitmap`, `clock`, `blend`, `plot`,
`turn` and `$light` are refused for `hgd`. The cost profile is
`CartridgeCostProfile.Deck`, measured by `puck cartridge-cost --target hgd`.

[Machine emulation](../README.md) · [Shared infrastructure](../shared/README.md)
