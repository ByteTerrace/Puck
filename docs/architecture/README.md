# Engine architecture

The architecture documents explain how a Puck world moves from durable data to
an observed frame. They describe current contracts and settled boundaries; the
source project READMEs own implementation detail.

## The path through the engine

A world follows this path:

1. **Document** supplies durable world data.
2. **Validation** admits a complete composed document.
3. **Simulation** advances authoritative state in fixed ticks.
4. **Snapshots and replay** carry ordered observations and recorded inputs.
5. **Presentation** consumes snapshots, submits input, and controls display
   timing without becoming a second authority.

The [worlds and federation manual](worlds.md) defines the relationships between
worlds, authority, sessions, destinations, and transfer. It also explains why
the document, validation, simulation, snapshot, and presentation layers stay
separate.

For implementation detail, follow the owning projects:

- [World composition root](../../src/Puck.World/README.md) owns the running host,
  console, document composition, screens, and graphics options.
- [World schema](../../src/Puck.World.Schema/README.md) owns document fields,
  serialization, and semantic model shape.
- [World server](../../src/Puck.World.Server/README.md) owns authority, ticks,
  bodies, submissions, persistence, and replay.
- [World protocol](../../src/Puck.World.Protocol/README.md) owns the wire
  messages and codecs between authorities and clients.
- [World client](../../src/Puck.World.Client/README.md) owns input, entity
  views, camera programs, and client-side projection.
- [SDF VM](../../src/Puck.SdfVm/README.md) and [signed-distance programs](../../src/Puck.SignedDistance/README.md)
  own rendering and deterministic world queries.

Development procedures live in the [contributing guide](../development/contributing.md),
and hosted validation and release procedures live in the [CI guide](../development/ci.md).
