# Puck.World.Silo

An Orleans silo whose grains carry activation lifecycle only. Game traffic
never rides the Orleans wire: cross-instance effects flow through the same
`WorldInstanceHost`/`WorldPeerCall` seam the desktop drives, on the one tick
thread `Puck.Launcher.HeadlessTickHostedService` pumps.

Run: `dotnet run --project src/Puck.World.Silo -c Release -- --silo <path>`,
which starts `dotnet Puck.World.Silo.dll` rather than the apphost, so one
firewall decision for the shared `dotnet` host covers the silo's listeners in
every worktree. Here `<path>` names a `puck.silo.configuration.v1` document (`WorldSiloDefinition`,
`Puck.World.Schema`)—the `worlds[]` rows this silo may activate, its
declared door budget, checkpoint/journal/definition store target, state
directory, and clustering. The generated schema is
`Assets/puck.silo.configuration.v1.schema.json`.

Optional capabilities are installed [extensions](../../docs/reference/extensions.md),
discovered from `--extensions-dir`, or from `extensions` under the working
directory and beside the silo when it is absent; the silo's one built-in
contributes `directory` storage. An installation it cannot use, or a conflict
between extensions, refuses startup by name. The silo exposes fixed-row Console
sessions as `IControlSessionHost`; it has no MCP project or package dependency.
`--mcp <remote.json>` names the control configuration for
the installed hosted control, [Puck.Mcp](../Puck.Mcp/README.md#host-extension),
and is refused when none is installed; `puck mcp --silo <path> --http <remote.json>`
runs the same composition. The host admits the validated issuer/subject through explicit OAuth admission and stamps the resulting Peer generation on commands. Replica disclosure authorizes text reads; writes require ordinary row grants. Remote sessions cannot use local administrative verbs. Row retirement closes all of its
attached Console sessions, including queued commands and waits.
Discovery runs on the ordinary command pump and filters registered help through
the same remote command guard. It requires Replica admission and advertises no
framebuffer on this headless host.

Activation explicitly leaves the Orleans scheduler before constructing
the authority and awaits storage without capturing it. `silo.activate.timing`
reports storage, checkpoint decode, authority construction/restore, and addon wiring
separately, so cold-process work can be distinguished from recovery work. The storage
phase includes root-qualified definition admission, rather than measuring I/O alone.

An isolated startup `Participant check thread has not completed` warning with a
duration close to host uptime can come from the Orleans 10.4.2 watchdog's startup
race: `Watchdog.Start` starts the platform thread before initializing the component
timestamp that `CheckRuntimeHealth` reads. See the
[upstream implementation](https://github.com/dotnet/orleans/blob/v10.4.2/src/Orleans.Runtime/Silo/Watchdog.cs).
This is distinct from a recurring health warning during operation, which requires
investigation. Keep health monitoring enabled; Puck configures no watchdog interval
override.

## Hosted test world

The silo selects Orleans membership through the composition root's registry.
`clustering.kind` is case-sensitive: this distribution registers `Localhost`.
The document loader validates against that same registry before host construction.
Other providers are refused until eligible world placement and exclusive ownership are implemented.
See [deployment](../../docs/development/ci.md) for the production endpoint and release process.

The official host release (`puck world host build`) publishes `Puck.World.Azure`,
both Gaming Brick forges, `Puck.Mcp`, and `Puck.Mcp.Azure` into separate named
directories under the installed version's `extensions`, including each extension's
dependencies and dependency manifest. The CLI that hosts the silo (`puck world host
serve`, with or without its MCP front) discovers that directory beside its own
assemblies. Merely placing assemblies
beside the CLI does not install them into the silo. Registering a provider does
not enable it: the silo and MCP documents still select their configured behavior.
Extensions using .NET hosted services retain their lifecycle callbacks and
background-task supervision. A fault stops the worker and returns a failure exit
code, including when the world itself was otherwise healthy. A listener the silo
cannot bind — a row's QUIC door, the lifecycle health port, or the hosted MCP
endpoint — exits 2 with one `[silo.host: unsupported: <transport> listener
<endpoint> unavailable: <reason>]` line, the shape World reports for an
environment that cannot run its boot.
Discovered providers remain loaded for the process lifetime; changing their code
requires restarting the worker with the selected host release.

The schema selects installed extensions' contributions by `type` and opaque object
`settings`; `world.extensions.catalog` lists what the silo composed, and world
documents cannot install code. For a local host:

```json
"store": { "type": "directory", "settings": { "path": "/world-store" } },
"lifecycle": {
  "shutdownSeconds": 120, "healthPort": 8081, "healthAddress": "127.0.0.1",
  "progressTimeoutSeconds": 30, "checkpointTimeoutSeconds": 180,
  "journalTimeoutSeconds": 30, "journalBacklogLimit": 1024
}
```

The health listener binds `healthAddress`, an IP literal that defaults to
`127.0.0.1`, so a local silo listens on no network interface and raises no
firewall prompt. A deployment whose load balancer probes the health port names
its interface instead: the Azure deployment writes `0.0.0.0` into the silo
document it composes ([deployment](../../docs/development/ci.md#azure-production-deployment)).
Orleans clustering stays on loopback. A row's QUIC door listens on its world's
`host.listen`, with the
[msquic limitation on Windows](../Puck.World/README.md#usage) that the
address does not narrow the sockets it opens.

A hosted world document arrives from the store as bytes, so it has no file to
sit beside. The silo document's optional `documentDirectory` names the
directory its relative paths resolve against instead: a machine's cartridge,
an asset row's source, a pipeline. It is the directory `puck world prepare`
wrote the composed documents into, inside the authored layout the host release
ships beside them, so a hosting machine's silo documents name `worlds`, resolved
against the installed version directory its unit runs in, and a prepared
document's `../cartridges/...` reads the version's `cartridges`. The first activation, a recovery from a checkpoint and a
reload of published content all read the same directory. Without
`documentDirectory`, a relative path in a hosted document is refused by name.
Checkpoint decoding receives that directory too, before it validates embedded
definitions; providing it only to the subsequent server constructor is too late.

A `worlds[]` row may add `extensions`, the path of its own host-approved
extension configuration: the same `puck.world.extensions.v1` document a local
World reads through `--extensions-config-file`, attached through the same
`WorldConfiguredExtensions.Attach`. The row's configured operations, connections,
observations, embeddings, and
[participants](../Puck.World.Server/ExtensionConfiguration.md#run-an-agent-participant)
select installed providers by type, run once release admission is open, are
pumped after the rows step, and stop before the row retires. The silo reads the
file at activation, and an unreadable file, a mismatched `world`, or an unknown
or ambiguous provider type refuses the activation by name. No two rows may share
a file. `world.extensions` reads back the addressed row's runtime.

Persistence enters `WorldSiloHost` as an `ObjectStorageTarget`. The built-in
`directory` store admits `WorldAuthorityBlobStore.MaximumBlobBytes` per blob, the
checkpoint codec's own bound, and the authority store refuses a smaller directory
target when it is built rather than at its first checkpoint. A pinned row that
cannot activate and checkpoint stops the silo: the drain runs and reports
`[silo.drain: …]`, the host reports `[silo.host: stopped by a failed service (…)]`,
and the process exits 1. An optional
`lifecycle.observer` selects an `IWorldHostRetirementObserver`, which supplies a
UTC deadline to the same `WorldSiloHost.DrainAsync` operation used by shutdown and
loopback-only `POST /drain`. Provider settings and metadata protocols belong to
the extension; the silo schema and lifecycle service do not interpret them.
Every host deadline — each lifecycle route's, a retirement's, shutdown's, storage
calls', and the peer network's — and the health age windows read one clock,
`WorldSiloHost.Clock`, which the host's constructor accepts and which defaults to
system time. `WorldSiloDeadlineLawTests` drives each on a virtual clock.
The [Azure extension](../Puck.World.Azure/README.md#silo-hosting) owns its provider keys.

`GET /healthz` returns 200 only after all pinned worlds have activated and
checkpointed. Activation itself lands that checkpoint: a new row's first one, or
one covering a recovered journal tail, so a successful activation always holds a
checkpoint that covers everything it restored. Unmanaged startup also reconciles published definitions through
ordinary reload. Managed startup preserves the recovered state and waits for
release admission. Health also checks ongoing simulation
progress, checkpoint coverage, journal failures and backlog, retention, and pending release saves.
Checkpoint age counts only from the moment a row holds state its landed
checkpoint does not cover (it stepped past the checkpoint's tick, or journaled an
entry since its capture), so a frozen or held row stays healthy however old its
checkpoint is (`WorldSiloCheckpointHealthLawTests`). A row whose checkpoints the
store refuses outright reports the refusal at once, and three landed checkpoints in
a row whose retirement of superseded objects failed report the retention failure.
`GET /livez` checks simulation progress independently of storage health, so a
storage outage does not trigger VM replacement. Both refuse during retirement.
`release.closedGroupRewind` enforces a fixed inventory of pinned worlds under the
group owner. Internal transfers remain available; remote transfers, outbound
federation connections, and issued federation claims are refused. The silo's issuer
still vouches for players, and its rows trust only the issuer's root while the
group is closed.
The first coherent capture records a durable boundary pin in every root. Later
activations must enforce that same boundary. `ExportReleaseFixtureAsync` then
retains a rewindable point with a capture time and complete receipt provenance.
`ApplyRestorePointAsync` checks the captured group and machine identity before
applying an explicit restore during private activation.
Repeating a completed admission publication rechecks the authority fences and
conditionally updates the group. An already completed publication under those
same fences reuses its registered routes and listeners without another pump callback.

Managed documents may add `release` with a deployment `group`, its private-store
`owner`, and an exact `expectedRelease`. Such a host restores candidates behind
the durable group barrier: route registration, listeners, console sessions,
simulation steps, transfers, and federation effects remain closed until the
explicit all-row publication succeeds. Publication checks every row's current
authority fence; a missing row or stale fence refuses the whole group. A
committed target can reopen after restart under fresh fences while retaining
the previous release and its rollback window. Loopback-only `GET /private-healthz`
reports candidate persistence health without opening admission. A managed
candidate in an uncommitted operation remains available for private
verification; it is not ready for public traffic.
Managed workers also expose loopback-only release control. `GET /release/status`
reports the configured group, release, pending operation, phase, and admission.
`POST /release/drain/<operation-id>` freezes the source and returns its protected
root pins. `GET /release/fences/<operation-id>` reports the privately verified
candidate's actual row fences. `POST /release/publish/<operation-id>` publishes
the committed target, or completes restored-source admission. Every operation
checks the current durable phase and operation identifier; an external caller
or unmanaged worker receives 404. Drain retains the frozen process for retries.
`POST /release/fixture/<request-id>` exports the admitted active release when no
unfinished operation remains. A committed, admitted operation retained for rollback
permits export; prepare, recovery, and commit before admission require resume first.
It captures every pinned row in one mailbox action and inserts a root read into
each row's publication queue after preceding writes and before later mutations.
That root selects the exact receipt chain and index; copying their immutable
objects does not hold the publication queue. Export verifies ownership and uploads
the fixture inventory after all checkpoint and receipt bytes.
It preserves source admission and authority roots. Repeating a completed request
returns the original capture; a partial upload cannot be consumed as a fixture.
The response contains only the retained inventory identity, not checkpoint payloads
or signing keys. `WorldReleaseCutoverLawTests` verifies the shared capture boundary
and continued source gameplay, including a delayed root read and cancellation.
A failed or canceled export read does not poison the serving publication queue.
Managed source recovery first restores the operation's protected roots into an
empty private host, then activates under fresh fences. The durable
`RecoverActivate` phase lets a restarted coordinator continue activation without
restoring those roots again. Capture and restore check the current operation,
source identity, and declared world inventory. The shared runtime adapter leaves
pumping to its host composition; it never advances worlds from storage callbacks.

The official host release retains the authored asset layout in its `worlds`
directory, so relative cartridge and other content paths resolve beside the
composed worlds. It carries only the package's files and the composed documents. The packaged qualification runner and its fixture format
are described in the [CLI guide](../Puck.Cli/README.md).
`GET /livez/azure` reports the same liveness as Azure's v2 Application Health
JSON contract, returning HTTP 200 with `Healthy` or `Unhealthy`. The generic
`/livez` retains its HTTP 200/503 contract.
Loopback-only `POST /reload` reconciles published content through the existing
reload submission, saves a checkpoint, and records the accepted content hash.
This administrative content reload is separate from managed release cutover.
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

Row deactivation also freezes its server before the final capture. This is not
`world.rate pause`: paused worlds keep draining administrative edits; retiring
activations drain their accepted edits once, then stop simulation and reject new
authority operations. A failed save retains the frozen row for a retry, not for
resumed play. Buffered intents are saved without inventing an extra tick.

Activation acquires its storage fence before reading one coherent recovery root.
A new world writes its initial checkpoint before opening socket or console
admission. Each activation serializes checkpoints, journal appends and hosted
definition publication in one queue; a checkpoint takes its queue position at
capture time and covers only the preceding journal prefix. Async status updates
name the exact activation, not merely a reusable world name. Final saves precede
fence release. A stale or uncertain publication cannot adopt a replacement
activation's fence and requires recovery.

## Types

- `WorldSiloHost : IWorldAuthorityHost`—one boot-free `WorldInstanceHost`
  (`WorldEmbodiedSeats.None`, `admitsSpawn: false`), an activation mailbox
  drained on the tick thread, and per-row bookkeeping (federation identity,
  adjacency resolver, checkpoint outcomes). `ActivateAsync`/`DeactivateAsync`
  do their own store I/O off the tick thread and post a short mailbox action
  to touch the host's registry; `TryDescribeRow`/`DescribeRows` read the
  registry directly and must only ever be called from the tick thread itself
  (`SiloCommandModule`'s own handlers run there).
- `WorldSiloSimulation : IFixedStepSimulation`—`RatePerSecond` is
  `WorldSiloHost.MasterRateHz` (the fastest active, unpaused, nonzero-rate
  row; 0 while nothing is active). `Step` drains the activation mailbox,
  drains pending transfers, steps every admitted row, then notes the master
  step toward the checkpoint cadence.
- `WorldSiloActivations : BackgroundService`—activates every `pinned` row
  from `ExecuteAsync` (never `StartAsync`, which would deadlock waiting on a
  tick thread the headless host has not spawned yet). It waits for application startup, then requires each pinned world to activate and checkpoint; a refusal stops the host with a failing exit code.
- `IWorldGrain`/`WorldGrain`—the grain interface (`IGrainWithGuidCompoundKey`:
  owner oid + world id extension) and its thin adapter over `WorldSiloHost`.
  Root and neighbour storage reads are awaited without a `Task.Run` wrapper.
- `WorldGrainStatus`—the Orleans-serializable read-back payload
  `IWorldGrain.StatusAsync` and `silo.grains` both answer with.
- `WorldAddonRuntime` (`Puck.World.Addons`)—the same mounting transaction the
  World host uses, owned by each activation and used by its replay factory.
  Attachment prepares and restores the checkpoint's guest image once before journal
  replay or admission; row retirement disposes the runtime. A failed attachment
  reports a named refusal and disposes the activation's machines and adjacencies.
- `WorldSiloBareInputBindings`/`WorldSiloBarePrincipalResolver`—the minimal
  `IInputBindings`/`IPrincipalResolver` pair `Puck.Launcher`'s
  simulation/router pairing rule needs; the silo embodies no local seats.
- `SiloCommandModule`—`silo.status`, `silo.grains`, `silo.publish <key>
  <path>`, `silo.activate <key>`, `silo.deactivate <key>`,
  `silo.checkpoint [<key>]`, `silo.use <key>`. `activate`/`deactivate`/
  `checkpoint` call the grain (`IGrainFactory`) and fire-and-forget: this
  module runs on the same thread the activation mailbox drains on, so
  blocking here for a grain turn that itself waits on that mailbox would
  deadlock. Read `silo.grains` for the outcome. `<key>` is
  `owner/{oid}/{world}` or the bare world id (`WorldSiloHost.TryResolveKey`).
  `silo.publish` uses the same completion reporter and returns `requested` before
  its publication enters the mailbox; stderr reports `published from` or the
  failure detail when storage completes. Wait for that completion before activating
  a newly published row. It publishes the document undrawn, as a release does
  (`WorldFileOrigin.TryReadPublishable`): each instance draws its own cells
  when it admits the bytes, and a release bootstrap of the same composed document
  finds the published bytes equal.
  The standalone silo has no source composer: prepare `.puck` sources with
  `puck world prepare worlds/debug --output <hosted-worlds>`, retain the authored
  assets beside those outputs, and publish the resulting `.world.json` paths.
- `SiloConsoleRouting`—one `TextCommandSession` per admitted row, created
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
- `SiloStdinRouter`—the silo's stdin reader (`AddLauncherHeadlessTerminal
  (readStandardInput: false)` disables the launcher's own single-session
  reader). `@<key> <line>` enqueues on that row's session; a `silo.*` line is
  always administrative regardless of `silo.use`; any other untagged line
  goes to the session `silo.use` last selected, refusing by name when none
  is selected or the addressed row is not currently admitted.
- `SiloConsoleTagging`/`SiloNarrationWriter`—every stdout/stderr line a
  silo run writes starts with `[<world-id>] ` or `[silo] `: verb output is
  tagged by the session's own `onResult` (`SiloConsoleTagging`); engine
  narration written straight to `Console.Out`/`Console.Error` is tagged by
  `SiloNarrationWriter`, installed once at startup via `Console.SetOut`/
  `SetError`, reading `WorldNarrationScope.Current` at write time. The
  desktop installs neither writer and tags nothing. Host log lines, at every
  level, go to stderr in both hosts, so stdout carries only answers.

## Live journaling

An ordinary mutation acknowledgement means the authority applied the edit to
the live world. A crash before the journal root is published can lose an
acknowledged edit. Crossing acknowledgements require a durable write, but the
step thread never waits for it: a crossing record joins the row queue, the row's
authority parks the step that wrote it, and the step resumes at the tick
boundary after the store answers (`WorldServer.RecordCrossingHeld`). A
successful checkpoint publishes a durable image through the same root; none is
captured while a crossing record is in flight, and retirement freezes its rows
and waits for their records' answers before the final capture.

`WorldPreparedMutation` asks the hosted journal to admit each external edit
before installing it. `WorldSiloHost.PrepareJournalMutation` bounds outstanding
entries by `WorldSiloLifecycle.JournalBacklogLimit` and outstanding payload
bytes by `WorldSiloHost.JournalBacklogBytes`. A full row returns a retryable
refusal before applying another edit. A failed or uncertain publication closes
journal admission for that activation until recovery. Guest and world-authored
effects travel in the checkpoint's tick image, so storage timing cannot refuse
a simulation-authored mutation.

The silo flushes each row's tick batch into one immutable, hash-linked journal
segment, then publishes its head with a fenced root compare-and-swap. A crossing
or checkpoint flushes any earlier edits before taking its place in the same row
queue. Recovery walks only the suffix above checkpoint coverage and replays
entries in publication order. Each privately drawn edit carries its recorded
words and starting positions. A new activation advances the generation so an
uncommitted draw seen before restart cannot predict the next draw.

A landed checkpoint retains its own image, the preceding image, and any
uncovered journal segments. It retires superseded payloads through the object
store and retries cleanup at the next checkpoint if deletion fails. Explicit
release recovery points keep archive copies under their operation IDs, so an
ordinary checkpoint can retire the live copies without affecting a protected
restore. The segmented journal uses a new authority root format. The first
release using it needs a fresh development authority store.

A cadence request stays pending per row until that row's upload
succeeds. Other rows can checkpoint on schedule while a row is deferred. A failed
capture or upload retries with exponential backoff on engine time, from one second
to 64, and a row that has neither stepped nor journaled since the failed capture
uploads the same bytes again instead of capturing and encoding a new image. A
refusal no retry can change (the store holds different bytes at the address, the
capture contradicts the authoritative root, an operation conflict) stops the
cadence for that row until an explicit `silo.checkpoint` lands; `silo.grains`
shows it as `cadenceRefusal`. A row whose journal tail above its landed checkpoint
passes 4096 entries or 32 MiB checkpoints early, which also bounds what a recovery
replays.

In a hosted run, use `silo.grains` to watch `journalPending` and
`journalPendingBytes` level off at their limits under a stalled store.
`journalSegments` should rise once per nonempty tick batch and fall when a
covering checkpoint lands. Divide changes in `journalBytesWritten` by
`journalMutationsWritten` to measure segment bytes per mutation as the
tail grows. `journalTail` counts the entries a recovery would replay now.
`retiredAuthorityObjects` should increase as covering checkpoints
retire journal and checkpoint objects; `retention` and `retentionFailures` name a
failed retirement, which the runtime identity's storage role must allow (blob delete).

## Document validation

`WorldSiloDataHookInstaller` installs the shared schema hooks for post-process
package, probe, binding, input, and command vocabularies. Machine registration is separate:
dynamic extensions register into the silo's own immutable `WorldMachineCatalog`.
Decoding a world without a catalog proves its document-local facts; provider
admission requires the deployment's explicit catalog. Loading the silo assembly
cannot replace another host's machine vocabulary.

A parsed machine declaration does not establish that its provider is installed
or its runtime state can be checkpointed. Qualification must exercise the
distribution's supplied machine catalog. Addon checkpoints preserve guest and
cross-tick host state through the ordinary authority checkpoint section; unsupported
guest resources refuse mounting by name, before they can disable durable capture.
Each captured guest retains its pinned module source. A deployment that changes
the file therefore restores old guest memory under its old module first, then
ordinary published-content reconciliation replaces changed guests with fresh
ones. Unchanged guests and the surrounding recovered world state survive. Managed
candidates keep the recovered module until explicit release reconciliation.
The [addon runtime](../Puck.World.Addons/README.md#durable-guest-state)
owns the captured inventory and current resource limitations.

## Production deployment

The [Azure workflow](../../docs/development/ci.md#azure-production-deployment) deploys the primary
world, the debug world (`debug`), as a pinned row on a single regular VMSS worker. QUIC requires
UDP ingress and Linux `libmsquic`; Container Apps does not expose UDP. The silo
uses its own managed identity, private blob container, and persistent federation
key. The host test (`puck azure test-world-host`) boots the primary world from an installed host
release, verifies its QUIC key and checkpoint, boots it again through the MCP shape, and verifies
recovery against the same store. The deployment
contract above owns resource names, grants, and operator setup.

Hosted neighbour definitions must already be composed and use canonical world
file names. `puck world prepare` prepares the primary world and its references with the
engine composer, rebasing nested machine asset paths to the shared worlds
directory. A colocated row with neither `host.authority` nor `host.listen` signs
as its stable instance name, matching `WorldServer.AuthorityIdentity`, and opens
no socket. A listening row must advertise `host.authority`. The official package
binds the primary endpoint while retaining local-only neighbours.
Checkpoint recovery preserves the running world's state and
embedded definition; CI does not erase checkpoints to apply authored changes.

## Not built here

Distributed clustering. The silo registers `SiloCommandModule` and, from
`Puck.World.Console`, `WorldWaitCommandModule`, `WorldNetworkCommandModule`,
`WorldStateCommandModule`, `WorldExtensionsCommandModule` and
`WorldMachineCommandModule`. The other console modules (`WorldGrantCommandModule`,
`WorldGroupCommandModule`, `WorldLookCommandModule`, `WorldRowCommandModule`, the
replay, history and read-back modules, and `world.counters`) are not registered
here. The grant, group, look and row modules resolve their row through
`IWorldConsoleAuthority`, which `SiloConsoleAuthority` implements, and none holds
a process-wide `IServerLink`, so their absence is not a missing seam.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
