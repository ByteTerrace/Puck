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

The owner's ruling: the Deck's accuracy is judged by the community's
conformance test ROMs (the MMC3 IRQ, CPU interrupt, PPU and DMC timing suites
and AccuracyCoin, run by the [battery](cpu-and-timing.md#battery-and-corpora)),
never by matching another emulator's output or by eyeballing a game. A timing
change lands only to fix a failure one of those suites proves against
documented hardware behaviour, and nothing is tuned for a particular game.
Super Mario Bros. 3's status-bar split is the standing example: its first lines
carry a few garbage pixels that vary from frame to frame with where the IRQ
lands in the game's wait loop and with DMC DMA cycles stolen inside its
handler, as they do on a real console.

A World screen hosts the Deck through the `humble-gaming-deck` engine. Other
regions, the other cartridge boards, expansion audio, the Famicom Disk System,
and peripherals remain open work.

[Machine emulation](../README.md) · [Shared infrastructure](../shared/README.md)
