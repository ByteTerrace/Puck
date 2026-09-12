# Puck.World.Machines

This project hosts deterministic machine extensions for World. It owns boot,
stepping, links, content pins, and symbol resolution through the contracts in
`Puck.Abstractions.Machines`. It references no Gaming Brick core or forge project.

`WorldMachineExtensionRegistry` collects the extensions selected by one host.
`Build()` creates an immutable `WorldMachineCatalog`; duplicate engine/provider
identifiers and providers without engines refuse. Dynamic loading requires an
explicit registry. A catalog contains factory and content-provider registrations,
not running machines, and can be shared by that host's replay and instance factories.
Registering another catalog cannot change an existing host.

`WorldMachineHost` implements `IWorldMachineHost`. Its `ValidationCatalog` is the
same catalog used to construct machines. World admission supplies that catalog to
`WorldDefinitionValidator`; structural decoding without one collects deferred
machine checks. Semantic author tools report those missing checks. There is no
machine registration fallback or machine vocabulary module initializer.

Every engine publishes a `MachineEngineDescriptor` for its versioned configuration,
ports, operations, and hardware spaces. Structured construction takes a
`MachineCreationRequest` containing host-prepared assets. Field metadata distinguishes
ordinary values, document references, content paths, and auxiliary asset paths;
the same field walker reaches nested objects and array elements. Missing prepared
assets, unknown fields, and invalid schema tags refuse before construction.

`IMachineRuntime` requires execution status, exact-tick advancement, and disposal.
Video, audio, normalized input ports, and removable content are optional capabilities.
Outputs have provider-owned names and stable identities for the runtime's lifetime.
World resolves video through `IMachineVideoOutputs`; a runtime without it can still
advance headlessly. The brick adapters keep their synchronous hardware entry points
and capture input when each queued tick segment is submitted.

The neutral hardware contract carries unsigned addresses and explicit availability.
`Inspect`, `Patch`, and `Bus` have separate semantics. Both brick adapters implement
coherent worker-boundary reads and validated writes; AGB bus operations use native
access widths and wait states, while SM83 bus operations occur at the current clock
boundary. A patch cannot silently become a hardware-register write.

Named machine memory bindings resolve unsigned raw addresses or prepared content
symbols before admission. Scalar formats cover signed and unsigned 8-, 16-, 32-,
and 64-bit values where the provider supports those widths. Conversion defaults
to checked; truncation is explicit. Bindings run after rules and before machine
advance, in authored order. Reads preserve the last accepted world value on an
unavailable observation. Writes declare on-change or explicit every-tick behavior,
including hardware-visible bus writes. Overlapping writes refuse. Cache identity
includes the instance generation and complete binding declaration.

Content providers own format recognition, validation, compilation, and exported
symbols. The host reads and pins input bytes, asks the selected provider to prepare
recognized source, and mounts the resulting image. `PreparedMachineAsset` retains
the exact host-read source bytes separately from that executable image;
`PreparedMachineContent` carries the image, canonical source identity, addresses,
and any provider-verified format marker independently of cartridge schemas. Provider
validation failures retain the provider's diagnostic.

Named `machines` declarations construct independently of screens and content slots.
Their running flag pauses advancement without discarding hardware state. Named
instances prepare replacement resources before commit; failed and abandoned plans
dispose their staged resources and retain the live device. A replacement receives
a new generation, and a stale plan cannot overwrite a subsequent commit. Runtime
ports must agree with the provider descriptor. `machine.state` reports this state;
`world.row.set machines` and `world.row.remove machines` use ordinary document
mutation and undo authority.

Screens and speakers reference an instance and a provider output name. Removing a
consumer does not remove or restart its machine. Engaged display inputs fold into
the instance's single normalized input port; machines with multiple input ports
can run headlessly, while display routing requires the later explicit port model.
Screen-index cable commands resolve named members and step a linked device once.
A linked device must be unlinked before an operation, replacement, pause, or removal;
preparation refuses those changes while its cable is live.

`machine.operation <instance> <generation> <operationId> <json>` submits a typed
provider operation through the ordered authority domain. It requires Control over
`machine:<instance>` and returns Applied, Unsupported, Refused, or Faulted. The JSON
includes the provider operation's schema identifier. Preparation and complete world
validation precede runtime commit; a successful configuration change becomes the
canonical machine declaration. `screen.insert` and `forge.play` resolve their named
producer and use this same operation path. `screen.eject` detaches the display.

The host checks its selected content-admission policy against the exact source and
executable bytes during preparation. The local default permits content and assets;
a restricted host supplies its own immutable policy at construction.

Provider operations are currently refused while recording. Reaching their runtime
commit barrier also closes boot-only replay/checkpoint capture, since existing
formats cannot reconstruct prior hardware execution. Full machine receipts,
snapshots, authored screen names, explicit control/link routes, and removal of the
remaining screen-owned compatibility paths are still tracked in
[the machine extension plan](../../docs/specs/machine-extensions.md).
