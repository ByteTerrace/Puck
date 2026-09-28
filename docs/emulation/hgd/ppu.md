# Picture processing

The Deck's PPU is the RP2C02G. It draws a 256 by 240 picture one dot at a
time: 341 dots on each of 262 lines, four master ticks per dot, so three dots
pass during every CPU cycle. Lines 0 to 239 are visible, 240 is idle, vertical
blank begins on line 241, and 261 is the pre-render line that prepares line 0.
On odd frames the pre-render line is one dot shorter while rendering is enabled.
[NESdev's PPU rendering page](https://www.nesdev.org/wiki/PPU_rendering) is the
reference for the schedule below.

## What a pixel is

The PPU does not produce colours. Each dot it writes a nine-bit code: six bits
choose one of the 64 colours the chip can generate, and the three emphasis bits
of $2001 sit above them. `HgdPpu.Frame` holds the last completed picture as
those codes, copied when vertical blank begins so a reader never sees a frame
the PPU is still drawing. Turning codes into colours is presentation, described
in [Hosting](hosting.md); the machine's state holds only codes.

## Background

The background comes from a pipeline of fetches and shift registers. For every
tile the PPU makes four memory reads, two dots each: the nametable byte naming
the tile, the attribute byte choosing its palette, and the tile's two pattern
bytes. Dots 1 to 256 fetch the tiles for the rest of the current line, and dots
321 to 336 fetch the first two tiles of the next one. Each fetch drives its
address on one dot and latches the data on the next, and the cartridge observes
every address as it is driven, which is how a board that counts PPU A12 edges
sees them.

Two 16-bit shift registers hold pattern bits and two more hold attribute bits.
They shift once per dot from dot 2 to 257 and from 322 to 337, and dots 9, 17,
…, 257, 329 and 337 reload their low bytes from the latches. The pixel at X
reads bit 15 minus fine X of each register after that dot's shift, so the first
pixel of a line shows the first tile fetched on the previous line.

Scrolling follows the chip's internal registers: `v`, the current address; `t`,
the address the next frame or line starts from; fine X; and the write toggle
shared by $2005 and $2006. Coarse X increments after every tile, the vertical
position increments on dot 256, dot 257 copies the horizontal bits of `t` into
`v`, and dots 280 to 304 of the pre-render line copy the vertical bits.
[NESdev's scrolling page](https://www.nesdev.org/wiki/PPU_scrolling) derives
these rules.

## Sprites

Sprite evaluation runs on every visible line and chooses the sprites the next
line shows. Dots 1 to 64 fill the 32-byte secondary object memory with $FF.
Dots 65 to 256 alternate: an odd dot reads primary object memory at OAMADDR, an
even dot writes to secondary memory and moves OAMADDR. A sprite whose Y range
covers the line is copied whole; after eight, the PPU keeps searching with the
hardware's diagonal increment, which is why the sprite overflow flag is
unreliable in the way games observe. OAMADDR is the evaluation pointer, so a
misaligned OAMADDR evaluates from the middle of an entry, as the chip does.
Once secondary memory is full, the even dots read its first Y byte instead
of writing. Finding another in-range value sets overflow and still reads the
next three primary bytes before scanning later sprites' Y bytes.
Bits 2 to 4 of each sprite's attribute byte have no storage cells; writes leave
them clear, including writes through OAM DMA.

Dots 257 to 320 fetch the eight chosen sprites' patterns, eight dots each, with
two nametable fetches the PPU discards before the pattern bytes; an empty slot
still fetches tile $FF. OAMADDR is cleared on each of those dots. A horizontally
flipped sprite's pattern is reversed as it is fetched, and a vertically flipped
one fetches the mirrored row, in 8 by 8 or 8 by 16 mode.
During background prefetch on dots 321 to 340 and dot 0, $2004 reads the first
byte of secondary object memory.

When a pixel is drawn, the first opaque sprite in slot order wins over the
others. It shows unless its priority bit puts it behind an opaque background
pixel, in which case the background shows and no later sprite gets a turn.
Sprite 0 hit sets when an opaque pixel of the sprite evaluation found first
lands on an opaque background pixel anywhere but X 255. The left-column masks
in $2001 hide the background, sprites, or both in the first eight pixels, which
also prevents a hit there.

## Registers

The eight registers at $2000–$2007 repeat through $3FFF.

| Register | Behaviour |
|---|---|
| $2000 | Nametable select into `t`, increment, pattern tables, sprite size, NMI enable. |
| $2001 | Greyscale, left-column masks, background and sprite enables, emphasis. |
| $2002 | Reads the vertical-blank, sprite 0 hit and overflow flags above the I/O latch's low five bits, then clears vertical blank and the write toggle. |
| $2003 | Sets OAMADDR. |
| $2004 | Reads or writes object memory at OAMADDR; while rendering a read returns the object-memory bus and a write only bumps OAMADDR. |
| $2005, $2006 | Two writes each, sharing the write toggle: scroll, then address. |
| $2007 | Reads through a one-byte buffer below the palette and directly from palette RAM, then increments `v` by 1 or 32, or clocks both scroll counters while rendering. |

The vertical-blank flag sets on dot 1 of line 241 and clears on dot 1 of the
pre-render line together with the sprite flags. The PPU's NMI output is the flag
while $2000 bit 7 enables it, so enabling NMI during vertical blank raises NMI at
once. A $2002 read on the dot before the flag sets returns it clear and keeps it
from setting that frame. Every register write loads the PPU's I/O latch, and
reads return it wherever the PPU drives nothing.

After power-on the PPU ignores writes to $2000, $2001, $2005 and $2006 until the
first pre-render line, as the RP2C02G does.

Palette RAM is 32 bytes. $3F10, $3F14, $3F18 and $3F1C are the same cells as
$3F00, $3F04, $3F08 and $3F0C, and every palette read returns six bits. With
rendering off the picture is the backdrop colour, unless `v` points into palette
RAM, in which case it shows the colour `v` addresses.

## Nametables and mirroring

The console has 2 KiB of nametable RAM, `HgdNametableRam`, but the PPU never
addresses it directly. Every access below $3F00 reaches the cartridge, and the
board selects which 1 KiB page answers by driving CIRAM A10, or answers from
memory of its own. NROM's header chooses horizontal or vertical mirroring, and a
four-screen NROM supplies its own 2 KiB for the upper two nametables.

## Not yet modelled

The PPU is exact to the dot but not within a dot. A CPU access lands between two
dots; the second write to $2006 reaches `v` at once rather than after the
hardware's short delay; and the I/O latch holds its value without decaying. The
remaining $2002 race windows, OAMADDR corruption on the RP2C02G, and the other
chip revisions belong to the NTSC accuracy package, which pins them against the
PPU test suites.

[Humble Gaming Deck](README.md) · [CPU and timing](cpu-and-timing.md) · [Audio](apu.md)
