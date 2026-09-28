# Input and DMA

This page covers the two controller ports and the 2A03's DMA unit, the part of
the chip that takes the bus away from the CPU to copy memory.

## Controllers

Each port holds a standard controller. A controller keeps its eight buttons in a
shift register. Writing 1 to bit 0 of $4016 raises the strobe, which keeps both
controllers reloading their buttons; writing 0 lowers it. After that, every read
of $4016 or $4017 returns that port's next button on data bit 0 and shifts, in
the order A, B, Select, Start, Up, Down, Left, Right, and returns 1 once all
eight have been read. Bits 5 to 7 of a port read are CPU open bus, which is why
`LDA $4016` usually returns $40 or $41: the last byte on the bus was the high
byte of the address. The read is a side effect, so a DMA that repeats it shifts
the controller again.
[NESdev's standard controller page](https://www.nesdev.org/wiki/Standard_controller)
describes the protocol.

The host folds each seat of the held `MachinePads` image onto a port with the
same positional mapping the Game Boy cores use: the bottom face button is A, the
right one B, Back is Select, and the d-pad or the left stick is the control pad.
Seat 0 is the $4016 controller and seat 1 the $4017 one. Held buttons are part
of the machine's state.

## DMA

Two things use DMA. Writing a page number to $4014 copies that 256-byte CPU page
into the PPU's object memory, and the APU's DMC fetches its sample bytes the same
way. `HgdDma` implements both; [NESdev's DMA page](https://www.nesdev.org/wiki/DMA)
is the reference.

A DMA request stops the CPU through its RDY input, but RDY cannot stop a write,
so the unit waits for the CPU's next read cycle. That first cycle is the halt:
the CPU performs its read, and will perform it again. From then on every CPU
cycle is either a DMA transfer or a cycle on which the halted CPU repeats its
pending read, with every side effect the read has. That is why a DMA landing on
an instruction that reads $2007 or $4016 reads it more than once.

Transfers alternate between get cycles, which read, and put cycles, which write.
An OAM DMA reads a byte on a get cycle and writes it to $2004 on the next put
cycle, 256 times. With the halt cycle and one alignment cycle when it starts on
a put, it takes 513 or 514 CPU cycles depending on the cycle parity it starts
on. A DMC DMA spends one dummy cycle after the halt, waits for a get cycle if it
needs to, and reads its byte; when both want the same get cycle, the DMC goes
first.

## Not yet modelled

Which cycle parity is a get cycle after power-on is fixed, not yet measured
against hardware. The DMC's separate load and reload timings, DMA aborts, the
2A03's partial address decoding during halted reads of $4000–$401F, and
expansion devices such as the Four Score and the Zapper belong to the accuracy
and peripherals packages.

[Humble Gaming Deck](README.md) · [Audio](apu.md) · [Hosting](hosting.md)
