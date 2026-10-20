# Puck.HumbleGamingDeck.Forge

Puck.HumbleGamingDeck.Forge holds the Humble Gaming Deck's cartridge tooling and
its extension. `DeckProgram` is a small 6502 assembler that packs a program and
its pattern data into an NROM-256 image, or, built at origin $C000, into an
NES 2.0 MMC1 or MMC3 image whose last 16 KiB holds the program; the Post
battery's fixtures, the Deck's board laws and the shipped test cartridges
(`DeckTestCartridges`) are written with it.
`HumbleGamingDeckExtension` registers the `humble-gaming-deck` machine engine,
which mounts iNES and NES 2.0 images as raw bytes, so no content provider
registers beside it.

`puck cartridges deck --output worlds/debug/cartridges/deck` rebuilds
the shipped `.nes` images, and `--check` compares them byte for byte without
writing.

## Documentation

- [Humble Gaming Deck manual](https://github.com/ByteTerrace/Puck/blob/main/docs/emulation/hgd/README.md) — the machine, its hosting and the test cartridges.
