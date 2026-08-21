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

## Mounting: a prepare/commit transaction

A `WorldAddonRow` in the world document's `addons` section is a data-only
descriptor (name, module path, content hash, fuel budget, enabled, requested
capabilities, a revision token). There is no separate runtime-facing
lifecycle surface: `world.row.set addons`/`world.row.remove addons` — the
ordinary document-mutation door (`WorldMutation.UpsertAddon`/`RemoveAddon`)
— is the ONLY way to mount, unmount, reload, enable, or disable a guest.
`Enabled` and the revision token express the whole lifecycle: a disabled row
is never compiled at all; any STRUCTURAL change to an already-mounted row
(content, revision, or `Requests`/`MemoryWatches` content) is a reload
(fresh guest instantiation, memory wiped) — resubmitting a byte-identical
row is a no-op for the runtime, and a sticky fault survives an unrelated
reprepare pass untouched, since runtime fault state is never part of the
reuse comparison.

`IWorldAddonHost.TryPrepare(current, candidate, out plan, out reason)` is
the whole runtime-delta computation — module resolve, hash pin, compile, ABI
admit, instantiate, and `puck_init` against the staged guest's own private
memory only — building a disposable, uncommitted `PreparedAddonInstall` that
reuses an unchanged row's guest (and its memory and fault state) untouched,
by explicit STRUCTURAL equality against the row it was last prepared under
(never reference identity: `Requests`/`MemoryWatches` are interface-typed
collections whose generated equality is reference equality, so the compare
walks their contents). A candidate whose channel declarations moved
invalidates every row's reuse eligibility at once and stages a fresh host
bound to a fresh resolver. `Commit` then publishes the whole plan by
reference adoption alone — no I/O, allocation, compilation, type dispatch
beyond its own downcast, or fallible call — and a separate `Finish` call,
made only after the caller's own document/journal publication is durable,
prints the deferred capability-disclosure/mount narration and disposes
every superseded guest (or the whole superseded host, when the channel
table moved). `WorldServer.TryApplyMutation` calls the pair as the LAST
fallible gate for an `UpsertAddon`/`RemoveAddon` mutation (refusing by name
first when no addon host is attached at all), after every cheaper refusal;
`WorldAddonRuntime.TryCreate` calls it at boot (against no prior state, and
`WorldPostBuildWiring` turns a refusal into an ordinary boot refusal rather
than an unhandled exception), `WorldServer.ApplyRebuild` calls it
unconditionally for `world.reset`/`.load`/`.reload` (a whole-document swap
can move any section, including the channel table), and `ApplyUndo` calls
it both as a throwaway per-entry probe (proving an intermediate journal
candidate COULD have mounted, disposed immediately either way) and once for
real, at the end, for the final restored document. An enabled row that
cannot prepare refuses the WHOLE mutation (or rebuild, or the whole world
installation at boot, or the whole undo) — the candidate discarded, the
live document byte-identical.

Requesting is not receiving: deny-by-default holds regardless of the
manifest, and authority materializes only where the row asked AND the grant
table holds (a hold outside the manifest mints no handle). `WorldAddonWire.cs`
is the fixed engine-owned mapping from a guest's validated acts onto
`PlayerIntent` values.

## The read-back and the pump points

`world.addons` is the joined configuration/runtime read-back — one segment
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

`AddonMutateRefusal` catalogs the six-stage dispatch gate's refusal reasons
for a guest-submitted `SubmitMutation` act (stale handle, unrequested
capability, masked mutation kind, budget exhaustion at three granularities,
pointer-safety failure, decode failure, and document-apply rejection).
`RefusalCatalog` (`Puck.World`) reflects over this assembly alongside
`Puck.World.Schema`, `Puck.World.Server`, and the composition root itself to
build `world.refusals`' catalog — a scan that drops this assembly silently
loses the whole `addon.mutate` door.
