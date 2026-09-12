# APU and Direct Sound

## Direct Sound FIFO

Each Direct Sound channel uses a seven-word queue plus one 32-bit playing
buffer. The timer consumes bytes from the playing word. DMA requests when at
least four queue words are empty. Overflow resets the queue rather than growing
an unbounded software collection.

`AgbApu.DirectSoundFifo` models the queue, playing word, byte position, and
underrun behavior. Its state is included in whole-machine snapshots.

## Narrow writes

FIFO registers accept 8-, 16-, and 32-bit writes. Narrow writes stream bytes in
address order into the next word rather than replacing an entire queued word.
This matters for cartridges that feed audio with halfword stores.

## FIFO DMA

FIFO-mode DMA uses the channel's configured destination and repeat behavior.
The APU supplies the request; the DMA controller remains responsible for the
actual bus transfer and timing. A focused ROM should verify non-default FIFO
destinations before this path is changed.

## Mixing

Emulated channel outputs and the Direct Sound mix are integer state. Exact
absolute levels, SOUNDBIAS PWM resolution, and clipping should be calibrated
from hardware captures. Host sample cadence uses exact rational accumulation
with carried remainder.

Band-limited resampling is a presentation improvement: consume timestamped
integer edge events or samples after the deterministic core boundary. Do not
introduce floating-point feedback into channel state.

## PSG channels

The two pulse channels, wave channel, and noise channel retain the DMG-derived
length, envelope, sweep, and 512 Hz frame-sequencer behavior with GBA register
layout and period scaling. Channel state, wave RAM, and sequencer phase are
snapshot state.

## Excluded mixer

An MP2K-specific floating-point high-quality mixer is not part of the emulator
contract. Game-specific presentation enhancements belong outside emulated
state.

## Sources

- [GBATEK sound controller](https://problemkaputt.de/gbatek-gba-sound-controller.htm)
- [mGBA issue #1847](https://github.com/mgba-emu/mgba/issues/1847)
