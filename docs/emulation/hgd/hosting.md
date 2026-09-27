# Hosting

The Deck runs in a host the same way the Game Boy cores do, through the shared
[machine hosting runtime](../shared/machine-hosting.md). This page covers what
is particular to the Deck.

## The engine

`HumbleGamingDeckEngine` is the `IMachineEngine` with id `humble-gaming-deck`.
Its configuration names the cartridge under `content.path`; it takes no option
string, because the NTSC model and the default power-on profile are its only
configuration today. It declares one video output, `video`, one audio output,
`audio`, and two input ports: `controls` for the $4016 controller and
`controls-2` for $4017. The engine is not yet registered with World; wiring it
into World's cabinets is the World cabinets package.

## The host and the core

`DeckMachineHost` is a `QueuedMachineHost` over a `HumbleGamingDeckCore`. The
host converts each exact engine-tick budget into master ticks through the shared
rational pacing, so the 236,250,000/11 Hz master clock paces with no drift, and
it offers the shared queue, backpressure, checkpoints, rewind, runahead and
fast-forward. Its debug memory window reads the CPU address map without side
effects and writes only work RAM, on the worker thread between steps.

`HumbleGamingDeckCore` works without a worker or renderer too. It advances the
machine by master ticks, loads the two controller ports from the first two
seats of the held input, drains audio, and captures and restores the machine's
whole state. `NativeFrameIndex` counts completed frames by the PPU's vertical
blank, so it stays exact even though an odd frame is one dot shorter. A
cartridge whose header declares a battery keeps its PRG RAM in the save file,
written atomically only when it has changed.

```csharp
using Puck.HumbleGamingDeck;
using Puck.Machines;

using var core = new HumbleGamingDeckCore(cartridgeImage: File.ReadAllBytes(args[0]));

core.ConfigureAudio(sampleRate: 48_000);
core.ApplyInput(input: MachinePads.Neutral);
core.RunCycles(cycles: 357_368); // one even NTSC frame of master ticks
uint[] pixels = core.Framebuffer.ToArray(); // 256 × 240, packed 0x00RRGGBB
short[] audio = new short[4096];
int sampleCount = core.DrainAudioSamples(destination: audio); // interleaved L/R
```

## Colour

The core converts the PPU's nine-bit pixel codes to `0x00RRGGBB` with
`HgdPalette`. The palette is decoded once from a model of the RP2C02G's composite
video signal: each code is a square wave between two of the chip's voltage
levels at one of twelve hue phases, the emphasis bits attenuate parts of the
wave, and averaging a colour cycle gives the luma and chroma that convert to RGB.
[NESdev's NTSC video page](https://www.nesdev.org/wiki/NTSC_video) gives the
levels. This is floating-point presentation work, done only when a new frame is
read, and the machine's state never sees it.

## Verification

The battery's host-contract stages run the shared probes against the Deck:
embedding without a worker, checkpoints, backpressure, whole-frame publication,
audio gated on attachment, rewind and runahead, and the marshalled memory
window. [CPU and timing](cpu-and-timing.md#battery-and-corpora) lists every
stage.

[Humble Gaming Deck](README.md) · [Input and DMA](input-and-dma.md) · [Machine emulation](../README.md)
