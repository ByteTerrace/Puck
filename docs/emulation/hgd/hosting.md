# Hosting

The Deck runs in a host the same way the Game Boy cores do, through the shared
[machine hosting runtime](../shared/machine-hosting.md). This page covers what
is particular to the Deck.

## The engine

`HumbleGamingDeckEngine` is the Deck's `IMachineEngine`, id
`humble-gaming-deck`, and `Puck.HumbleGamingDeck.Forge`'s
`HumbleGamingDeckExtension` registers it; the World and the CLI compose it as a
built-in, and the silo installs the forge as an extension. Its configuration,
`puck.humble-gaming-deck.configuration.v1`, has one field, `content.path`: an
iNES or NES 2.0 image on an NROM, MMC1 or MMC3 board, mounted as raw bytes, so
no content provider registers with it. The engine judges a file in a machine's
`contentFolders` by its header (`IMachineImageRecognizer`): an image with the
iNES signature and a board and memory layout the Deck implements is mountable,
and any other file is listed as refused with the loader's reason, so an image
of an unimplemented mapper is refused by name rather than started. The engine
takes no options and no firmware: the cartridge starts through its reset vector
with the default power-on profile. Its operations are `content.insert`,
`content.eject` and `machine.reset`, each a host replacement. The engine parses
an image before building a host, so a damaged or unsupported cartridge is
refused with the loader's own diagnostic.

The machine has two input ports, `controls` ($4016) and `controls-2` ($4017).
A world screen that shows it and can be engaged names the port it drives in its
route (`route { port: "controls" }`); the host refuses such a screen whose
route names no port, and the port nobody routes to holds neutral input.

```text
machines [
  {
    name: "deck"
    engine: "humble-gaming-deck"
    configuration {
      schema: "puck.humble-gaming-deck.configuration.v1"
      content {
        path: "../cartridges/deck/colour-bars.nes"
      }
    }
  }
]
```

## Test cartridges

No world runs a downloaded ROM. `DeckTestCartridges` builds the shipped images
with `DeckProgram`, the forge's 6502 assembler, and
`puck cartridges deck --output src/Puck.World/Assets/cartridges/deck` writes
them (`--check` compares without writing):

| Cartridge | Picture | Input |
|---|---|---|
| `colour-bars.nes` | Eight vertical bars: white, yellow, cyan, green, magenta, red, blue, black. | A, B and Select emphasise blue, green and red; Start turns the picture greyscale. |
| `moving-sprite.nes` | A 16 by 16 orange ball bouncing round a navy sky above a green striped ground. | The control pad steers the ball. |

The battery's `shipped-cartridges` stage runs both and reads their pictures from
the PPU's pixel codes.

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
restore discards queued audio and restarts the presentation stream at its
configured rate. The save file contains the cartridge's declared nonvolatile
PRG RAM followed by its nonvolatile CHR RAM, on every board; volatile RAM is
excluded. Normal
flushes write it atomically only when it has changed, and disposal forces a
final flush.

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
Chroma is measured against the colour burst, which is colour 8's phase and decodes
as a yellow, so `$28` is yellow and `$16` red; the battery's `shipped-cartridges`
stage checks the colour bars' hues.
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
