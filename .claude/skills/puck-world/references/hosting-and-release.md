# Hosting, extensions, and release

How a World host composes extensions, runs agent participants and the MCP
attachment, and how the production silo and its release coordinator are
verified. The base world projects never reference the agent projects or any
cloud SDK; everything here is an extension or a host concern.

## Contents

- Run an agent participant
- Agent participants and the MCP attachment
- Host provider boundary
- Production silo verification

Silo administrative verbs run on the tick thread, which also drains the host's
mailbox. `SiloCommandModule` sends publication and grain lifecycle tasks through
its shared completion reporter and immediately answers `requested`; never wait
synchronously for those tasks in a command handler. For a new row, wait for
`silo.publish`'s asynchronous `published from` line before `silo.activate`, then
its `completed` line before reading `silo.status` and `silo.grains`.

## Run an agent participant

A model-driven participant (a guide, a companion) on a body is configuration,
not code. `Puck.World.AgentHarness` contributes the `agent.harness`
participant type and `Puck.World.AgentHarness.Azure` the `azure.openai`
`ChatClientProvider` (an Azure OpenAI or Foundry chat deployment). Neither base
host references them: publish both beside each host that runs one
(`dotnet publish src/Puck.World.AgentHarness -c Release -nodeReuse:false -o extensions/Puck.World.AgentHarness`,
the same for `.Azure`), and `world.extensions.catalog` then lists them.

The participant is a `participants` row of a `puck.world.extensions.v1`
document (`WorldExtensionConfiguration`):

```json
{
  "schema": "puck.world.extensions.v1",
  "world": "<the world's documentId>",
  "lineage": "<a new UUID, kept across restarts>",
  "participants": [
    {
      "name": "guide",
      "type": "agent.harness",
      "principal": "addon:guide",
      "body": 5,
      "settings": {
        "objective": "Help visitors reach the observatory.",
        "provider": "azure.openai",
        "providerSettings": { "endpoint": "https://<resource>.openai.azure.com/", "deployment": "<chat-deployment>" },
        "approval": "refuse"
      }
    }
  ]
}
```

- Locally, a World reads it through `--extensions-config-file <file>`; a silo
  row names the same file in its `extensions` member, and both hosts attach it
  through `WorldConfiguredExtensions.Attach`, so one file behaves the same in
  both. Each silo row needs its own file and lineage.
- `body` is the 0-based body index. `principal` is a seat, addon or peer, never
  `console`. The configuration grants nothing: the world itself grants the
  principal `Observe` and `Drive` over that body (document `grants` or
  `world.grant`).
- `approval` is the consent, since no operator is in the loop: `refuse` (the
  default) offers the model only observing tools; `allow` offers the mutating
  ones (`puck_move`, `puck_press_channel`, `puck_stop`) without a prompt. Grants
  still decide every action.
- `azure.openai` signs in with `DefaultAzureCredential` (a managed identity on a
  host, the developer's own Azure sign-in on a workstation) and refuses a key
  member such as `apiKey` by name. Its `providerSettings` are `endpoint`
  (https), `deployment`, and optionally `tenantId` and
  `managedIdentityClientId`; the identity needs a data-plane role such as
  Cognitive Services OpenAI User.
- An unknown type, provider or setting, a blank objective, or a console
  principal refuses the whole configuration by name; at most 16 participants.
- `world.extensions` echoes one status line per participant: type, principal,
  body, provider, state, completed turns and the last failure.
- Verify with `WorldAgentParticipantLawTests` and `AzureOpenAiChatClientLawTests`
  (`tests/Puck.World.Agents.Tests`), and `WorldSiloExtensionLawTests` and
  `ExtensionModelLawTests` (`tests/Puck.World.Silo.Tests`); no test reaches a
  live model. The participant section of
  `src/Puck.World.Server/ExtensionConfiguration.md` and the two projects'
  READMEs own the settings tables.

## Agent participants and the MCP attachment

A host runs an agent as an extension-configuration `participants` row (`WorldParticipantType`, in Protocol), which `WorldConfiguredExtensions` creates, pumps at every closed boundary, and disposes; the participant acts only through its principal's grants and refuses the console principal. The Operator MCP adapter lives in
`Puck.Mcp`, referenced by CLI for local stdio, never by base World or the silo. Remote MCP is the installed
`Puck.Mcp` extension's `HostedControl`, started by the silo's `--mcp <remote.json>` (which is what
`puck mcp --silo <silo.json> --http <remote.json>` runs); the silo exposes only Hosting's neutral
`IControlSessionHost`. A `services` member in that configuration selects exactly one installed
`McpServicesProvider` (`Puck.Mcp.Azure` ships the `azure` one) and is refused by name with none or two. World installs the `Puck.Hosting` local control endpoint only
when the host Console issues `world.control start`, or when the run was launched with `--control` (the same start at
boot, announced on stderr, wired through `WorldBootInputs.Control`); `stop` and `status` manage its live lifetime. Attach with
`puck mcp --profile operator --attach <printed-file>` on Windows or Linux x64. One adapter serves every agent of a
harness: `puck_worlds` lists the running Worlds, and the `world` argument of `puck_exec`, `puck_capture_frame` and
`puck_state_vector_write` (an attachment id or process id) reaches exactly that World or is refused; an unnamed call
runs on the adapter's current World and the result's `world` member names it, so name your own World when several
run. `puck_world_start` boots a World of the agent's own from a checkout of this repository (`OperatorWorlds`:
the build of the checkout's current sources from the [World build store](../../../../docs/reference/cli.md#where-the-world-artifact-is-built),
built first when the store lacks that source state, so never older than its inputs, one build at a time machine-wide under the `build` lease, and with SPIR-V when `backend` is `vulkan`; `--devices none`, its own state
directory, `--control`, the GPU lease held for a windowed one, taken after the build) and `puck_world_stop`
ends only a World the adapter started; the console verb `host.log` reads any World's `HostLog` ring of its stdout and
stderr lines (`Puck.Hosting`, teed from `Console` in `Program.cs`, refusals appended by `BufferedConsoleOutput`,
ticks anchored in `WorldPostBuildWiring`). The file protects a mutual-authenticated
loopback capability for the current OS user, including its other processes/elevation levels. Each connection has
one bounded, dedicated Console session. Exec preserves ordinary result uncertainty; capture uses the same
session's `InvokeAsync` barrier and the exact render request's completion, with off-pump waiting and temporary
artifact cleanup. Host deadlines remain enforced even when an injected session ignores cancellation; invalid
host results are `unknown`. A console answer is delivered whole up to `ControlLimits.OutputCharacters` (1 Mi UTF-16
code units) and cut past it to a head ending in a marker, flagged `truncated`, under the command's own status
(`ControlResponse.Bounded`); a long read is never an unknown outcome. MCP results carry schema-backed metadata in `structuredContent` with a short status in `content[0].text` (omitting null and false fields with defaults); invalid tool arguments return tool errors. The adapter bounds UTF-8 input and pending replies and
closes both stdio streams on shutdown; malformed input or stalled output exits with failure.
Cancellation/EOF close ingress without stopping World or its recordings. The local adapter outlives Worlds: each
call attaches on demand (pinned `--attach` file, or the newest answering World), a call with nothing to attach is
`refused` before dispatch, and a closed attachment (timeout, cancellation, host stop or exit) is reported `unknown`,
never replayed; the next call attaches anew. Calls are serialized through that attachment, and an idle attachment
whose host hung up is dropped before dispatch (`LocalControlClient.CloseIfHostGone`). Remote MCP is an in-process silo
extension, never a gateway to a local Console capability. Reuse the existing `user_impersonation` scope.
The host matches validated issuer/subject to explicit OAuth admission and stamps a generation-bound Peer.
Replica disclosure authorizes text reads; ordinary World grants authorize state-cell writes. The remote
command allowlist must remain fail-closed for local admin verbs. Current target/owner binding is fixed;
distributed placement and portable handles are not implemented. Both transports select the MCP revision
`RemoteMcpServer` pins as `ProtocolVersion`; local stdio also answers earlier revisions' initialize handshake.
Remote HTTP uses caller-bound application handles, four attachments and four concurrent HTTP requests,
with two of each per subject and no waiting queue. Discovery uses admitted registry help, hides headless
capture and discloses only granted observation names. Downstream user-interaction challenges return
bounded claims in an HTTP 401 bearer challenge; clients obtain fresh authorization before retrying.
Revocation closes attachments and active requests. Optional Azure services reuse Function self-onboarding
and observation providers through request-confined OBO with federated managed identity client assertions;
never substitute host credentials. Deployment uses automatic Caddy TLS behind the existing load balancer
(whose MCP rule probes Caddy's port, never the admission-gated `/healthz`, so a worker with no stored certificate certifies before its commit;
the platform's `main.bicepparam` is the only writer of the world subnet's security group and its `WorldMcp` rule, and the compute template only references them),
certificate state the silo's unit cannot reach, which the TLS state keeper (`puck world host tls`, `WorldHostTlsKeeper`) keeps in
the owner's private store under a compare-and-swap lease document (a fresh worker restores it; one hostname issues once; Caddy's
unit is `Requisite=` on the keeper, which `Upholds=` it), and Azure expiry/readiness alerts. Durable delegated
cloud writes and richer participant tools remain uninstalled.
Run `tests/Puck.Hosting.Tests`, `tests/Puck.Networking.Tests`, `tests/Puck.Mcp.Tests`, `tests/Puck.Cli.Tests` (the `puck mcp` process interop) and the real-host smoke described in
[`Puck.Mcp`](../../../../src/Puck.Mcp/README.md) when changing this attachment seam.

## Host provider boundary

Cloud-specific implementations belong in extensions, and every host composes them one way
([Extensions](../../../../docs/reference/extensions.md) owns the contract):

| Step | Contract |
|---|---|
| Contract | One `IPuckExtension` (`Puck.Abstractions`) registering keyed, typed contributions; `[assembly: PuckExtension]` names entry types. No per-kind extension interfaces. |
| Discovery | `PuckExtensionDiscovery.Compose(builtIns, directories)` (`Puck.Hosting`): `extensions/<name>/<name>.dll`, own non-collectible load context; malformed installations refused by path. |
| Composition | `PuckExtensionSet.Compose`: name order, key order per kind; duplicate name, duplicate (kind, key), blank key, failing `Register` refused by name; received extensions disposed on refusal. |
| DI | `AddPuckExtensions` (`Puck.Launcher`): the set, `PuckHostedService` contributions, the one `HostedControl` when a control configuration is named, `world.extensions.catalog`. |
| Selection | `PuckExtensionSet.Select<T>(key, purpose)` (`Puck.Abstractions`) refuses an uninstalled key naming the installed ones; a null key takes the kind's one installed contribution and refuses none or several by name. |
| Configuration | One `puck.world.extensions.v1` document (`WorldExtensionConfiguration.Load`) attached by `WorldConfiguredExtensions.Attach` to a row: World's `--extensions-config-file` on the boot row, each silo row's `extensions` path. `WorldInstance.Extensions` holds the runtime; `world.extensions` (`Puck.World.Console`) echoes it in both hosts. A provider `type` installed as both operation and embedding is refused by name. An observation may name a `status` Int table the host keeps (`WorldExtensionObservationFeed`: cell `status` 0 pending, 1 live, 2 offline); the built-in `observation.fixture` provider (`WorldFixtureObservationProvider`) serves items written in the configuration, or a `failure`, for tests and headless runs. |

The contribution kinds, each registered by an extension method on
`IPuckExtensionRegistry` under a key unique within its kind:

| Kind | Key | Registered with | Read by |
|---|---|---|---|
| `IMachineEngine`, `IMachineContentProvider` | engine id | `AddMachineEngine` | every host's `WorldMachineCatalog.From` |
| `WorldStorageProvider` | store `type` | `AddStorage` | the silo document's `store`; World's `--storage-config-file` |
| `WorldSiloRetirementProvider` | observer `type` | `AddRetirement` | the silo's `lifecycle.observer` |
| `WorldSignInProvider` | `type` | `AddSignIn` | an issuer's `signIn` (the silo document's `issuer`, World's `--issuer-config-file`); World's `--authentication-config-file` |
| `WorldExtensionProviderType`, `WorldExtensionEmbeddingProviderType` | provider `type` | `AddOperation`, `AddEmbedding` | the extension configuration's `providers` |
| `WorldParticipantType` | participant `type` | `AddParticipant` | the extension configuration's `participants` |
| `ChatClientProvider` | provider name | `AddChatClient` | an `agent.harness` participant's `provider` |
| `WorldHealthCheck` | request path | `AddHealthCheck` | the silo's health listener |
| `PuckHostedService` | any | `registry.Add` | every host, started and stopped with it |
| `HostedControl` | always `control` | `AddHostedControl` | a host with an `IControlSessionHost`; one per host |
| `McpServicesProvider` | provider name | `registry.Add` | remote MCP, when its configuration names `services` |
| `WorldReleaseHost` | host name | `registry.Add` | CLI release operations (`--host`), official root document selection, provider command tree |

CLI deployment lives in the installed `Puck.Cli.Azure` extension, which contributes
the `azure` release host and `puck azure` commands. `CliExtensions` composes the
installation through the same discovery and selection above. `puck embed` selects
`WorldExtensionEmbeddingProviderType` with `--provider` and optional `--settings`
JSON, then binds `IWorldEmbeddingSource`; never construct a provider generator in
the CLI. Only tests may compose providers by project reference (PUCKARCH009).

A new provider (a GCP or S3 blob store for the silo and the World, say) is one optional
extension project; a store's `ObjectStorageTarget` subtype and the `IObjectBlobStoreBackend` it names live in the
provider's own project (`Puck.Storage.Azure` holds Azure Blob's), so no host or `Puck.Storage` registers a backend.
The extension is an `IPuckExtension` with a public parameterless constructor,
named after its assembly and declared with `[assembly: PuckExtension(typeof(...))]`,
whose `Register` calls the kind's method (`registry.AddStorage(provider: new WorldStorageProvider(...))`
under the `type` a silo document's `store` names, as `Puck.World.Azure`'s
`AzureWorldExtension` does for `azure.blob`). The project sets
`<EnableDynamicLoading>true</EnableDynamicLoading>` and publishes into
`extensions/<AssemblyName>`; no host, the silo, `Puck.Mcp` or the CLI references
it, and it adds no static registry, no directory scan of its own and no
per-kind extension interface. Registration does no I/O. A remote-MCP service is
an `McpServicesProvider` contribution, selected only when the MCP configuration
names `services` and refused with none or two installed. Check the composed
result with `world.extensions.catalog` in a running host
(`Puck.World.Server: WorldStorageProvider directory` is a built-in line), and
verify with `ExtensionModelLawTests`.

`Puck.World` built-ins: both Gaming Brick forges, the Gaming Deck forge and `WorldServerExtension` (`directory`
storage, the `observation.fixture` provider); the silo's:
`WorldServerExtension`. Both search `./extensions` then `<app>/extensions`. The silo selects persistence,
connection authentication, retirement observers and health endpoints from contributions by the document's
`type` with opaque settings. `WorldSiloHost` consumes a supplied `ObjectStorageTarget`; its lifecycle service
consumes `IWorldHostRetirementObserver`. Metadata polling, event types and credential handling stay in
`Puck.World.Azure`, an Optional extensions project no shipped host references.
A provider project declares `<PuckProvider>`, and the architecture gate (PUCKARCH009) refuses a provider project or provider SDK assembly in any provider-neutral project's closure except tests. Neither simulation nor replay executes physical host retirement. The silo README
owns provider-neutral configuration; the Azure README owns Azure provider keys. Verify with
`ExtensionModelLawTests` (`tests/Puck.World.Silo.Tests`).

## Production silo verification

Desktop update recovery lives in `Puck.Launcher/Release`, including the startup
health writer (`HealthGateFile`, not `Program`). Its hosted service first
registers the process (`InstallRegistration`: waits for `update.lock` without a
deadline, confirms by identity that `versions/<name>` is the directory its entry
assembly was loaded from, and holds it until exit, or fails the start with the
reason; registration also requires the release record to admit its version).
It then begins an `InstallTransaction` (the same persistent `update.lock` as
the stub and updater), records health when the record's current version is the
registered one (`RecordHealthy`), and runs one `InstallPruner` pass. Pruning and its invariants are in the
[launcher README](../../../../src/Puck.Launcher/README.md#pruning-past-versions);
every removal goes through `Puck.Confinement`'s handle-held directories, never a
path, and the stub holds its child's version the same way. `InstallPruneLawTests`
pins each review finding against it. One release record,
`state/release-policy.json` (`ReleasePolicy`), holds current, per-payload launch
attempts, identity-bound proofs, rejections, revocations and the state generation
floor; only `InstallTransaction` writes it, one durable commit per transition, and
an unproven version is never a rollback target. A current version without a valid
install record refuses launch ([the plan](../../../../docs/plans/updater-policy.md)).
Replay reservation precedes the durable recovery intent; interruption between
them needs a fresh signed release. `UpdateRecoveryHardeningTests` and
`UpdateInstallTransactionTests` cover these boundaries, including killed processes.
World-authored cache roots remain below the host updates directory; absolute
roots require trusted local update configuration.

Endpoint naming and world/host alias conventions are owned by
[CI and releases](../../../../docs/development/ci.md); deployment values belong in `main.bicepparam`.

Azure CI packages the debug world, the package `worlds/debug`, and any neighbours it references
with `puck world prepare`; hosted references use canonical world file
names. The storage-neighbour resolver parses and migrates the composed document
with state-expression binding, then reduces it to seam facts; it does not validate
the neighbour's unrelated local settings or recursively prove its adjacencies.
Hosted activation awaits root and neighbour storage reads before validating the
loaded world. Failed drain saves can be retried; closed ingress stays frozen.
An activation's federation subject and `WorldInstance.ListenEndpoint` come from
the published definition, independently of checkpoint network fields. Reload
checks that activation binding; moving an endpoint requires a fresh activation.
`puck azure test-world-host --install <root>` boots the primary (`debug`) row from an installed host release
(`puck world host build`, published on the `server` channel, installed with `puck world host fetch` as a hosting machine
installs it), verifies a durable checkpoint and the expected QUIC key, then boots again against the same store through the
deployment's MCP shape (`puck world host serve --mcp`, with an Azure-shaped `mcp.json` whose inventory observation builds an
ARM pipeline), so an extension that cannot load fails CI rather than the VM. Linux requires `libmsquic` and UDP ingress; a
hosting machine takes it from the host's package manager. How a machine installs and runs its release (the installer by pin,
`world host install`, the sandboxed units, the guest verbs) is [the decision](../../../../docs/decisions/runtime-and-delivery.md#hosting-machines).
`puck world release rehearse` (`WorldReleaseRehearsal`) runs the worker's managed start locally: a release
package (or a `world prepare` output, packaged first) through `WorldReleaseCoordinator.ResumeAsync` from Drain over a
directory store, with the release's own silo started as a child process on a managed silo document and reached through
the release-control routes. Use it before any change to the silo's startup, the release transaction or the worker's
silo document reaches a deploy: `--silo <Puck.World.Silo.dll> --extensions-dir <dir> --document-directory worlds/debug`
locally (publish the GB, AGB and HGD forge projects, and `Puck.World.Azure` to load the provider's extension, into that
directory, as `puck world host build` does), `--install` through an installed host release, as CI's `host-release` job does. `--mode
durable` adds a reset deployment through the source's `/release/drain` and a failed deployment the source is recovered
from; the storage steps over drained roots are `WorldReleaseRoots`, shared with `AzureCommand`'s runtime. Each silo's
lines are `<name>.log` in the output directory (`source`, `candidate`, `refused-candidate`, `recovered-source`) and the
file telemetry sink's records `telemetry.jsonl`. When private health is not ready it says why (`WorldSiloHost.DescribeLiveness`): activation's
progress or refusal, a pump fault, or the mailbox work or held step a stalled pump is inside; `Live` is false until
`Ready`, so "not progressing" alone never means a stalled pump before activation finished.

A deploy reads the deployment's hosted-data policy live (`AzureCommand.ReadWorldHostedDataPolicyAsync`, the latest
world-platform deployment's `worldSiloHostedData` output). Under `scratch` it replaces the world
(`AzureCommand.ReplaceWorldReleaseAsync`: qualify the candidate alone, then `WorldHostedData.DiscardAsync`, the one door
that deletes reachable hosted data, then the bootstrap). An unfinished discard leaves `<group>.discarding` beside the
group record, and `WorldReleaseGroupStore.LoadAsync` refuses the group until a discard finishes; an owner container
holds one group (`WorldHostedData.RequireSoleGroupAsync`). Everything from here to
the end of this section describes the `durable` path, which no deploy runs while the declaration is scratch.

An installed extension resolves each assembly name to one copy
(`PuckExtensionLoadContext`): the host's when the host carries at least the
version the extension's published graph carries, otherwise the graph's, whatever
version a reference asks for. Two copies of one name in one process split its
types: the CLI host carries the `System.Memory.Data` Orleans pins, older than the
Azure SDK's, and a reference to one copy's `BinaryData` finds no matching member
on a `System.ClientModel` bound to the other (`MissingMethodException` at startup).
Unmanaged pinned activation waits for host startup, establishes its initial checkpoint,
and reconciles changed published content through `WorldSiloHost.ReloadAsync`, which
runs the reload door the desktop's `world.reload` runs (`WorldReloadDoor`): the
published bytes are read as an edit off the mailbox, the delta is cut on the thread
pool from the revision the row holds, and the row's authority validates what it
installs once. A row whose revision already carries the published pin sends nothing.
A release marker holds that pin and advances only after its
checkpoint; retrying failed persistence must not rebuild twice.

Every write an activation publishes (a checkpoint, a journal batch, a crossing record,
a definition) goes through its `RowPublicationQueue`: strictly in enqueue order, under
the activation's fence, started on the thread pool, and never once the activation is
blocked. A write of edits that are already live (a journal batch) blocks the
activation on any failure; any other write blocks it only on a stale fence or an
uncertain commit. A blocked activation's unsent write answers a failed outcome. Drain waits for
accepted reloads before freezing the pump. `/healthz` includes persistence health;
`/livez` checks simulation progress independently. `WorldSiloLifecycleLawTests`
owns reload and drain failure controls. `WorldSiloDefinitionLawTests` checks
serialized health defaults. Failure during startup stops the host.
Managed release startup skips ordinary reload, restores privately, and opens all
rows only through the deployment-group publication barrier. Source recovery
restores the protected operation roots before recording `RecoverActivate`; a
restart in that phase activates without restoring again. Run
`WorldReleaseCutoverLawTests` for the real-host coordinator, latest-state rollback,
and interrupted recovery, plus `WorldReleaseArchiveLawTests` for retained package
integrity. These same-binary laws do not qualify a pair of packaged engines.
`world release deploy` retains exact inputs and enters this
coordinator after qualification; `resume` reads the pending operation and retained
configuration. `azure prepare-world-release` binds and pins all composed worlds.
Deploy and rollback automatically export current source state through loopback
`POST /release/fixture/<request-id>`; the host captures all rows at one pump boundary
without draining. `WorldReleaseFixtureArchive` publishes its inventory after the
immutable checkpoints. The CLI materializes exact captured state with test keys
and preserves machine identity. Run `WorldReleaseFixtureArchiveLawTests`, the
cutover laws, and `WorldReleaseFixtureBuilderTests`. Empty no-kit population checkpoints
preserve only the zero selection sentinel; `WorldEmptyPopulationCheckpointLawTests`
also rejects nonempty population and invalid kit selections. The operator
`rollback` command selects the retained predecessor directly from an admitted
commit. `WorldReleaseMetadataTransition` prepares metadata-only checkpoint changes
and their undo bases; its preservation laws include continuation, conflicts, custom
null presence, and undo after rollback. The Azure activation adapter calls
`WorldAuthorityBlobStore.PrepareReleaseMetadataAsync` to publish the transformed
checkpoint and definition in one CAS against the protected drain root, retaining
receipts and recognizing retries without rewinding candidate progress. Run
`WorldReleaseMetadataPublicationLawTests` for that boundary. Changed definition
pins require the runner's package-bound preservation exercise. `WorldReleaseQualificationTransition` uses the real atomic
publisher on disposable copies; both engines import the forward result, then both
import the reversed candidate continuation. Never reverse the original seed or
normalize away a checkpoint difference. Standalone `qualify` requires manifests
at their package roots for changed definitions; managed paths use the archive.
Every manifest carries the one coordinator contract,
`WorldReleaseManifest.CurrentCoordinatorContract` (`puck.world.release.restore.v1`), as a
required canonical member: metadata publication, receipt-aware qualification and
closed-group rewind enforcement. `TryValidate` refuses a missing member and every other
value by name, and the archive validates each manifest it loads, so no caller re-checks
the contract. Manifest pins parse through `Puck.Assets.ContentPin`, which refuses
uppercase hex. Run `WorldReleaseCoordinatorContractLawTests` and
`WorldReleasePinLawTests`.
Receipt export uses `CaptureReceiptSnapshotAsync`, which must
receive a root selected in the checkpoint's publication queue, never a later root
sampled after capture. `WorldAuthorityReceiptSnapshot` retains and validates the
original index and complete chain. `CreateReleaseFixtureAsync` creates only a new
disposable authority, retaining those references and the source sequence/journal
coverage under an unowned epoch. Run `WorldAuthorityReceiptSnapshotLawTests` and
`WorldReleaseReceiptFixtureLawTests`. `WorldSiloHost.ExportReleaseFixtureAsync` queues only the
root read at the capture boundary, then copies its immutable graph without holding
later publications. A canceled read must not poison the queue. `WorldReleaseFixtureArchive`
pins canonical receipt envelopes; every inventory row requires one, including explicit
empty history, and a row without its pin is malformed. Bootstrap fixtures publish each
world's exact release bytes through `WorldAuthorityBlobStore.PublishDefinitionBytesAsync`,
an unowned root with no gameplay state. Run cutover, archive and CLI
fixture tests for this path. Exercise reports use
`WorldReleaseExerciseResult.CurrentSchema` (`puck.world.qualification-exercise.v1`),
with all three receipt hashes required:
each engine checks every original receipt after import and continuation, then
duplicate and conflicting retries on the drained fixture without changing its root.
The runner independently computes the expected receipt hash before each leg.
`WorldReleaseReceiptProofTests` covers this helper.
Do not equate the checkpoint hash with receipt proof.
The official host release publishes `Puck.World.Azure`, both Gaming Brick forges, `Puck.Mcp` and `Puck.Mcp.Azure` with
their dependency manifests under its `extensions` directory, which the CLI that hosts the silo discovers beside itself. A load context resolves another installed extension's own assembly from that
extension, then anything the host carries at a compatible version, then an upstream extension's dependency, then
its own graph.
Discovered providers use process-lifetime load contexts; the discovery API has no
unload owner, so collectible contexts would allow premature dependency unloading.
Keep .NET hosted-service supervision and lifecycle callbacks when composing an
extension, and return failure if its background task faults. `puck azure test-world-host`
over an installed host release verifies actual startup, Azure health and the MCP shape.
Read retained package definitions with the composed-document parser, allowing
unfilled boot draws; checkpoint live and undo documents keep strict rehydration.
Bootstrap retries compare `LoadPublishedDefinitionBytesAsync` with the archived
bytes, never `LoadDefinitionAsync`'s initialized result. A different published
definition is replaced only when an earlier bootstrap of the same group captured
it in a recovery root (`PublishedByRecoveredBootstrapAsync` reads the group's
history), so a fixed release can bootstrap again after a failed first deploy.
Workers follow the same evidence: a bootstrap that finds a worker replaces it (deletes the scale set, then creates
fresh compute at Activate) only when `WorldReleaseGroupRecord.HasRecoveredBootstrap` holds, that is, the group never
committed and its history records a bootstrap that captured roots and recovered; otherwise it refuses. A bootstrap's
recovery deletes its compute (`StopCandidateAsync` with no source), since nothing restarts on it and Azure's
automatic repairs would keep rerunning the refused extension. Every start of a retained deployment
(`AzureWorldReleaseRuntime.EnsureWorkerAsync`: the candidate at Activate, Verify or Commit, the restored source at
RecoverActivate) only checks a worker already running its exact engine and replaces any other with fresh compute. Azure
re-runs a VM extension only when its configuration changes, so re-applying the model a worker already carries (a
source its drain stopped, a VM an automatic repair booted while the guest guard refused it) runs nothing and reports
that worker's last result, a stopped host or a stale refusal; a fresh worker's bootstrap meets the guard under the
phase as it is then. Never reconfigure an idle worker in place. A deploy decides what to do with the group's pending
operation through `WorldReleaseCoordinator.Succession`, shared by the Azure host and the succession laws: its own
release resumes, no unfinished operation begins (the store's `BeginAsync` moves an admitted operation into history and
closes its rollback window in the claiming write, so the pipeline never finalizes), and another release's unfinished
operation is superseded (`WorldReleaseCoordinator.SupersedeAsync`). Supersession admits a committed target first and
refuses with its cause when it cannot; abandons a Prepare operation or an uncaptured bootstrap (`AbandonAsync` keeps
any roots it saved unrecorded); and recovers every other pre-commit operation's source, finishing a drain's capture
first. So every failed release leaves a group the next deploy can follow without a hand-run resume or an Azure change,
except a committed release that cannot be admitted. `WorldReleaseCoordinatorSafetyLawTests.ADeploymentFollowsWhateverItsPredecessorLeftPending`
drives each phase against a host fake that makes the worker's durable writes. A deploy that cannot carry its source's
state resets instead of refusing, until preservation rules for definition edits exist; the reset root carries no rewind boundary. `WorldReleaseFixtureBuilder.BuildAsync` is the one decision:
with the deploy's successor it first asks `WorldReleaseCoordinator.FindDefinitionResetAsync`, which resets when a world's
package definition changes outside its metadata (`WorldReleaseMetadataTransition.ChangesOutsideMetadata`, the comparison
the transition itself refuses on), then `WorldReleaseCoordinator.FindStateReset`, which resets a source export this
build cannot decode. `WorldReleaseFixtureBuilderTests` shows a metadata-only successor carrying the captured state and
an edit outside metadata building the fresh fixture. `FindStateReset` decodes with the asset layout the silo resolves
relative paths beside (`LocalWorldsDirectory`), because a checkpoint embeds its definition with those paths unresolved and
decodes only beside that directory, exactly as `WorldSiloHost` decodes it; every release-path decode of a checkpoint
(the fixture builder, `WorldAuthorityBlobStore`, `WorldReleaseRestore`, the qualification transition) takes the same
directory, so a state the silo can restore is never reset or refused here; `BeginResetAsync` qualifies the target
alone and records a `WorldReleaseGroupHistoryEntry.ResetResult` entry (read back as `PendingStateReset`, in history so
an older draining source still parses the record); Activate calls `WorldAuthorityBlobStore.PrepareReleaseResetAsync`
per world instead of the metadata transition. Never add migration code or read tolerance for the old shape.
Every pre-commit recovery outcome names its cause: `RecoverPreCommitAsync` is
the one door, and it appends the failure that began recovery to every result. `world prepare` relocates
provider-declared machine asset paths from nested origins to the common worlds
directory, preserving the host release's asset layout; the silo resolves those paths beside its document's
`documentDirectory` (the installed version's `worlds`), which becomes every hosted definition's `DocumentDirectory`,
and the machine host reads content beside the directory of the definition it prepares. Colocated silo rows with neither
authority nor listen endpoint sign with the stable instance name, matching the
server's authority identity; listening rows still require an advertised endpoint.
Run release bootstrap/preparation/publication and silo lifecycle controls for these seams.
A checkpoint restore leaves navigation domains unbaked: goal fields are not checkpointed, and a goal-field
follower's entry carries no route to validate against a bake. Run Physics navigation laws and packaged
full-inventory restore controls.
A retained pending identifier alone does not mean maintenance is unfinished;
use `HasUnfinishedOperation`, and cover this boundary with `WorldReleaseRollbackTests`
and the cutover law's loopback export before rollback. Explicit restore uses
`WorldReleaseRestore` and a pinned `RestorePoint` through the ordinary phases.
`release.closedGroupRewind` blocks remote federation/claims and constrains local
transfers to the fixed pinned inventory. Capture establishes `RewindBoundary` in
the publication queue; later hosting must retain its policy. Preserve current
receipts and journal numbering when restoring old gameplay; never replace them
with the point's receipt history. Qualification copies explicitly drop the live
boundary proof. The focused `LocalDeployRollbackRewindAndInterruptedResumePreserveTheirDistinctStateContracts`
probe drives actual hosts, metadata publication, an internal transfer, interrupted
rewind, and restart. It compares the entire checkpoint with the explicit reconnect
parking adjustment, and verifies reconnect, retained current receipts, and recovery
of the fresh drain after a rejected private rewind and interrupted recovery. It is
not a packaged-engine or Azure acceptance substitute.
Local forwarding disposal must tolerate retired destinations without changing their
frozen checkpoint. An already completed admission under the same fence claim
still needs the group CAS and root census, but reuses fully published host effects;
do not add another simulation-boundary wait to a no-op publication retry.
Deploy, rollback, restore, resume, and
finalization use a renewable controller lease. Run the CLI
`WorldRelease*` laws for changes to retained
configuration, bootstrap, or controller ownership. The Azure lease law requires
Docker and the Azurite image documented in the CLI test README. Cancellation of
the local Azure CLI does not retract an already accepted remote operation.
The guest guard (`WorldReleaseGuestGuard`, run on a machine as `puck world host guard`) reads
the group record through `WorldReleaseGroupStore.LoadAsync`, so no second copy of its wire format
exists. Bootstrap and guest mutations share the machine's release lock
(`WorldHostLayout.ReleaseLock`) and reject stale effects after acquiring it. With no pending
operation, bootstrap permits only the admitted active release, so ordinary VM replacement can
recover without reopening a release transaction.
`world release qualify` runs each engine's own CLI from its install over a copied offline fixture; see [the CLI reference](../../../../docs/reference/cli.md#automation-commands)
for the fixture and evidence contract.
A pair that ran and failed a leg's claim is a `WorldReleaseQualificationResult.Failed`
verdict (the coordinator refuses the group claim with it; `qualify` exits 1); only a
run that cannot exercise the pair throws (exit 2). `IWorldReleaseQualificationEngines`
is the engine seam: installed processes in production (`WorldHostEngine` resolves an install
root to the version whose payload identity the manifest names), scripted in
`WorldReleaseFixtureBuilderTests`. It returns the packaged engine's captured output, and a leg
that exits nonzero carries the tail of its standard error and output in its failure, so the
deploy's refusal names the cause. Each package must preserve the same source import and
candidate-written reverse import, advance simulation, and checkpoint successfully. A same-engine
control proves the runner only. Named handheld machines support complete durable checkpoints;
run the real-core `queued-host-time-travel` probes and the world machine continuation
laws when changing this seam. Pumped addons and applied screen operations use the
ordinary authority checkpoint. Unsupported guest resources refuse mounting; live
coupled links and enabled machine rewind history still refuse capture rather than
being omitted from a qualification fixture.
`--authentication-config-file` selects an installed sign-in provider and the
authority's transport key pin; the run's own key is vouched for by the
authority's issuer (`WorldAttestationIssuer`, the silo document's `issuer`, a
World's `--issuer-config-file`), and every world the host runs trusts the
issuer's root without writing it into the document
([decision](../../../../docs/decisions/worlds-and-federation.md#players-are-admitted-through-their-hosts-issuer)).
A player build carries its deployment's sign-in: `puck world player --sign-in <file>`
ships the file as `world-authentication.json` beside the provider's `--extension`, and
refuses a sign-in the folder's extensions cannot select
([decision](../../../../docs/decisions/runtime-and-delivery.md#the-deployments-issuer)).
No token belongs in world content or checkpoints, and no world verifies one:
Azure's provider validates ByteTerrace API membership at the issuer only. Prove
the flow locally with `puck benchmark --headless --route debug --remote issued`
(an authority with an issuer and the engine's `puck.accounts` sign-in, and a
client that pins no key in the document). Run the real client against the
deployed endpoint for admission, authoritative interaction, and reconnect
evidence; a QUIC key probe alone does not prove these. `docs/development/ci.md`
owns Azure deployment policy.
