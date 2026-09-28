# Humble Gaming Deck

The Humble Gaming Deck is Puck's NES/Famicom core. Its implemented hardware is
the NTSC console: the RP2A03G CPU with its DMA unit and APU, the RP2C02G PPU,
two standard controllers, and NROM cartridges. It runs on an integer master
clock and captures snapshots at any tick, including the middle of an
instruction.

| Page | Covers |
|---|---|
| [CPU and timing](cpu-and-timing.md) | The master clock, the CPU, interrupts and RDY, power-up, snapshots, the bus and loader, and the verification battery. |
| [Picture processing](ppu.md) | The PPU's background and sprite pipelines, registers, and nametable mirroring. |
| [Audio](apu.md) | The five channels, the frame counter, and the presentation mixer. |
| [Input and DMA](input-and-dma.md) | The controller ports and the DMA unit. |
| [Hosting](hosting.md) | The engine, the queued host and synchronous core, and colour. |

Other regions, other cartridge boards, expansion audio, the Famicom Disk System,
peripherals, and World cabinets remain open work.

[Machine emulation](../README.md) · [Shared infrastructure](../shared/README.md)
