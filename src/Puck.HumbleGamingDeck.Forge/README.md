# Puck.HumbleGamingDeck.Forge

Puck.HumbleGamingDeck.Forge holds the Humble Gaming Deck's cartridge tooling and
its extension. `DeckProgram` is a small 6502 assembler that packs a program and
its pattern data into an NROM-256 image, or, built at origin $C000, into an
NES 2.0 MMC1 or MMC3 image whose last 16 KiB holds the program; the Post
battery's fixtures, the Deck's board laws and the shipped test cartridges
(`DeckTestCartridges`) are written with it.
`HumbleGamingDeckExtension` registers the `humble-gaming-deck` machine engine,
which mounts native images directly. Its `DeckContentProvider` prepares authored
`.deck` selections through the shared code-keyed derived-byte cache.

`puck cartridges deck --output <dir>` exports native `.nes` images when needed.
Worlds commit `.deck` sources and derive executable bytes at boot, with counted cache misses and hits.

## Documentation

- [Humble Gaming Deck manual](https://github.com/ByteTerrace/Puck/blob/main/docs/emulation/hgd/README.md) — the machine, its hosting and the test cartridges.
