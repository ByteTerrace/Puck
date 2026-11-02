# Puck.World.Addons

This project owns the mounted addon guest runtime: `WorldAddonRuntime` (the
`IWorldAddonHost` implementation), `WorldAddonMutationDecoder` (the addon
mutation seam's stage 6 hand-walked JSON decode), `WorldAddonWire` (the
World-side ABI vocabulary mappings—capability bits, grant-rule-to-verdict),
`AddonMutateRefusal` (the `addon.mutate` door's cataloged refusal reasons),
and `AddonSimulationPump` (the crossing from a guest's decoded output cells
to typed, vocabulary-validated submissions). It is dedicated strictly to WASM
scripting guests and remains free of emulator implementation
dependencies (which live in [`Puck.World.Machines`](../Puck.World.Machines/README.md)).
It references [`Puck.World.Server`](../Puck.World.Server/README.md) directly—the
seam interface (`IWorldAddonHost`) and the wire-format records it persists
(`WorldAddonReceipt`) live there instead, so `WorldServer` never names this
project's concrete types.

Project references: `Puck.World.Server`, `Puck.Scripting` (the WASM guest
ABI `AddonSimulationPump` validates against), `Puck.Maths`, and `Puck.Assets`
(the module bytes a mount reads).

Development guests can bulk-read an observed set's poses into guest memory with one
completion cell (`BodyPoses`), query a body's settled kinematics and numeric authored
state cells or bounded text chunks, read/hash machine memory, capture/restore one bounded
checkpoint, engage a driven body and submit generic provider operations. They write
individual state cells through the ordinary mutation door,
and emit bounded lines into `host.log`. The [ABI reference](../../docs/reference/scripting.md#request-channelquery-acts-through-a-handle)
owns their wire layouts, authority, limits and host fuel charges.

`IWorldAddonHost` implements the shared `IWorldExtensionRuntime` contract with
recomputed contributions. Capability-manifest matching is shared with recorded
providers through `WorldCapabilityRequests.Contains`. Service calls belong at
the [external-operation boundary](../Puck.World.Server/Extensions.md), where
replay consumes recorded contributions without running the provider.

## Durable guest state

Both World and the silo mount through `WorldAddonRuntime.TryCreate`. The silo
owns each activation's runtime and disposes it when the row retires; its replay
factory uses the same runtime.

`IWorldAddonHost` captures a strict addon section in the ordinary
`WorldAuthorityCheckpoint`. It includes every guest's full grown linear memory,
numeric globals (including private globals), remaining fuel and sticky fault,
plus next-tick answers, disclosure projections, event-gap and watch baselines,
bound cell names/generations, debugger machine snapshots with their named machine
incarnations, and cost counters. A snapshot keeps its identity even while its
machine has no display consumer; a replaced incarnation still refuses it.
Resolved arena addresses and text-decode caches are derived again. Each guest's
exact module source is retained in the image and checked against its source pin,
so a new deployment can recover old memory without reading a changed file.
Restoration prepares and decodes the entire guest set before any live authority
state changes, then replaces the mounted set by reference;
it preserves the authority's restored input controllers. A server reconstructed
before host composition retains the image until addon attachment and refuses
to step or capture while it is pending.
Local synthetic input sessions have no socket observer by design; startup ends
only unobserved sessions without a local controller owner, preserving these
producers' checkpointed histories.

The loader adds host-only global exports to the compiled binary, preserving
the source pin, global indices and executable instructions. No guest-specific
save program is required. Mounting refuses modules with tables, element sections,
data-count sections (which can carry segment-drop state), reference globals or
no live store. Those resources cannot yet be reconstructed by this serializer;
none is silently omitted. The debug world's seven guests use supported state.
Replay recording and history preserve the same live guest state as persistence.
Checkpoint JSON requires every constructor member and refuses unknown members,
null collections and malformed images by name. Its shape changes with the authority
checkpoint version; there is no reader for retired shapes.

Compilation is shared by content pin across process-lifetime runtime hosts;
stores, memory and host continuations remain private to each mount. Concurrent
loads wait on the host semaphore. A recovery prepares and narrates each mount once.
The current file is still read for a fresh or changed declaration, so ordinary
published-content reconciliation starts changed guests fresh and preserves the
surrounding recovered world state. Memory captured under an old module is never
fed to a changed module.

Fuel bounds deterministic work. A host timer also advances Wasmtime epochs every
10 ms; each instantiation, initialization and tick call gets ten epochs. The
watchdog uses host waits and never spins. An interrupted tick freezes that
authority and refuses capture of its incomplete state; the shared pump continues
other rows, and recovery starts from the last durable checkpoint. Wall time never
becomes a simulated guest fault. A bulk memory instruction can finish before its
next interrupt check, bounded by the 16 MiB memory limit. This follows Wasmtime's
[interruption contract](https://docs.wasmtime.dev/examples-interrupting-wasm.html),
[epoch and bulk-operation limits](https://docs.wasmtime.dev/api/wasmtime/struct.Config.html#method.epoch_interruption),
and the [.NET store API](https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Store.html#Wasmtime_Store_SetEpochDeadline_System_UInt64_).

The binary transform follows the [Core specification's section and export
encoding](https://webassembly.github.io/spec/core/binary/modules.html), checked
against [WABT's independent binary reader](https://github.com/WebAssembly/wabt/blob/main/src/binary-reader.cc).
[Wasmtime's global API](https://bytecodealliance.github.io/wasmtime-dotnet/api/Wasmtime.Global.html)
provides numeric values after export; it is an implementation API, not the
format specification. The references agree on export indices and section
framing. Segment lifetime remains separate from memory bytes, as the
[Core execution specification](https://webassembly.github.io/spec/core/exec/modules.html)
describes; the current serializer therefore refuses that unsupported state.

## Mounting: a prepare/commit transaction

A `WorldAddonRow` in the world document's `addons` section is a data-only
descriptor (name, module path, content hash, fuel budget, enabled, requested
capabilities, a revision token). There is no separate runtime-facing
lifecycle surface: `world.row.set addons`/`world.row.remove addons`—the
ordinary document-mutation door (`WorldMutation.UpsertAddon`/`RemoveAddon`)
—is the ONLY way to mount, unmount, reload, enable, or disable a guest.
`Enabled` and the revision token express the whole lifecycle: a disabled row
is never compiled at all; any STRUCTURAL change to an already-mounted row
(content, revision, or `Requests`/`MemoryWatches` content) is a reload
(fresh guest instantiation, memory wiped)—resubmitting a byte-identical
row is a no-op for the runtime, and a sticky fault survives an unrelated
reprepare pass untouched, since runtime fault state is never part of the
reuse comparison.

`IWorldAddonHost.TryPrepare(current, candidate, out plan, out reason)` is
the whole runtime-delta computation—module resolve, hash pin, compile, ABI
admit, instantiate, and `puck_init` against the staged guest's own private
memory only—building a disposable, uncommitted `PreparedAddonInstall` that
reuses an unchanged row's guest (and its memory and fault state) untouched,
by explicit STRUCTURAL equality against the row it was last prepared under
(never reference identity: `Requests`/`MemoryWatches` are interface-typed
collections whose generated equality is reference equality, so the compare
walks their contents). A candidate whose channel declarations moved
invalidates every row's reuse eligibility at once and stages a fresh host
bound to a fresh resolver. `Commit` then publishes the whole plan by
reference adoption alone—no I/O, allocation, compilation, type dispatch
beyond its own downcast, or fallible call—and a separate `Finish` call,
made only after the caller's own document/journal publication is durable,
prints the deferred capability-disclosure/mount narration and disposes
every superseded guest (or the whole superseded host, when the channel
table moved). During replay (`WorldExtensions.IsReplaying`), `Finish` skips
those successful confirmations, so an offline re-drive narrates no second
boot; preparation refusals and receipt/trajectory differences still report.
`WorldServer.TryApplyMutation` calls the pair as the LAST
fallible gate for an `UpsertAddon`/`RemoveAddon` mutation (refusing by name
first when no addon host is attached at all), after every cheaper refusal;
`WorldAddonRuntime.TryCreate` calls it at boot (against no prior state, and
`WorldPostBuildWiring` turns a refusal into an ordinary boot refusal rather
than an unhandled exception), `WorldDocument.ApplyRebuild` calls it
unconditionally for `world.reset`/`.load`/`.reload` (a whole-document swap
can move any section, including the channel table), and `ApplyUndo` calls
it both as a throwaway per-entry probe (proving an intermediate journal
candidate COULD have mounted, disposed immediately either way) and once for
real, at the end, for the final restored document. An enabled row that
cannot prepare refuses the WHOLE mutation (or rebuild, or the whole world
installation at boot, or the whole undo)—the candidate discarded, the
live document byte-identical.

Requesting is not receiving: deny-by-default holds regardless of the
manifest, and authority materializes only where the row asked AND the grant
table holds (a hold outside the manifest mints no handle). `WorldAddonWire.cs`
is the fixed engine-owned mapping between engine capabilities and grant rules
and the ABI's mask bits and verdicts. The world document's channel table
decides which channels a guest drives, and `WorldAddonRuntime` folds the
validated acts into `PlayerIntent` channel vectors at the resolved ordinal.

The ordinary mutation gates authorize a row before addon preparation. Within preparation,
the source byte limit and document hash check precede native compilation; imports and declared
memory/table limits are checked before compiling the binary. Channel descriptors still require
the bounded, fuelled guest handshake, so channel-specific admission follows compilation.
The [scripting resource contract](../../docs/reference/scripting.md#loading-ticking-faulting)
owns source, cache and store limits; none of these grants a guest host authority.

## The read-back and the pump points

`world.addons` is the joined configuration/runtime read-back—one segment
per document row, in document order, never a mounted-guest-only
enumeration: a disabled row reads `DISABLED` with no cost figures; an
enabled row always has a committed runtime entry to join against (lifecycle
state, fuel budget, fuel consumed), because an enabled row that cannot
prepare refuses the whole mutation/rebuild/boot that would have installed
it. Guests are pumped only from inside
`WorldServer.Step`'s three pinned points
(`IWorldAddonHost.TickAddons`/`ApplyContributions`/`ResolveReads`), which is
what keeps guest driving reproducible under replay without recording it.

## The `addon.mutate` door

Mount preparation initializes the decoder's vocabulary and JSON contracts before any guest tick. Successful
decoding reads property names directly from the JSON document and shares unchanged immutable cells with the
previous decode in that guest's bounded reservation slot. The cached decode is only a storage hint: every act
still crosses admission, byte metering, the pending queue, validation, journaling, narration and echoes. Later
decodes never mutate cells retained by an earlier journal entry.

`AddonMutateRefusal` catalogs the six-stage dispatch gate's refusal reasons
for a guest-submitted `SubmitMutation` act (stale handle, unrequested
capability, masked mutation kind, budget exhaustion at three granularities,
pointer-safety failure, decode failure, and document-apply rejection).
`RefusalCatalog` (`Puck.World`) reflects over this assembly alongside
`Puck.State`, `Puck.State.Rules`, `Puck.World.Schema`, `Puck.World.Server`,
`Puck.World.Client`, and the composition root itself to build
`world.refusals`' catalog—a scan that drops this assembly silently loses the
whole `addon.mutate` door.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
