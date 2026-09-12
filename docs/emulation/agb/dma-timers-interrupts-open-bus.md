# DMA, timers, interrupts, and open bus

## DMA bus state

Each DMA channel owns its source, destination, count, control, and 32-bit read
latch. Undrivable sources retain that channel's latch; 16-bit transfers mirror
the halfword into both halves. Enable-edge setup latches transfer state before
the first bus access.

A DMA start incurs internal setup cycles and breaks the CPU's sequential fetch
stream, making the following CPU fetch non-sequential. After a transfer, the
last DMA bus value can remain visible through CPU open bus for a short pipeline
window. Puck represents that value separately from the normal CPU fetch latch.

`--oracle` includes self-checking latch and SIO probes plus measurement probes
for startup and fetch sequencing. Reconcile a measurement with the source ROM's
pipeline and stop condition before treating a number difference as a core bug.

## DMA scheduling

Channels are prioritized from 0 through 3 at transfer boundaries. Video-capture
DMA3 runs on the hardware scanline interval. FIFO requests originate in the APU
but retain the DMA channel's configured transfer semantics.

## Timers

Timers use per-cycle prescaler state, delayed register latching, reload values,
and same-cycle cascade from lower timers. Overflow requests its interrupt after
the hardware delay. The reload-write and start/stop boundary cases remain useful
cycle-indexed evidence; do not replace the state machine with elapsed-time
division.

## Interrupts and low-power modes

IE, IF, and IME pass through their modeled synchronization stages. IRQ dispatch
cost also includes BIOS handler location and first-fetch waitstates, so retail
BIOS identity is required for exact region comparisons.

HALT wakes when an enabled interrupt is pending, independent of whether IRQ
entry itself is permitted. STOP is distinct from HALT and has separate display
and sound implications. CpuSet-mediated and already-pending interrupt cases need
the exact BIOS path in timing probes.

## I/O masks and SRAM width

Mapped I/O registers return their documented readable bits rather than generic
prefetch open bus. Puck's readability masks cover the non-PPU I/O map and the
PPU applies its own register masks.

Game Pak SRAM is an 8-bit bus. Wide CPU accesses repeat byte-lane behavior, and
SRAM is not a normal 16- or 32-bit DMA target. Save and jsmolka stages cover
persistence; bus-width changes need a focused access ROM.

## Open accuracy question

The main timing cluster is exact DMA-versus-CPU bus arbitration: startup,
prefetch break, pipeline phase, and interrupt observation can produce several
suite differences from one mechanism. Investigate the group with original ROM
shapes and normalized instruction traces rather than tuning independent delays.

## Sources

- [nba-emu hardware tests](https://github.com/nba-emu/hw-test)
- [GBATEK DMA](https://problemkaputt.de/gbatek-gba-dma-transfers.htm)
- [GBATEK interrupts](https://problemkaputt.de/gbatek-gba-interrupt-control.htm)
