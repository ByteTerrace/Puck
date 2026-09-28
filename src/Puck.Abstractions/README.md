# Puck.Abstractions

Puck.Abstractions is the engine's neutral contract layer: backend- and
platform-agnostic interfaces and small value types that every subsystem
implements and depends on.

The optional `IGpuTimestampFactory` creates named observational query pools.
`GpuDeviceServices` carries it through the ordinary counting and creation-fault
wrappers; unsupported queues return no pool. Consumers fence readback and never
use durations as deterministic work or simulation input.

## Documentation

- [Seam abstractions](https://github.com/ByteTerrace/Puck/blob/main/docs/reference/abstractions.md) — presentation, windowing, machine, platform, and work-counting contracts.
- [Engine manual](https://github.com/ByteTerrace/Puck/blob/main/docs/README.md) — setup, architecture, and related libraries.
- [Development and verification](https://github.com/ByteTerrace/Puck/blob/main/tests/Puck.Abstractions.Tests/README.md).
- [License](https://github.com/ByteTerrace/Puck/blob/main/LICENSE.md): Apache 2.0.
