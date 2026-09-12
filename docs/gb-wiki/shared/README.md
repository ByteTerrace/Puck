# Gaming Bricks Shared Infrastructure

This section documents the universal hosting contracts, synchronization primitives, multiplayer link session protocols, and cartridge standards shared between the Humble Gaming Brick (`Puck.HumbleGamingBrick`) and Advanced Gaming Brick (`Puck.AdvancedGamingBrick`).

---

## Shared Modules

| Document | Component Focus | Description |
|---|---|---|
| **[Machine Hosting Runtime](machine-hosting.md)** | `Puck.GamingBricks` | The universal `IMachineEngine` and `IMachineRuntime` abstractions, tick-to-cycle remainder accumulation, worker thread boundaries, and snapshot APIs. |
| **[Serial Link Cables & Multi-Device Sessions](link-cables.md)** | `Puck.GamingBricks`, `Puck.HumbleGamingBrick` | Byte-level serial transfer protocols, instruction-atomic interleaving between cores, infrared transceivers, and peripheral printer sessions. |
| **[Universal Cartridge Contracts](cartridge-format.md)** | `Puck.GamingBricks`, Subsystems | Cartridge header parsing, checksum verification, memory-mapped I/O, battery-backed SRAM/Flash persistence, and ROM loading contracts. |
