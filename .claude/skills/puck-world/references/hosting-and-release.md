# Hosting, extensions, and release

How a World host composes extensions, runs agent participants and the MCP
attachment, and how the production silo and its release coordinator are
verified. The base world projects never reference the agent projects or any
cloud SDK; everything here is an extension or a host concern.

## Contents

- Agent participants and the MCP attachment
- Host provider boundary
- Production silo verification

## Agent participants and the MCP attachment

A host runs an agent as an extension-configuration `participants` row (`WorldParticipantType`, in Protocol), which `WorldConfiguredExtensions` creates, pumps at every closed boundary, and disposes; the participant acts only through its principal's grants and refuses the console principal. Verify with `WorldAgentParticipantLawTests` (`tests/Puck.World.Agents.Tests`) and `WorldSiloExtensionLawTests`. The Operator MCP adapter lives in
`Puck.Mcp`, referenced by CLI for local stdio, never by base World or the silo. Remote MCP is the installed
`Puck.Mcp` extension's `HostedControl`, started by the silo's `--mcp <remote.json>` (which is what
`puck mcp --silo <silo.json> --http <remote.json>` runs); the silo exposes only Hosting's neutral
`IControlSessionHost`. A `services` member in that configuration selects exactly one installed
`McpServicesProvider` (`Puck.Mcp.Azure` ships the `azure` one) and is refused by name with none or two. World installs the `Puck.Hosting` local control endpoint only
when the host Console issues `world.control start`; `stop` and `status` manage its live lifetime. Attach with
`puck mcp --profile operator --attach <printed-file>` on Windows or Linux x64. The file protects a mutual-authenticated
loopback capability for the current OS user, including its other processes/elevation levels. Each connection has
one bounded, dedicated Console session. Exec preserves ordinary result uncertainty; capture uses the same
session's `InvokeAsync` barrier and the exact render request's completion, with off-pump waiting and temporary
artifact cleanup. Host deadlines remain enforced even when an injected session ignores cancellation; invalid
host results are `unknown`. MCP results carry the same schema-backed metadata as JSON text and structured
content; invalid tool arguments return tool errors. The adapter bounds UTF-8 input and pending replies and
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
never substitute host credentials. Deployment uses automatic Caddy TLS behind the existing load balancer,
persistent certificate state outside World mounts, and Azure expiry/readiness alerts. Durable delegated
cloud writes and richer participant tools remain uninstalled.
Run `tests/Puck.Hosting.Tests`, `tests/Puck.Networking.Tests`, `tests/Puck.Cli.Tests` and the real-host smoke described in
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
| Configuration | One `puck.world.extensions.v1` document (`WorldExtensionConfiguration.Load`) attached by `WorldConfiguredExtensions.Attach` to a row: World's `--extensions-config-file` on the boot row, each silo row's `extensions` path. `WorldInstance.Extensions` holds the runtime; `world.extensions` (`Puck.World.Console`) echoes it in both hosts. A provider `type` installed as both operation and embedding is refused by name. |

`Puck.World` built-ins: both Gaming Brick forges plus `WorldServerExtension` (`directory` storage); the silo's:
`WorldServerExtension`. Both search `./extensions` then `<app>/extensions`. The silo selects persistence,
connection authentication, retirement observers and health endpoints from contributions by the document's
`type` with opaque settings. `WorldSiloHost` consumes a supplied `ObjectStorageTarget`; its lifecycle service
consumes `IWorldHostRetirementObserver`. Metadata polling, event types and credential handling stay in
`Puck.World.Azure`, an Optional extensions project no shipped host references.
The compiled-output architecture gate denies Azure SDK API use in Schema, Protocol, Server, and Client. Neither simulation nor replay executes physical host retirement. The silo README
owns provider-neutral configuration; the Azure README owns Azure provider keys. Verify with
`ExtensionModelLawTests` (`tests/Puck.World.Tests`).

## Production silo verification

Endpoint naming and world/host alias conventions are owned by
[CI and releases](../../../../docs/development/ci.md); deployment values belong in `main.bicepparam`.

Azure CI packages `Assets/worlds/puck.world.json` and its referenced neighbours
with `puck world prepare`; hosted references use canonical world file
names. The storage-neighbour resolver parses and migrates the composed document
with state-expression binding, then reduces it to seam facts; it does not validate
the neighbour's unrelated local settings or recursively prove its adjacencies.
Hosted activation awaits root and neighbour storage reads before validating the
loaded world. Failed drain saves can be retried; closed ingress stays frozen.
An activation's federation subject and `WorldInstance.ListenEndpoint` come from
the published definition, independently of checkpoint network fields. Reload
checks that activation binding; moving an endpoint requires a fresh activation.
`puck azure test-world-container --image <image>` boots the primary Puck row, verifies a durable
checkpoint and the expected QUIC key, then replaces the container against the
same store and repeats the checks. Linux requires `libmsquic` and UDP ingress.
Unmanaged pinned activation waits for host startup, establishes its initial checkpoint,
and reconciles changed published content through `WorldSiloHost.ReloadAsync` and
the ordinary rebuild submission. A release marker advances only after its
checkpoint; retrying failed persistence must not rebuild twice. Drain waits for
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
integrity. These same-binary laws do not qualify a pair of packaged engine images.
`world release deploy` retains exact inputs and enters this
coordinator after qualification; `resume` reads the pending operation and retained
configuration. `azure prepare-world-release` binds and pins all composed worlds.
Deploy and rollback automatically export current source state through loopback
`POST /release/fixture/<request-id>`; the host captures all rows at one pump boundary
without draining. `WorldReleaseFixtureArchive` publishes its inventory after the
immutable checkpoints. The CLI materializes exact captured state with test keys
and preserves machine identity. Run `WorldReleaseFixtureArchiveLawTests`, the
cutover laws, and `WorldReleaseFixtureBuilderTests`; build the candidate image as
`puck/world-silo:candidate` for the latter's unchanged-definition and metadata Docker controls. Empty no-kit population checkpoints
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
publisher on disposable copies; both images import the forward result, then both
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
each image checks every original receipt after import and continuation, then
duplicate and conflicting retries on the drained fixture without changing its root.
The runner independently computes the expected receipt hash before each leg.
`WorldReleaseReceiptProofTests` covers this helper.
Do not equate the checkpoint hash with receipt proof.
The official image publishes `Puck.World.Azure`, both Gaming Brick forges, `Puck.Mcp` and `Puck.Mcp.Azure` with their
dependency manifests under `/app/worlds/extensions`. Both shipped entry points
discover that directory. A load context resolves another installed extension's own assembly from that
extension, then anything the host carries at a compatible version, then an upstream extension's dependency, then
its own graph.
Discovered providers use process-lifetime load contexts; the discovery API has no
unload owner, so collectible contexts would allow premature dependency unloading.
Keep .NET hosted-service supervision and lifecycle callbacks when composing an
extension, and return failure if its background task faults. Run
`WorldReleasePackagedHostTests` against `puck/world-silo:candidate` to verify actual image
startup, both machine types, Azure health, MCP discovery and failed-listener shutdown.
Read retained package definitions with the composed-document parser, allowing
unfilled boot draws; checkpoint live and undo documents keep strict rehydration.
Bootstrap retries compare `LoadPublishedDefinitionBytesAsync` with the archived
bytes, never `LoadDefinitionAsync`'s initialized result. `world prepare` relocates
provider-declared machine asset paths from nested origins to the common worlds
directory, preserving the image's asset layout. Colocated silo rows with neither
authority nor listen endpoint sign with the stable instance name, matching the
server's authority identity; listening rows still require an advertised endpoint.
Run release bootstrap/preparation/publication and silo lifecycle controls for these seams.
`NavigationRuntime.Domain.ValidateShared` must leave empty shared-navigation slots
unbaked during checkpoint restore. Validate scheduler and empty-slot shape without
querying geometry; resident trees still bake and validate their recorded static
edges. Run Physics navigation laws and packaged full-inventory restore controls.
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
not a packaged-image or Azure acceptance substitute.
Local forwarding disposal must tolerate retired destinations without changing their
frozen checkpoint. An already completed admission under the same fence claim
still needs the group CAS and root census, but reuses fully published host effects;
do not add another simulation-boundary wait to a no-op publication retry.
Deploy, rollback, restore, resume, and
finalization use a renewable controller lease. Run the CLI
`WorldRelease*` and `CheckedProcessCancellationTests` laws for changes to retained
configuration, bootstrap, or controller ownership. The Azure lease law requires
Docker and the Azurite image documented in the CLI test README. Cancellation of
the local Azure CLI does not retract an already accepted remote operation.
`build/Guard-WorldRelease.py` reads the durable C# group wire format; keep its
numeric phase mapping synchronized with `WorldReleaseOperationPhase`. Bootstrap
and guest mutations share a VM flock and reject stale effects after acquiring it.
With no pending operation, bootstrap permits only the admitted active release,
so ordinary VM replacement can recover without reopening a release transaction.
The CLI `WorldReleaseGuestGuardTests` requires Python 3 on `PATH`.
`world release qualify` executes exact Docker images over a copied offline fixture
with networking disabled; see [the CLI reference](../../../../docs/reference/cli.md#automation-commands)
for the fixture and evidence contract.
A pair that ran and failed a leg's claim is a `WorldReleaseQualificationResult.Failed`
verdict (the coordinator refuses the group claim with it; `qualify` exits 1); only a
run that cannot exercise the pair throws (exit 2). `IWorldReleaseQualificationContainers`
is the container seam: Docker in production, scripted in `WorldReleaseFixtureBuilderTests`.
Each package must preserve the same source import and candidate-written reverse
import, advance simulation, and checkpoint successfully. A same-image control proves
the runner only. Named handheld machines support complete durable checkpoints;
run the real-core `queued-host-time-travel` probes and the world machine continuation
laws when changing this seam. Pumped addons, applied screen operations, live coupled
links, and enabled machine rewind history remain uncapturable and must reject
qualification rather than be omitted from its fixture.
`--authentication-config-file` selects an installed client provider and server
key pin; no token belongs in world content or checkpoints. Azure's provider
validates ByteTerrace API membership, while generic protocol code sees only the
verified session namespace. Run the real client against the deployed endpoint
for admission, authoritative interaction, and reconnect evidence; a QUIC key
probe alone does not prove these. `docs/development/ci.md` owns Azure deployment policy.
