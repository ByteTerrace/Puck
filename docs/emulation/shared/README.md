# Shared emulation infrastructure

The shared `Puck.GamingBricks` layer provides the host contracts used by both
machine families. It owns worker lifetime, integer tick-to-cycle pacing,
backpressure, frame and audio publication, snapshots, and multi-device link
coordination. Hardware behavior remains with the [Humble Gaming Brick](../hgb/README.md) or
[Advanced Gaming Brick](../agb/README.md) core.

## Shared contracts

- [Machine hosting runtime](machine-hosting.md) explains `IMachineEngine`, queued
  workers, cycle pacing, buffers, audio, save flushing, and state capture.
- [Serial link cables and multi-device sessions](link-cables.md) explains serial sessions,
  infrared devices, printer links, and instruction-atomic interleaving.
- [Universal cartridge contracts and storage](cartridge-format.md) explains cartridge admission,
  headers, memory maps, banking, and persistent saves.

When integrating a machine into an application, start with machine hosting and
then read the core-specific entry for hardware and firmware choices. When
comparing machine behavior, use the core's evidence and Post documentation;
the shared layer supplies coordination and state transport rather than a
hardware oracle.
