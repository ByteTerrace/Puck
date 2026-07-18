# CPU pipeline, prefetch, and waitstates

## Game Pak prefetch

The Game Pak prefetch unit is an eight-halfword FIFO. It fills only while the
CPU is not using the ROM bus. A ROM-bus miss advances the bus clock without also
filling the FIFO. Branches and other non-sequential fetches invalidate the
stream.

`AgbBus` models this with prefetch slots, a load address, wait progress, and
explicit reset/synchronize operations. `PUCK_NO_PREFETCH=1` is an A/B diagnostic,
not a supported machine mode.

## Cycle attribution

ARM7TDMI timing distinguishes sequential, non-sequential, and internal cycles.
Pipeline refill, exception entry, branch, and memory instructions must charge
their bus accesses in execution order. A condition-failed instruction still
consumes its fetch and condition-check timing.

Puck uses an explicit three-stage pipeline and carries `BusAccessType` through
the bus. Compare traces by completed-instruction deltas; external emulators can
attribute the next instruction's fetch to a different row.

## Multiplication

Multiply timing terminates early according to the significant byte pattern of
the multiplier. The open accuracy question is the carry flag produced by the
final Booth iteration for long-multiply variants. Settle it with a focused ROM
that stores both result and CPSR, not a timing-only test.

## WAITCNT

WAITCNT selects SRAM wait, first and sequential Game Pak wait for each mirror,
and prefetch enable. The same ROM image appears in the 0x08, 0x0A, and 0x0C
regions with different timing classes. Crossing a 128 KiB boundary requires a
new non-sequential charge.

Puck models the public WAITCNT fields. The undocumented fast-EWRAM register at
0x04000800 and the prefetch-disabled timing anomaly require hardware-focused
evidence before implementation.

## Open bus

ARM open bus is pipeline-visible state, commonly represented by the word at
`PC+8`. Thumb behavior depends on region and alignment and can combine halves
from consecutive pipeline fetches. `AgbBus` stores per-fetch word and halfword
latches instead of treating the most recent arbitrary bus read as open bus.

This state is part of snapshots. DMA can temporarily supply its lingering bus
value; see [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md).

## Interpreter structure

Keep timing decisions beside the operation that consumes the bus. A decoded
instruction may look ahead to classify the next fetch, but an optimization must
not bypass waitstate, open-bus, prefetch, DMA, or snapshot behavior. Profile the
current table dispatcher before adding block linking or fast-memory paths.

## Sources

- [GBATEK memory control](https://problemkaputt.de/gbatek-gba-memory-control.htm)
- [mGBA, *If You Give a Game Boy Advance a Cookie*](https://mgba.io/2015/06/27/cookie/)
- [NanoBoyAdvance hardware tests](https://github.com/nba-emu/hw-test)
