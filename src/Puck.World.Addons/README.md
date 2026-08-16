# Puck.World.Addons — the addon guest host

This project owns the mounted addon guest runtime: `WorldAddonRuntime` (the
`IWorldAddonHost` implementation), `WorldAddonMutationDecoder` (the addon
mutation seam's stage 6 hand-walked JSON decode), `WorldAddonWire` (the
World-side ABI vocabulary mappings — capability bits, grant-rule-to-verdict),
`AddonMutateRefusal` (the `addon.mutate` door's cataloged refusal reasons),
and `AddonSimulationPump` (the crossing from a guest's decoded output cells
to typed, vocabulary-validated submissions). It references
[`Puck.World.Server`](../Puck.World.Server/README.md) directly — the seam
interface (`IWorldAddonHost`) and the wire-format record it persists
(`WorldAddonReceipt`) live there instead, so `WorldServer` never names this
project's concrete types.

Project references: `Puck.World.Server`, `Puck.Scripting` (the WASM guest
ABI `AddonSimulationPump` validates against), `Puck.Maths`, and
`Puck.Assets` (the module bytes a mount reads).

## Mounting and the ABI

A `WorldAddonRow` in the world document's `addons` section is a data-only
descriptor (name, module path, content hash, fuel budget, enabled, requested
capabilities). Mounting happens at boot: `WorldAddonRuntime.Create` compiles
each enabled row's WebAssembly module through `Puck.Scripting`, pins its
content hash, and prints one capability-disclosure line per guest naming
what its manifest requested versus what the grant table actually holds —
granted, withheld, and holds-beyond-manifest. Requesting is not receiving:
deny-by-default holds regardless of the manifest, and authority materializes
only where the row asked AND the table grants (a hold outside the manifest
mints no handle). `WorldAddonWire.cs` is the fixed engine-owned mapping from
a guest's validated acts onto `PlayerIntent` values.

## Lifecycle verbs

`world.addon.mount`/`world.addon.unmount` live-mount a new guest, or fully
remove one, through the ordered submission domain
(`WorldSubmissionPayload.AddonLifecycle`, buffered to the tick boundary
through the same `DrainPendingOps` door a document mutation drains through)
and are captured on the replay tape through their own leaf codec — a
recorded mount/unmount re-executes on `replay.verify`, so they are not
refused while a recording is armed. `world.addon.reload`,
`world.addon.enable`, and `world.addon.disable` are the older, still-live
side path: they manage already-mounted guests synchronously, calling
straight into the concrete `WorldAddonRuntime` outside the ordered domain
and outside the tape, so they stay refused outright while a recording is
armed. `world.addons` is the per-guest cost surface (lifecycle state, fuel
budget, fuel consumed) — an unmounted guest no longer appears there at all.
Guests are pumped only from inside `WorldServer.Step`'s three pinned points
(`IWorldAddonHost.TickAddons`/`ApplyContributions`/`ResolveReads`), which is
what keeps guest driving reproducible under replay without recording it.

## The `addon.mutate` door

`AddonMutateRefusal` catalogs the six-stage dispatch gate's refusal reasons
for a guest-submitted `SubmitMutation` act (stale handle, unrequested
capability, masked mutation kind, budget exhaustion at three granularities,
pointer-safety failure, decode failure, and document-apply rejection).
`RefusalCatalog` (`Puck.World`) reflects over this assembly alongside
`Puck.World.Schema`, `Puck.World.Server`, and the composition root itself to
build `world.refusals`' catalog — a scan that drops this assembly silently
loses the whole `addon.mutate` door.
