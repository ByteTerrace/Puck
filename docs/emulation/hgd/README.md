# Humble Gaming Deck

The Humble Gaming Deck is Puck's NES/Famicom core. Its implemented hardware
is the NTSC RP2A03G CPU, the CPU address map, and NROM cartridges. It runs on
an integer master clock and captures snapshots at any tick, including the
middle of an instruction.

Read [CPU and timing](cpu-and-timing.md) for the clock, CPU, cartridge loader,
power-up choices, and verification battery. The PPU, APU, controllers, DMA
arbiter, host adapter, other regions, and other cartridge boards remain outside
this CPU package.

[Machine emulation](../README.md) · [Shared infrastructure](../shared/README.md)
