# APU and Direct Sound

The GBA audio path is entirely integer by hardware design — PSG channel
synthesis, envelope/length/sweep counters, the Direct Sound FIFO/DMA logic, the
SOUNDCNT mix shifts, and the SOUNDBIAS PWM stage — so a bit-exact deterministic
core needs no float anywhere in it. Puck's APU is integer-clean and correctly
structured; the one load-bearing accuracy detail — the FIFO trigger model — was
the naive GBATEK simplification at gather time and **landed this arc** as the
hardware-measured ring model. The remaining gaps are unverified absolute mix
levels and pure-presentation output polish.

Provenance: `digest-3` (gatherer), `review-b` PART 2 (deep review), with credit
and post-wave facts from `digest-0` and the implementation.

---

### The Direct Sound FIFO: 7-word ring + 1 playing buffer, ≥4-empty-word DMA gate

- **Source:** mGBA issue #1847 (*Fix the behavior of the direct audio channels*),
  https://github.com/mgba-emu/mgba/issues/1847 ; GBATEK, *GBA Sound Channel A and
  B (DMA Sound)*,
  http://problemkaputt.de/gbatek-gba-sound-channel-a-and-b-dma-sound.htm.
- **Finding:** GBATEK documents each FIFO as 8×32-bit words, refilled "once
  half-empty." Hardware measurement (mGBA #1847, from a purpose-built timing ROM)
  is different and is the load-bearing model: a **7-word ring buffer + a separate
  32-bit "playing" buffer**. On each timer overflow of the selected timer
  (SOUNDCNT_H bit 10 selects timer 0/1 for A, bit 14 for B): (1) *first*, if the
  ring has **≥4 empty words**, request the FIFO DMA (DMA1/2 in FIFO-timing mode,
  which unconditionally moves exactly 4 words / 16 bytes); (2) *separately*, if
  the playing buffer is empty and the ring has ≥1 word, move one word ring→playing.
  Because the playing buffer empties one word at a time and refill needs 4 empty
  ring slots, **two DMA requests can never occur without an intervening timer
  overflow** — the invariant a deterministic re-implementation must preserve.
- **Determinism fit:** fully compatible — an integer ring + counters driven off
  the deterministic timer-overflow event stream (`OnTimerOverflow`), which the
  core already had. No float/RNG/wall-clock.
- **Puck status: implemented (this arc).** The two `Queue<sbyte>` FIFOs were
  replaced by the 7-word ring + 32-bit playing-buffer model, with the ≥4-empty
  DMA gate and the double-DMA-impossible invariant. The `--oracle`
  `directsound-fifo` gate is a self-checking Post probe that drives `AgbApu`
  directly and asserts the ring size, the invariant, and overflow-resets-to-empty
  (`DirectSoundFifo.SaveState`/`LoadState` also teach the snapshot layer the new
  model). **Verdict (review-b A1 / survey #4): test-first → adopt now** — done.
  Fixes named FIFO-underrun crackle: Mother 3, Sonic Advance 2, The Simpsons Road
  Rage, Super Mario Advance.
- **Calibration (review-b §b3):** GBATEK's "8 words / half-empty refill" is the
  known simplification; trust the hardware-measured 7+1 model.
- **See also:** overflow auto-reset and narrow writes below,
  [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md) (the
  FIFO-mode DMA itself).

### FIFO overflow auto-reset to empty

- **Source:** mGBA issue #1847, https://github.com/mgba-emu/mgba/issues/1847.
- **Finding:** on overrun (more written than consumed), hardware auto-resets the
  FIFO to empty — equivalent to writing the FIFO-reset bit (SOUNDCNT_H bit 11 for
  A, bit 15 for B). It silently drops all buffered samples rather than wrapping or
  corrupting adjacent memory. (Underrun instead repeats/stalls the last playing
  byte — the mechanical cause of audible crackle when refill timing drifts.)
- **Determinism fit:** compatible.
- **Puck status: implemented (this arc), with A1.** The ring model auto-resets on
  overrun rather than unboundedly enqueuing (a `Queue<sbyte>` would have kept
  growing). **Verdict (review-b A2): adopt now (with A1)** — done; same probe ROM
  covers it.
- **See also:** the FIFO ring model above.

### Narrow (8/16-bit) writes to FIFO registers

- **Source:** mGBA issue #1847 + commit c52a5d2,
  https://github.com/mgba-emu/mgba/commit/c52a5d2859a535cd439f914beffeb1f2eb49e9f6.
- **Finding:** an 8-bit CPU write to FIFO_A replaces only the lowest byte of the
  *next* FIFO word, not the whole word; mGBA had to add explicit 16-bit-write
  support. The Classic NES ports exploit this (they write audio 16 bits at a time
  into one half of the 32-bit FIFO register).
- **Determinism fit:** compatible.
- **Puck status: implemented (this arc).** The `0xA0–0xA7` FIFO register windows
  accept 8/16/32-bit writes; the bus decomposes each to the exact bytes it carries
  and streams them in write order (a narrow write fills only part of the next
  word; a whole streamed word is pushed into the ring). **Verdict (review-b A3):
  adopt later** was the pre-wave ruling (low incidence for MP2K's whole-word DMA
  fills) — landed alongside the ring model since the streaming path made it free.
- **See also:** the FIFO ring model above,
  [cartridge-saves-rtc-peripherals.md](cartridge-saves-rtc-peripherals.md)
  (Classic NES half-word FIFO writes).

### FIFO-mode DMA honors the configured (non-FIFO) destination

- **Source:** mGBA issue #1847, https://github.com/mgba-emu/mgba/issues/1847 ;
  Deku's Tree of Art, *Sound Mixing*, https://deku.gbadev.org/program/sound1.html.
- **Finding:** "Sound FIFO DMA is forced to 32-bit, but the destination is NOT
  forced to be the FIFO" — an accurate core uses the channel's configured DAD
  rather than hardwiring FIFO_A/B, which timing-sensitive homebrew exploits. (Only
  DMA1/DMA2 support FIFO timing; word count and transfer-type bits are ignored;
  Repeat stays set so the channel re-arms indefinitely.)
- **Determinism fit:** compatible.
- **Puck status: partial / likely hardwired.** `OnFifo` "runs a 4-word burst into
  the fixed FIFO destination," which reads as hardwired. **Verdict (review-b A4):
  adopt later** (S, use the channel's DAD instead of a constant in
  `AgbDmaController.OnFifo`) — niche but trivially correct; low compatibility risk.
- **See also:** [dma-timers-interrupts-open-bus.md](dma-timers-interrupts-open-bus.md).

### Exact integer mixing formula and absolute levels

- **Source:** mGBA `src/gba/audio.c`,
  https://github.com/mgba-emu/mgba/blob/master/src/gba/audio.c ; NESDev, *GBA
  sound quality…*, https://forums.nesdev.org/viewtopic.php?t=12214 ; GBATEK,
  *Sound Control Registers*,
  https://problemkaputt.de/gbatek-gba-sound-control-registers.htm.
- **Finding:** the concrete integer mix, confirmed pure-integer in mGBA's real
  mixer: PSG `>> psgShift` (25/50/100%); Direct Sound folded as
  `(chX << 2) >> !volumeChX` (the `!volumeChX` boolean-to-0/1 shift trick); final
  `(sample * masterVolume * 3) >> 4`; sum + SOUNDBIAS + clamp to unsigned
  `0..0x3FF`. Per-channel swing: PSG ≤ ±0x80, Direct Sound ≤ ±0x200. SOUNDBIAS
  bits 14-15 select amplitude-resolution/rate (9-bit@32.768 kHz default … 6-bit@
  262.144 kHz), bits 1-9 the DC bias; the console output is literally PWM at
  16.78 MHz.
- **Determinism fit:** all integer shifts/adds — compatible.
- **Puck status: partial — structure right, levels unverified.** `GenerateSample`
  does per-channel pan + 25/50/100% + master 0–7 + Direct Sound 50/100%, but
  SOUNDBIAS is register-plumbed with no audible mixing effect and absolute levels
  are unverified against a reference (Post README §7.E). **Verdict (review-b A5 /
  survey #18): adopt later / test-first** (M, align `GenerateSample`'s
  scaling/clamp to the mGBA integer reference under an audio-hash). Making
  SOUNDBIAS audible changes output — gate it behind an audio-hash so render-hash
  determinism is preserved. SOUNDBIAS PWM modeling (review-b A8) is subsumed by
  this for audible purposes.
- **See also:** band-limited resampling below.

### Band-limited (BLEP / blip_buf) output resampling

- **Source:** Blargg audio libraries, https://www.slack.net/~ant/libs/audio.html ;
  blip_buf, https://github.com/blarggs-audio-libraries/Blip_Buffer ; BizHawk,
  https://github.com/TASEmulators/BizHawk/tree/master/blip_buf.
- **Finding:** the standard classic-sound-chip technique — declare waveform edges
  as (clock-time, amplitude-delta) events, convolve with a precomputed
  band-limited step kernel, deposit fractional-sample-accurate contributions into
  an output ring. Eliminates the aliasing naive decimation produces. blip_buf is
  integer internally.
- **Determinism fit:** **presentation-only — safe.** It consumes the deterministic
  integer edge stream and writes the host-audio ring only; it never feeds back
  into emulated state, so even a float resampler is fine here (it lives outside the
  traced core). The guard: any audio *hash used for verification* must derive from
  the integer edge stream, not the resampled output.
- **Puck status: not implemented — point-sampling today.** Samples are generated
  at `m_cyclesPerSample = MasterClock / sampleRate` (decimation). **Verdict
  (review-b A6): adopt later** (L, emit edge events instead of point samples and
  port blip_buf) — pure presentation polish; do it after the FIFO correctness
  work. blip_buf is integer internally, so no float even enters.
- **See also:** the mixing formula above.

### PSG channels carried forward from DMG/CGB

- **Source:** DeepWiki, *GBA PSG Channels*,
  https://deepwiki.com/mgba-emu/gbatek/2.4.1-gba-psg-channels ; Pan Docs,
  https://gbdev.io/pandocs/Audio.html ; NESDev,
  https://forums.nesdev.org/viewtopic.php?t=12214.
- **Finding:** GBA channels 1–4 are the same DMG/CGB PSG hardware (pulse×2 with
  channel-1 sweep, wave, noise), same 4-bit envelope, same 512 Hz frame sequencer
  — reproduced because the GBA's 16.777216 MHz clock is exactly 4× the DMG's
  4.194304 MHz, so the same divider ratio yields the identical 512 Hz rate.
  SOUNDCNT_H bits 0-1 add a GBA-only global PSG shift (25/50/100%) after DMG-style
  mixing. (NESDev claims internal PSG generation at 262.144 kHz — the SOUNDBIAS
  6-bit rate — but this is a forum post and not load-bearing for us since our PSG
  is cycle-driven off the frame sequencer.)
- **Determinism fit:** integer, cycle-driven — compatible.
- **Puck status: already at SOTA.** Pulse×2 (channel-1 sweep), wave, noise as
  separate channel classes, driven by a 512 Hz frame sequencer clocked from the
  master clock (length on even steps, sweep on 2/6, envelope on 7). **Verdict:
  surveyed, not deep-reviewed** (the reuse-DMG-PSG justification is confirmatory,
  not a new technique to adopt).
- **See also:** the mixing formula above.

### MP2K / "Sappy" HQ float mixer — skip

- **Source:** GBAtemp, *HQ sound in native GBA games is entirely possible*,
  https://gbatemp.net/threads/hq-sound-in-native-gba-games-is-entirely-possible.625549/ ;
  NanoBoyAdvance, https://github.com/nba-emu/NanoBoyAdvance.
- **Finding:** NanoBoyAdvance's opt-in HQ mode detects the game's MP2K/Sappy mixer
  routine and substitutes a native 64 kHz cubic-interpolation float
  re-implementation — an audio-quality enhancement, not accuracy emulation, and
  the *only* place float legitimately appears in any surveyed core.
- **Determinism fit:** **violates the contract** — float in the audio path + HLE
  that detects game code.
- **Puck status: not implemented — deliberately reject.** **Verdict (review-b A7 /
  survey skip): skip** — contract violation and an enhancement not accuracy; our
  per-sample synthesis is already SOTA-granularity. If ever wanted it would have to
  be a fully out-of-core presentation mode with its own non-verified output.
- **See also:** band-limited resampling above (the *safe* presentation-side polish).

---

**Already at SOTA in this partition (credit, per review-b §a):** integer-only APU
with no float / no wall-clock (a *stronger* determinism posture than typical
emulators, matching mGBA's real integer mixer); the 4-word / 16-byte FIFO DMA
burst driven off the timer-overflow event stream (the correct core mechanism —
only the trigger cadence needed the ring refinement, now landed); cycle-exact
timer-overflow-driven FIFO consumption via the timer→bus-access path (the
"catch-up off the event stream, not polled per-sample" pattern done right); and
the 512 Hz frame sequencer from the 4× master clock. The gaps above are level
verification and presentation polish, not missing subsystems.
