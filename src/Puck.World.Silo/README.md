# Puck.World.Silo — the second substrate driving the moved host engine

An Orleans silo whose grains carry activation lifecycle only. Game traffic
never rides the Orleans wire: cross-instance effects flow through the same
`WorldInstanceHost`/`WorldPeerCall` seam the desktop drives, on the one tick
thread `Puck.Launcher.HeadlessTickHostedService` pumps.

Run: `dotnet run --project src/Puck.World.Silo -c Release -- --silo <path>`,
where `<path>` names a `puck.silo.def.v1` document (`WorldSiloDefinition`,
`Puck.World.Schema`) — the `worlds[]` rows this silo may activate, its
declared door budget, checkpoint/journal/definition store target, state
directory, and clustering. The generated schema is
`Assets/puck.silo.def.v1.schema.json`.

Optional host services are supplied by an outer composition through
`WorldSiloApplication.RunAsync`. The silo exposes fixed-row Console sessions as
`IControlSessionHost`; it has no MCP project or package dependency. The host admits the validated issuer/subject through explicit OAuth admission and stamps the resulting Peer generation on commands. Replica disclosure authorizes text reads; writes require ordinary row grants. Remote sessions cannot use local administrative verbs. The CLI can
install [Puck.Mcp](../Puck.Mcp/README.md#host-extension) with
`puck mcp --silo <path> --http <remote.json>`. Row retirement closes all of its
attached Console sessions, including queued commands and waits.
Discovery runs on the ordinary command pump and filters registered help through
the same remote command guard. It requires Replica admission and advertises no
framebuffer on this headless host.

## Hosted test world

The silo selects Orleans membership through the composition root's registry.
`clustering.kind` is case-sensitive: this distribution registers `Localhost`.
The document loader validates against that same registry before host construction.
Other providers are refused until eligible world placement and exclusive ownership are implemented.
See [deployment](../../docs/ci.md) for the production endpoint and release process.

The schema selects installed extensions by `type` and opaque object `settings`.
The composition root's `WorldSiloExtensions` catalog uses `WorldExtensionRegistry`;
world documents cannot install code. For a local host:

```json
"store": { "type": "directory", "settings": { "path": "/world-store" } },
"lifecycle": {
  "shutdownSeconds": 120, "healthPort": 8081,
  "progressTimeoutSeconds": 30, "checkpointTimeoutSeconds": 180,
  "journalTimeoutSeconds": 30, "journalBacklogLimit": 1024
}
```

Persistence enters `WorldSiloHost` as an `ObjectStorageTarget`. An optional
`lifecycle.observer` selects an `IWorldHostRetirementObserver`, which supplies a
UTC deadline to the same `WorldSiloHost.DrainAsync` operation used by shutdown and
loopback-only `POST /drain`. Provider settings and metadata protocols belong to
the extension; the silo schema and lifecycle service do not interpret them.
The [Azure extension](../Puck.World.Azure/README.md#silo-hosting) owns its provider keys.

`GET /healthz` returns 200 only after all pinned worlds have activated, reconciled
their published definitions, and checkpointed. It also checks ongoing simulation
progress, checkpoint age, journal failures and backlog, and pending release saves.
`GET /livez` checks simulation progress independently of storage health, so a
storage outage does not trigger VM replacement. Both refuse during retirement.
Loopback-only `POST /reload` reconciles published content through the existing
reload submission, saves a checkpoint, and records the accepted content hash.
Unchanged releases preserve recovered state; failed saves can retry without
applying the same accepted rebuild twice. Readiness stays closed while a release
is uncommitted. Changing the world's network binding requires a fresh activation.
Activation takes its signing subject and listener from the published definition,
even when the recovered checkpoint contains the previous endpoint. Reconciliation
then updates the saved definition through ordinary reload. A live reload still
refuses a binding different from the one established by that activation.
`silo.status` reports readiness, retirement,
and selected provider keys without exposing settings. The drain settles accepted
reload submissions before freezing every row
at one pump boundary, closes ingress, waits for earlier checkpoint uploads and
journal appends, then saves final checkpoints. Failure is reported; deployment
does not replace a running process after a refused drain. A failed attempt may be
retried with a fresh deadline; once ingress has closed, worlds stay frozen while
saving is retried. Concurrent callers observe their own deadlines. Earlier failed
persistence tasks are observed and reported, and the final frozen checkpoint
supersedes them. A successful `POST /drain` completes its response and stops the
host. Physical retirement is a live host operation, outside gameplay and replay.

## Types

- `WorldSiloHost : IWorldAuthorityHost` — one boot-free `WorldInstanceHost`
  (`WorldEmbodiedSeats.None`, `admitsSpawn: false`), an activation mailbox
  drained on the tick thread, and per-row bookkeeping (federation identity,
  adjacency resolver, checkpoint outcomes). `ActivateAsync`/`DeactivateAsync`
  do their own store I/O off the tick thread and post a short mailbox action
  to touch the host's registry; `TryDescribeRow`/`DescribeRows` read the
  registry directly and must only ever be called from the tick thread itself
  (`SiloCommandModule`'s own handlers run there).
- `WorldSiloSimulation : IFixedStepSimulation` — `RatePerSecond` is
  `WorldSiloHost.MasterRateHz` (the fastest active, unpaused, nonzero-rate
  row; 0 while nothing is active). `Step` drains the activation mailbox,
  drains pending transfers, steps every admitted row, then notes the master
  step toward the checkpoint cadence.
- `WorldSiloActivations : BackgroundService` — activates every `pinned` row
  from `ExecuteAsync` (never `StartAsync`, which would deadlock waiting on a
  tick thread the headless host has not spawned yet). It waits for application startup, then requires each pinned world to activate and checkpoint; a refusal stops the host with a failing exit code.
- `IWorldGrain`/`WorldGrain` — the grain interface (`IGrainWithGuidCompoundKey`:
  owner oid + world id extension) and its thin adapter over `WorldSiloHost`.
  Activation allows three minutes for composition and checkpoint recovery;
  root and neighbour storage reads are awaited without a `Task.Run` wrapper.
- `WorldGrainStatus` — the Orleans-serializable read-back payload
  `IWorldGrain.StatusAsync` and `silo.grains` both answer with.
- `WorldNoAddonHost : IWorldAddonHost` — the inert host every row's replay
  tape carries for its own offline re-drive seam; the silo mounts no addon
  guests, so nothing ever calls it.
- `WorldSiloBareInputBindings`/`WorldSiloBarePrincipalResolver` — the minimal
  `IInputBindings`/`ICommandPrincipalResolver` pair `Puck.Launcher`'s
  simulation/router pairing rule needs; the silo embodies no local seats.
- `SiloCommandModule` — `silo.status`, `silo.grains`, `silo.publish <key>
  <path>`, `silo.activate <key>`, `silo.deactivate <key>`,
  `silo.checkpoint [<key>]`, `silo.use <key>`. `activate`/`deactivate`/
  `checkpoint` call the grain (`IGrainFactory`) and fire-and-forget: this
  module runs on the same thread the activation mailbox drains on, so
  blocking here for a grain turn that itself waits on that mailbox would
  deadlock. Read `silo.grains` for the outcome. `<key>` is
  `owner/{oid}/{world}` or the bare world id (`WorldSiloHost.TryResolveKey`).
- `SiloConsoleRouting` — one `TextCommandSession` per admitted row, created
  and retired in the same tick-thread mailbox action that admits/retires the
  row itself. Each session carries its own `WorldConsoleWaitGate` (`row.
  PublishTick` wired to it at registration) and its own slot, so
  `SiloConsoleAuthority : IWorldConsoleAuthority` resolves a dispatched
  command's row from `CommandContext.Slot` and `WorldSiloHost.GateFor`
  answers `world.wait`'s own gate per row (`IWorldWaitGateResolver`).
  Retirement disposes the session and refuses its queued host operations,
  even when a wait holds them. Stdin racing retirement gets a row refusal;
  work already injected into simulation retains its existing semantics.
  `silo.use <key>` sets which row an untagged line routes to; it never
  refuses admitted-row traffic addressed with an explicit `@<key>` tag.
- `SiloStdinRouter` — the silo's stdin reader (`AddLauncherHeadlessTerminal
  (readStandardInput: false)` disables the launcher's own single-session
  reader). `@<key> <line>` enqueues on that row's session; a `silo.*` line is
  always administrative regardless of `silo.use`; any other untagged line
  goes to the session `silo.use` last selected, refusing by name when none
  is selected or the addressed row is not currently admitted.
- `SiloConsoleTagging`/`SiloNarrationWriter` — every stdout/stderr line a
  silo run writes starts with `[<world-id>] ` or `[silo] `: verb output is
  tagged by the session's own `onResult` (`SiloConsoleTagging`); engine
  narration written straight to `Console.Out`/`Console.Error` is tagged by
  `SiloNarrationWriter`, installed once at startup via `Console.SetOut`/
  `SetError`, reading `WorldNarrationScope.Current` at write time. The
  desktop installs neither writer and tags nothing.

## Live journaling

Every mutation `WorldServer` applies fires `WorldServer.MutationJournalTap`
(wired once per row, right after activation's own tail replay so a replayed
entry is never re-appended as a duplicate). `WorldSiloHost.ScheduleJournalAppend`
re-encodes it (`WorldSubmissionCodec.TryEncodeCommittedMutation`) and schedules the
store write as a continuation of that row's own append chain
(`RowBookkeeping.JournalTail`) — never two appends racing concurrently for one
row, since `WorldAuthorityBlobStore.AppendJournalAsync` rewrites the whole
journal blob if-match per append and a race would drop one. Appends are
acknowledged **asynchronously with a bounded lag**, never a block on the tick
thread: `silo.grains`' `journalPending`/`journalOutcome` columns read the
count of outstanding appends and the most recently acknowledged one back.
Recovery uses `TryDecodeCommittedMutation`. These trusted-storage leaves admit
the world's own authored effects; live submissions still refuse a world actor.

## Document validation

`WorldSiloDataHookInstaller` calls the SAME
`Puck.World.Client.WorldSchemaVocabularyHooks.Install` the desktop client and
the test suite do, supplying only the two predicates that live in
`Puck.World.Server` (`WorldScreenMachineEngines.IsRegistered` and the
`WorldPostRenderExtensions` shipped-manifest check), so a document declaring a
`screens[]` machine, a `render.extensions[]` row, a `bindingBar.slotSet` id, or
an `icons.badges` source validates identically whether `silo.publish`/activation
loads it here or the desktop boots it. The silo
mounts no machine or addon host regardless (the checkpoint arm gate already
refuses a row that pumps one), so a validated key never actually runs here.

## Production deployment

The [Azure workflow](../../docs/ci.md#azure-production-deployment) deploys the primary
Puck world as a pinned row on a single regular VMSS worker. QUIC requires
UDP ingress and Linux `libmsquic`; Container Apps does not expose UDP. The silo
uses its own managed identity, private blob container, and persistent federation
key. The container test boots Puck, verifies its QUIC key and checkpoint, replaces
the container, and verifies recovery against the same store. The deployment
contract above owns resource names, grants, and operator setup.

Hosted neighbour definitions must already be composed and use canonical world
file names. `puck world prepare` prepares Puck and its references with the
engine composer. Checkpoint recovery preserves the running world's state and
embedded definition; CI does not erase checkpoints to apply authored changes.

## Not built here

Distributed clustering. Console verbs whose module takes a
process-wide `IServerLink`/similar singleton rather than resolving it
through the row `IWorldConsoleAuthority` returns (`WorldGrantCommandModule`,
`WorldGroupCommandModule`, `WorldLookCommandModule`,
`WorldRowCommandModule`, `WorldStateCommandModule`) are not registered here — registering them
unmodified would misattribute every row's mutation to whichever one
happened to be resolved into the shared singleton.
