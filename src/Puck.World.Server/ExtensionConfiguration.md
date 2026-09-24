# Compose service extensions with data

A host operator can select installed service providers, create several instances
with different identities, bind their operations, and connect them to game state
without writing C#. Deterministic WASM modules remain selected by the world's
existing `addons` rows. Both can participate in the same authored game.

Provider types come from the host's installed [extensions](../../docs/reference/extensions.md):
`Puck.World.Azure` contributes `azure.resource`, and `Puck.World.Embeddings`
contributes `embedding.fixture` and `embedding.azure-openai`. Participant types
come the same way: `Puck.World.AgentHarness` contributes `agent.harness`. Run
`world.extensions.catalog` to ask the running host which types it composed.
Installing another service implementation means installing its extension beside
the host; configuration never loads DLLs or executes paths.

## Select a deployment

Copy the [Azure example](../Puck.World/Assets/hosting/azure.extensions.example.json)
to a private deployment directory. Set the resource ID, API version, incarnation,
and authentication arrangement for the resource you intend to operate. Choose a
new lineage UUID for a new authority, then retain it across ordinary restarts.
Never reuse an example's lineage for independent deployments.

```text
dotnet run --project src/Puck.World -c Release -- --world my.world.json --extensions-config-file my.extensions.json
```

The configuration's `world` must match that document's `documentId`. It applies
to the boot authority; separately started world instances do not inherit its
service access. A remote-client boot cannot enable it. Without the option,
external services are disabled.

A [silo](../Puck.World.Silo/README.md) reads the same document per row: a
`worlds[]` row's `extensions` member names its file, and the row's world must
match its `world`. The silo reads the file when the row activates and refuses the
activation by name if the file or anything it selects is wrong. Both hosts attach
the configuration through `WorldConfiguredExtensions.Attach`, so the same file
selects the same providers and participants in either one. Give every row its own
file and lineage; two rows naming one file are refused when the silo starts.

The file is host approval, so keep it outside imported worlds and writable addon
storage. Puck reads the explicitly selected file with the
[confined storage reader](../Puck.Storage/README.md), rejects unknown and duplicate
members, and validates provider names, operation references, clients, and state
connections before starting work. Missing configuration never falls back to
ambient cloud access.

## Mix providers and bindings

The configuration separates reusable provider instances from resource operations:

```json
"providers": [
  { "name": "production", "type": "azure.resource",
    "settings": { "authentication": "managedIdentity", "clientId": "<identity-client-id>" } },
  { "name": "development", "type": "azure.resource",
    "settings": { "authentication": "azureCli", "tenantId": "<tenant-id>" } }
]
```

Each operation names one provider instance. Several operations can share that
instance, and each pins its own resource, incarnation, method, API version,
optional action, query arguments, and ETag. The
[Azure guide](../Puck.World.Azure/README.md) owns those settings. Credentials are
selected by an explicit authentication method; secrets are not pasted into
request tables. Provider instances using different credentials stay separate.

Each client names its canonical acting principal, allowed operation names, and
ordinary world capability requests. Requests restrict access; the world's grants
must also authorize it. Optional `storageBytes` and `storageWritable` give that
client an isolated storage capability. World content cannot create a service
grant by adding a request table or choosing an operation name.

## Connect a game

A connection maps a **text request table** to an operation and an **integer
status table**. Add these ordinary rows to the world's `state.world` array for
the example configuration:

```json
{ "name": "cloud-requests", "kind": "Text", "capacity": 64,
  "cells": [], "visibility": {} },
{ "name": "cloud-status", "kind": "Int", "capacity": 64,
  "cells": [], "visibility": {} }
```

Here `visibility: {}` permits public observation. For private requests, restrict
the readers to the configured principal using the existing visibility policy.
The example explicitly uses the trusted local console principal and its world
grants. A hosted controller can instead use a named addon principal with narrow
Observe, Mutate, and Edit grants over its tables.

When a creature dies, an authored rule writes its request JSON into
`cloud-requests` under a stable key such as `creature-17-incarnation-3-death-1`.
The local console can exercise that same state interface:

```text
world.state.cell.set cloud-requests creature-17-incarnation-3-death-1 {}
world.extensions
world.state cloud-status
```

The connection commits the request with recovery evidence, and the shared worker
dispatches it. It writes status under the **same key**. An optional `results`
property names a text table receiving the provider's progress/result payload.
That table must also exist and be observable and editable by the client.

Status values are the existing `WorldExternalOperationStatus` encodings:

| Value | Meaning |
|---|---|
| 0 | Pending: durably queued |
| 1 | Dispatching: claimed for execution |
| 2 | Running: service accepted asynchronous work |
| 3 | Succeeded |
| 4 | Failed |
| 5 | Unknown: outcome requires reconciliation |

A missing status cell means no status has yet been projected. Readers may miss
intermediate states. Rules can respond to these ordinary values: dissolve a
creature, change its appearance, print text, or trigger another request. No
renderer or service-specific rule is required.

Never reuse a request key for another incarnation or change its payload after
commit. A repeated identical request resolves to the original operation. Clearing
the table or renaming a connection does not create a new operation identity:
keys are scoped to the authority lineage, client principal, and operation name.
Use distinct keys for independent requests even when they originate in different
tables. Clearing the request table does not cancel an effect or erase its
deduplication history.
Results for old keys cannot overwrite newer keys. Separate connections require
separate output tables; chaining is an authored rule writing a new request, so a
service result cannot accidentally become another request body.

## Observe a collection

An `observations` entry reads an approved external collection and maps selected
fields into existing state rows of any cell kind, and nothing else. The
[granaries example](../Puck.World/Assets/worlds/modules/README.md) uses this
path to represent storage accounts without configuring any cloud mutation
operations; the [Azure hosting example](../Puck.World/Assets/hosting/azure.extensions.example.json)
also shows a `Fixed` row fed by a metrics observation. An observation writes
rows only; a district that wants one placement per observed row reads the
target row from an ordinary authored rule or placement facet.

Each entry names a provider, configured client, provider-specific `settings`,
and `fields` mapping provider field names to world row names. Rows need
explicit `observe state:<name>` requests, visibility for that principal, and
ordinary mutation authority. Each output row belongs exclusively to one
projection, has capacity at least `maximumItems`, and cannot double as an
operation request table. A field's value string parses by its row's own cell
kind: `Int` as an invariant integer, `Fixed` as an invariant decimal to Q48.16,
`Bool` as `true`/`false` or `1`/`0`, `Text` as authored. A value that does not
parse refuses the whole projection this cycle by field name — never a partial
collection — and the previous projection stands; `world.extensions` echoes the
refused field until a later cycle parses clean. Rules consume these ordinary
keyed cells.

Only a complete snapshot can replace the previous collection. Changed rows
enter one recorded mutation batch; unchanged snapshots submit nothing.
Failed, partial, or oversized reads retain the last complete collection. Normal
admission can still refuse a projection; read-back retries it until it is applied.

`refreshTicks` paces attempts, including failures, using simulation time; a stopped
world starts no new reads. `scanEveryTicks` also bounds when results are collected.
There are at most 16 sources, one call in flight per source, 128 items per source,
and 16 mapped fields. Source calls run on workers with the configured worker
operation timeout and host cancellation. These reads do not enter the durable
effect journal. Accepted gameplay batches are recorded through the existing
extension ingress. Replay revokes the client, stops new reads, and prevents late
results from being projected; fresh host composition is required to reconnect.

## Embed text at runtime

Hosts can connect runtime text embedding generation directly to world state tables:

```json
"embeddings": [
  {
    "name": "dialogue-embeddings",
    "provider": "local-fixture",
    "client": "addon:dialogue-embedder",
    "space": "lore",
    "requests": "said",
    "results": "saidVectors",
    "status": "saidStatus",
    "maximumItems": 32,
    "batchSize": 32,
    "retryTicks": 60,
    "cacheEntries": 256
  }
]
```

- **`provider`**: names an embedding provider instance declared under `providers`.
- **`space`**: names an active embedding space declared in the world definition. The provider's model, revision, and dimensions must match the world space's identity.
- **`requests`**: a `Text` state table. Cells added or updated here become candidate embedding requests.
- **`results`**: a `Vector` state table. On success, generated unit-normalized 8-bit vectors (`sbyte[]`, radius 127) are written here under the same key.
- **`status`**: an `Int` state table recording status: `0` = absent, `1` = pending, `2` = in flight, `3` = succeeded, `4` = failed.
- **`maximumItems`**: maximum items processed per scan (`1..128`).
- **`batchSize`**: chunking batch size passed to the embedding provider (`1..2048`).
- **`retryTicks`**: simulation ticks to wait before retrying after a provider refusal or error.
- **`cacheEntries`**: size of the connection's in-memory LRU text-to-vector cache (`0..65536`). Cache hits make zero provider calls.

The host scans embedding requests asynchronously outside simulation ticks. Results are submitted as atomic
mutation batches protected by `ExpectedCells` text guards: if request text changes while a generation task is
in flight, the stale vector is safely discarded. During replay, recorded gameplay mutations reproduce all
vectors deterministically without contacting any embedding provider.

## Run an agent participant

A `participants` entry runs an autonomous participant in the world. It names an
installed participant type, the principal it acts as, the 0-based body it
controls, and the type's own settings:

```json
"participants": [
  {
    "name": "guide",
    "type": "agent.harness",
    "principal": "addon:guide",
    "body": 5,
    "settings": {
      "objective": "Help visitors reach the observatory without blocking the path.",
      "provider": "azure.openai",
      "providerSettings": {
        "endpoint": "https://<resource>.openai.azure.com/",
        "deployment": "<chat-deployment>"
      },
      "approval": "refuse",
      "turnSeconds": 10
    }
  }
]
```

The participant acts only through the world's grants. Its principal must be a
seat, an addon, or a peer, never the console, and the world must grant it
whatever it should observe and drive. The configuration grants nothing.

The [agent harness](../Puck.World.AgentHarness/README.md) owns the
`agent.harness` settings, and each model provider owns its own `providerSettings`;
[Puck.World.AgentHarness.Azure](../Puck.World.AgentHarness.Azure/README.md)
provides `azure.openai`. A participant starts with the host and stops when the
host stops or the silo row retires. At most 16 participants may be configured.
An unknown participant type, provider, or setting refuses the whole
configuration by name before any work starts.

## Recovery, limits, and read-back

`recovery: "checkpoint"` captures a settled authority checkpoint. Worlds that
pump WASM or screen machines need `recovery: "recording"`; this explicitly starts
a recovery recording at boot and captures closed replay prefixes. Retain the
pinned module assets. Stopping that recording prevents admission of new requests
requiring a prefix. Recording and journal byte ceilings still apply; a growing
recovery image can exhaust the configured journal budget.

A local World keeps the journal under the host state root's `extensions`
directory; a silo row keeps it in the silo's configured store. Either way it is
isolated by lineage UUID, outside rewindable world saves. Live replay revokes existing
clients. Reopening service access requires explicit host recovery and fresh
composition, never automatic reconnection to the original resources.

`scanEveryTicks` sets connection and observation scan cadence. `worker` optionally sets the
[shared worker policy](ExtensionHosting.md); `maximumEntries` and `maximumBytes`
bound retained history. Startup constructs and validates providers but does not
make service calls. A visible request is what authorizes the already-granted
operation to enter durable history.

`world.extensions` reports the host console's operation names, connection wiring,
each observation's provider-declared kind, item counts, freshness ticks, read-back
`applied` flags, submissions, failures, the field name a value last refused to
parse under, and each participant's status line; other callers see only their
granted operations. In a silo it reads the row the Console addresses. Status tables reflect ordinary
authority admission: an external success can coexist with a refused gameplay
write. The connection checks read-back and retries an unapplied projection while
its request remains present. The ordinary mutation outcome stream explains
refusals. No SDK exception messages or private recovery images are exposed as
provider results by this connector.
