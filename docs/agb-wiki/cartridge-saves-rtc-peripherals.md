# Cartridge, Saves, RTC, and Peripherals

Save-type detection and the RTC are the two subsystems where "SOTA" is defined by
a *database*, not an algorithm: the ROM string-scan heuristic every emulator uses
is known-broken for a nontrivial minority of the library, and the field converges
on a per-game override table keyed by the 4-char game code. Puck's Flash/EEPROM
protocols and the tick-derived S-3511A RTC are strong, and the per-game override
DB **landed this arc**. The remaining gaps are the sensor peripherals and a
possible ROM-mirror-vs-open-bus conflation.

Provenance: `digest-7` (gatherer), `review-c` (deep review), with credit and
post-wave facts from `digest-0` and the implementation.

---

### Save-type detection: the string-scan heuristic and its failure modes

- **Source:** Screwtape's Notepad, *What's the deal with GBA save files?*,
  https://zork.net/~st/jottings/GBA_saves.html ; GBA ROM Patcher, *GBA Save Types
  Explained*, https://gbarompatcher.com/blog/gba-save-types-explained/.
- **Finding:** every mainstream emulator scans the ROM for Nintendo SDK
  backup-library markers (`EEPROM_V`, `SRAM_V`, `SRAM_F_V`, `FLASH_V`,
  `FLASH512_V`, `FLASH1M_V`) — linker artifacts official titles reliably embed.
  It fails when: homebrew lacks a marker; some official ROMs embed *more than one*
  (dead code, multi-region builds); the marker encodes family but not size (512 B
  vs 8 KB EEPROM, 64 K vs 128 K Flash); and at least one title weaponizes the
  ambiguity as anti-piracy — **Top Gun: Combat Zones** carries three conflicting
  strings, and returning *any* backup type trips its anti-piracy lock.
- **Determinism fit:** N/A (detection policy).
- **Puck status: string-scan present as fallback; override DB landed this arc.**
  The core string-sniffs `FLASH1M_V`→Flash128, `FLASH512_V`/`FLASH_V`→Flash64,
  `EEPROM_V`→Eeprom, `SRAM_V`→Sram. The known-broken minority is now handled by
  `AgbGameOverrides` (below). **Verdict (review-c A6 / survey #3): implemented
  (this arc)** for the override layer; the string-scan is the fallback default.
- **See also:** the override DB below, EEPROM/Flash below.

### Per-game override database

- **Source:** mGBA `src/gba/overrides.c`,
  https://github.com/mgba-emu/mgba/blob/master/src/gba/overrides.c ; mGBA blog,
  *Classic NES Series Anti-Emulation Measures*, https://mgba.io/2014/12/28/classic-nes/.
- **Finding:** the reference SOTA implementations key a static table on the 4-char
  game code that force-sets save type and RTC/rumble/gyro/tilt/light-sensor flags
  (mGBA also hardcodes idle-loop addresses). Concrete entries: Pokémon Ruby (AXVJ)
  → FLASH1M+RTC; Emerald (BPEJ) → FLASH1M+RTC+idle-loop; Boktai (U3IJ) →
  EEPROM+RTC+light sensor; WarioWare Twisted (RZWJ) → SRAM+rumble+gyro; Yoshi's
  Universal Gravitation (KYGJ) → EEPROM+tilt; Advance Wars (AWRE) → FLASH512. This
  is what makes save detection actually SOTA — pure heuristics are known-broken.
- **Determinism fit:** perfect — a compile-time table is a pure function of the
  game code, fully reproducible, no heuristic thresholds.
- **Puck status: implemented (this arc).** `AgbGameOverrides.cs` is keyed by the
  4-char header game code and overrides save/RTC detection *before* the string
  scan: the Top Gun no-backup case, the Classic NES 'F'-family EEPROM force, and
  the known RTC titles. **Verdict (review-c A6 / survey #3): adopt now** — done. It
  is also the correct home for later peripheral keying and any idle-loop DB.
  **Naming rule (honored):** table values/enums do not embed external emulator
  proper nouns in identifiers, though the table is seeded from mGBA's public MIT
  `overrides.c`.
- **See also:** save-type detection above, GPIO sensors below, the Classic NES
  family below.

### EEPROM: 512 B vs 8 KB, DMA-only, protocol, timing

- **Source:** GBATEK, *GBA Cart Backup EEPROM*,
  https://problemkaputt.de/gbatek-gba-cart-backup-eeprom.htm ; Dennis H,
  *[GBA] EEPROM Save Type*,
  https://densinh.github.io/DenSinH/emulation/2021/02/01/gba-eeprom.html.
- **Finding:** EEPROM can only be transferred via DMA3 (the chip needs `/CS` low
  and `A23` high across the whole bitstream — only a DMA burst guarantees it).
  Size is auto-detected from the first DMA3 transfer length: 9 halfwords → 6-bit
  address (512 B); 17 halfwords → 14-bit address (8 KB); both move fixed 64-bit
  blocks MSB-first. This heuristic is *gameable* — the NES Classics do a
  "wrong-length" transfer — so it must be overridable by the game-code DB. Read =
  `11`+addr+`0`, then 4 dummy + 64 data bits; write = `10`+addr+64 data+`0`. Writes
  take ~6.5 ms on hardware (software polls a fixed halfword until bit 0 goes high) —
  for a deterministic emulator this is a fixed tick-count busy period, not a
  wall-clock wait. On a full 32 MB cart, EEPROM decode uses the upper 17 address
  bits, not just the top bit — so decode must know the total ROM size.
- **Determinism fit:** fixed tick-count busy period — compatible.
- **Puck status: already at SOTA (path implemented, untested against a real game).**
  512 B (6-bit) and 8 KB (14-bit) auto-detected from the first command's bit
  length; full serial read (`11`)/write (`10`), 64-bit blocks MSB-first, 68-bit
  read replies. Routed only via DMA accesses at `0x0D…` under the `m_dmaActive`
  gate so a CPU string-scan can't trigger it. The path is untested against a real
  EEPROM game (none on hand) — the override DB + a real title would close it.
  **Verdict: surveyed, not deep-reviewed** for the protocol; the DMA-gate is a
  credited correctness subtlety.
- **See also:** the EEPROM DMA-gate anti-hijack (credit list below), the override
  DB above.

### Flash: one implementation per capacity tier

- **Source:** Dillon Beliveau, *GameBoy Advance Cartridge Backup Storage*,
  https://dillonbeliveau.com/2020/06/05/GBA-FLASH.html.
- **Finding:** the key SOTA insight — *you do NOT need to simulate all the chips*.
  Because the PCB vendor wasn't known at ROM-build time, game code accepts any
  supported manufacturer/device ID and drives them all through one converged
  command protocol; a correct emulator needs only **one implementation per
  capacity tier** (64 K, 128 K). Reference IDs: 64 K = Panasonic (mfr `0x32`, dev
  `0x1B`); 128 K = Sanyo (mfr `0x62`, dev `0x13`). Command protocol is
  unlock-sequence based (three writes to `0x0E005555`/`0x0E002AAA`) for chip-ID
  (`0x90`/`0xF0`), chip erase (`0x10`), byte program (`0xA0`); sector erase
  (`0x30`) is the exception, written to the target sector directly; `0xB0`
  bank-selects the 128 K parts. Atmel is the protocol outlier and is skipped by
  everyone — no shipped game hard-requires it.
- **Determinism fit:** integer state machine — compatible.
- **Puck status: already at SOTA.** 64 KiB and 128 KiB (2-bank) with the full
  unlock-sequence state machine (chip erase, 4 KiB sector erase, byte program,
  bank switch) and identification mode returning Sanyo (128 K) / Panasonic (64 K)
  IDs — skipping Atmel like everyone else. Save round-trip is a Post stage.
- **See also:** the override DB above.

### RTC: the S-3511A protocol under determinism

- **Source:** GBATEK, *GBA Cart Real-Time Clock (RTC)*,
  http://problemkaputt.de/gbatek-gba-cart-real-time-clock-rtc.htm , *GBA Cart I/O
  Port (GPIO)*, http://problemkaputt.de/gbatek-gba-cart-i-o-port-gpio.htm ;
  BizHawk RTC sync-setting issue #1870,
  https://github.com/TASEmulators/BizHawk/issues/1870 ; ares issue #1892,
  https://github.com/ares-emulator/ares/issues/1892.
- **Finding:** Boktai-family titles use the Seiko-Epson S-3511A (3-wire serial)
  over the 6-byte GPIO window folded into ROM space — `0x080000C4` data,
  `0x080000C6` direction, `0x080000C8` port-mode (bit 0 switches ROM-data vs GPIO
  passthrough) — the *same* three-register block RTC, solar, rumble+gyro, and tilt
  all reuse. The control register's bit 7 is a "power lost" flag that self-clears
  on read (games use it to prompt a date re-entry after a dead battery). The
  determinism precedent is settled across the field: BizHawk and libTAS both
  *virtualize* RTC as a recorded/replayable input, never `DateTime.Now` — and a
  documented BizHawk sync-breaking regression (pre- vs post-increment time
  off-by-one-tick) shows how easily an RTC-tick semantics change silently breaks
  replay. ares' pending design keys RTC support off the game-code DB, same pattern
  as save detection.
- **Determinism fit:** perfect — time derived from the deterministic tick, latched
  "power lost" only on a deliberate reset event.
- **Puck status: already at SOTA.** The full S-3511A edge-driven serial protocol
  over GPIO (`StepRtc`), with time computed from `cycles / 16_780_000` off a fixed
  epoch — exactly the posture the field converged on. RTC presence was sniffed
  from `SIIRTC_V` (overridable via `PUCK_GBA_NO_RTC=1`); the override DB now keys
  the known RTC titles. **Verdict (review-c B4 credit): already at SOTA** for the
  RTC itself.
- **See also:** the override DB above, GPIO sensors below,
  [determinism-savestate-replay.md](determinism-savestate-replay.md) (RTC
  virtualization).

### GPIO sensors as replayable commands (solar, tilt, gyro, rumble)

- **Source:** shonumi, *Edge of Emulation* index, https://shonumi.github.io/ ;
  GBATEK, *GBA Cart I/O Port (GPIO)*,
  http://problemkaputt.de/gbatek-gba-cart-i-o-port-gpio.htm.
- **Finding:** solar (Boktai), tilt (Yoshi Topsy-Turvy / WarioWare Twisted Z-axis),
  gyro (WarioWare Twisted), rumble (Drill Dozer/WarioWare) all ride the same
  3-register GPIO block the RTC already uses — shonumi's GBE+ is the reference
  implementation, exposing sunlight intensity / tilt / gyro angle as
  live-adjustable inputs rather than fixed constants.
- **Determinism fit:** perfect *if* each sensor value is a recorded/replayable
  `CommandSnapshot` (the unifying pattern: virtualize, don't disable; never source
  from a real accelerometer/clock) — exactly Puck's per-tick command model.
- **Puck status: partial — the seam exists, only RTC rides it.** The full
  GPIO/RTC serial protocol is implemented; no rumble/gyro/tilt/solar yet.
  **Verdict (review-c A8 / survey #17): adopt later** (M per peripheral family —
  protocol documented, seam built) — each sensor keys off a game-code override
  entry in the DB.
- **See also:** the RTC above, the override DB above.

### The Classic NES Series / anti-emulation carts

- **Source:** mGBA blog, *Classic NES Series Anti-Emulation Measures*,
  https://mgba.io/2014/12/28/classic-nes/ ; Hackaday,
  https://hackaday.com/2016/12/31/anti-emulation-tricks-on-gba-ported-nes-games/ .
- **Finding:** these Nintendo-published NES ports use six confirmed
  anti-emulation tricks: address-mirror probing (jumping into a mirror with
  "don't-care" middle bits set); execute-from-VRAM; an `STM` write-order
  dependency; the save-type SRAM-probe-but-EEPROM-backed bait-and-switch (Game Pak
  Error otherwise — the only fix is a game-code override forcing EEPROM);
  self-modifying-code / prefetch-pipeline abuse; and half-word FIFO audio writes.
  Uniquely, they "mirror out-of-bounds ROM areas" — they read past nominal ROM
  expecting the mirror, and controls go unresponsive without it.
- **Determinism fit:** each trick maps to an accuracy detail already covered
  elsewhere (open bus, prefetch, FIFO, ROM mirror) — compatible.
- **Puck status: partly covered, partly gated on the override DB + ROM mirror.**
  The EEPROM bait-and-switch is now handled by the `AgbGameOverrides` 'F'-family
  EEPROM force (landed this arc); the half-word FIFO writes are handled by the
  ring-model narrow-write streaming (landed this arc,
  [apu-and-direct-sound.md](apu-and-direct-sound.md)); execute-from-VRAM and the
  `STM` order are CPU-correctness items likely already right. The out-of-bounds
  ROM mirror is the open item — see below. **Verdict: test-first** (verify each
  trick against a real title once the ROM mirror is confirmed).
- **See also:** undersized-ROM mirroring below, the override DB above.

### Undersized-ROM modulo mirror vs true open bus

- **Source:** GBATEK / mGBA gbatek DeepWiki,
  https://deepwiki.com/mgba-emu/gbatek/2.1-gba-memory-system ; GBAtemp, *GBA MAX
  ROM Size*, https://gbatemp.net/threads/gba-max-rom-size.530489/ ; mGBA blog,
  *The Infinite Loop That Wasn't*,
  https://mgba.io/2020/01/25/infinite-loop-holy-grail/.
- **Finding:** a ROM smaller than the 32 MB window must repeat at `address %
  romSize` across the window (mirror-wrap) — distinct from *true* open bus on
  genuinely unmapped regions (return the last prefetched opcode). Several emulators
  historically conflated these. Classic NES titles read past nominal ROM expecting
  the mirror. (The cart bus exposes 24 address lines → 32 MB ceiling; Majesco's
  GBA Video carts bank-switch past it to 64 MB — supported by mGBA/VBA-M "for
  years," but no game ROM uses it.)
- **Determinism fit:** neutral (pure addressing math).
- **Puck status: partial / likely conflated — flag.** Out-of-range ROM reads
  return the classic `(address/2)&0xFFFF` pattern — that is *true open bus*, with
  no mention of ROM-size modulo mirroring, suggesting undersized-ROM wrap may be
  missing or merged into the open-bus path. **Verdict (review-c A12 / survey #12):
  test-first** (S to add modulo-wrap; S–M for the 32 MB EEPROM upper-address
  decode) — confirm in `AgbCartridge.ReadRomBurst`/`AgbBus` whether mirror-wrap is
  distinct from open bus; if conflated, fix. Feeds Classic NES compatibility.
- **See also:** the Classic NES family above,
  [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md).

### STOP-mode LCD/sound power-down

- **Source:** GBATEK; `AgbBus.Halt` code comment.
- **Finding:** STOP powers down the LCD and sound; distinct from HALT.
- **Determinism fit:** neutral.
- **Puck status: not implemented — STOP is modeled identically to HALT.**
  **Verdict (review-c A13): adopt later** (S) — a known spec divergence, low
  priority unless a title depends on it.
- **See also:** [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md)
  (HALTCNT).

### Exotic peripherals — skip

- **Source:** shonumi, *Edge of Emulation*, https://shonumi.github.io/ ; David GF,
  *Emulating the GBA Wireless Adapter*,
  https://www.davidgf.net/2024/01/13/gba-wireless-adapter/ ; mGBA multiplayer
  (DeepWiki), https://deepwiki.com/mgba-emu/mgba/9.3-multiplayer-support.
- **Finding:** the e-Reader (FLASH1M + scan HW), Wireless Adapter/RFU (not even in
  mainstream mGBA; gpSP-in-RetroArch is the working path), Play-Yan/Nintendo MP3
  (SD media player), JOY-BUS/GameCube bridge, BattleChip Gate, Mobile Adapter GB,
  and Advance Movie Adapter are all research-heavy, small-audience peripherals.
  shonumi's GBE+ is the field authority (method: watch RCNT reads to intercept
  what the game expects, reverse-engineer the protocol from software behavior).
- **Determinism fit:** N/A.
- **Puck status: not implemented.** **Verdict (review-c A16 / survey folded):
  skip** — revisit only on explicit request. Local same-machine multi-instance
  link is the one thread that could matter to the fleet; it folds into the
  rollback/Tier-C work
  ([determinism-savestate-replay.md](determinism-savestate-replay.md)), not into
  peripheral one-offs. GBE+ / Edge of Emulation is the reference if peripheral
  emulation ever enters scope.
- **See also:** [determinism-savestate-replay.md](determinism-savestate-replay.md).

---

**Already at SOTA in this partition (credit, per review-c §B):** deterministic
RTC from tick (`cycles/16_780_000` off a fixed epoch, "power-lost" latched only
on reset, full S-3511A serial over GPIO); the EEPROM DMA-gate anti-hijack
(`m_dmaActive` at 0x0D so a cart merely embedding `EEPROM_V` — the AGS aging cart
— can't be hijacked, a direct mitigation of a known string-scan failure mode);
and Flash "one impl per capacity tier" with correct vendor IDs, skipping Atmel.
The override DB (landed this arc) complements the EEPROM DMA-gate rather than
replacing it. The open items are the sensor peripherals and the ROM-mirror audit.
