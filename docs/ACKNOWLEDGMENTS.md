# Acknowledgments and citations

Puck's emulator cores are validated against public hardware-test corpora and
independently-written reference emulators used strictly as *evidence* (never as
gates — see the `gaming-bricks` skill's oracle discipline). This file carries the
citations for publicly-documented hardware facts that informed the implementation,
so that identifiers, comments, and XML docs in the code stay free of external
company / product / emulator proper nouns.

## Advanced GamingBrick (ARM7TDMI / GBA-class) — evidence-first accuracy wave

- **Direct Sound FIFO ring + playing-buffer model** (the 7-word ring + separate
  32-bit playing buffer, the "two DMA requests need an intervening timer overflow"
  invariant, and overrun auto-reset). Hardware measurement:
  <https://github.com/mgba-emu/mgba/issues/1847> ·
  <http://problemkaputt.de/gbatek-gba-sound-channel-a-and-b-dma-sound.htm>

- **Per-channel DMA read latch** and the DMA start-delay / force-non-sequential /
  latch / IRQ-dispatch-latency / HALTCNT / timer reload-race cycle oracles that
  the `--oracle` probe battery reproduces. Documented hardware-test corpus:
  <https://github.com/nba-emu/hw-test> ·
  <https://problemkaputt.de/gbatek-gba-dma-transfers.htm> ·
  <https://problemkaputt.de/gbatek-gba-timers.htm>

- **Per-game save-type / RTC override database.** The behaviours are public
  documentation; no external emulator's data file was copied. Top Gun: Combat
  Zones triple-decoy anti-piracy lock and the Classic NES / Famicom Mini
  SRAM-bait-but-EEPROM family:
  <https://mgba.io/2014/12/28/classic-nes/> ·
  <https://zork.net/~st/jottings/GBA_saves.html> ·
  <https://tcrf.net/Top_Gun:_Combat_Zones_(Game_Boy_Advance)>

- **BIOS pre-flight hash identification** (refusing cycle-parity work on a
  non-retail BIOS — the documented "phantom cycle drift" trap; mismatched
  ROM/BIOS hash is a top cause of divergence):
  <https://mgba.io/2020/01/25/infinite-loop-holy-grail/> ·
  <https://www.smashladder.com/guides/view/26pv/desync-troubleshooting-guide>

## Reference emulators and test suites (co-simulation oracles / evidence)

Independently-written emulators and community test suites, used as differential
oracles and conformance evidence only:

- mGBA — <https://github.com/mgba-emu/mgba> (and its test suite,
  <https://github.com/mgba-emu/suite>)
- ares — <https://ares-emu.net/>
- jsmolka `gba-tests` — <https://github.com/jsmolka/gba-tests>
- FuzzARM — <https://github.com/DenSinH/FuzzARM>
- AGS aging cartridge test spec (AGSTests) — <https://github.com/DenSinH/AGSTests>
- GBATEK hardware reference — <https://problemkaputt.de/gbatek.htm>
