# Puck.World.Azure

This optional extension lets Puck request changes to Azure resources through
Azure Resource Manager (ARM), Azure's management API. A creature's death can
request deletion of its associated resource; a world rule, script, or AI actor
can originate the same request. The host controls which bindings each caller
may invoke. An MCP adapter can expose those bindings to an AI harness without
changing delivery or recovery.

`AzureResourceOperationProvider` implements the existing
[`IWorldExternalOperationProvider`](../Puck.World.Server/Extensions.md) contract.
It supports POST actions, PATCH updates, DELETE, and PUT creation/replacement
across ARM resource types. Only `Azure.ResourceManager` is needed; there is no
Compute, Storage, or other specialist management SDK dependency. The resource
provider still decides which methods, API versions, and JSON shapes it accepts.

The implementation uses `ArmResource`'s authenticated SDK pipeline with raw
JSON. The typed `GenericResource` CRUD surface cannot express arbitrary POST
actions or preserve every provider-specific field. Authentication, credential
refresh, cloud selection, and HTTP transport remain Azure SDK responsibilities.
See Microsoft's [generic resource API](https://learn.microsoft.com/en-us/dotnet/api/azure.resourcemanager.resources.genericresource)
and [ARM resource pipeline](https://learn.microsoft.com/en-us/dotnet/api/azure.resourcemanager.armresource.pipeline).

## Silo hosting

`AzureSiloExtensions` registers opt-in storage, retirement, and API-user authentication providers:

```json
"store": { "type": "azure.blob", "settings": { "accountUrl": "https://<account>.blob.core.windows.net" } },
"lifecycle": {
  "shutdownSeconds": 120, "healthPort": 8081,
  "progressTimeoutSeconds": 30, "checkpointTimeoutSeconds": 180,
  "journalTimeoutSeconds": 30, "journalBacklogLimit": 1024,
  "observer": { "type": "azure.scheduled-events", "settings": { "pollSeconds": 1 } }
}
```

This extension validates those settings. Blob persistence uses the deployed
identity's restricted storage access. `AzureScheduledEvents` polls local metadata
for maintenance affecting this VM and supplies a retirement deadline through the
provider-neutral `IWorldHostRetirementObserver` contract. It ignores temporary
freezes, never acknowledges events on behalf of other processes, and propagates
retirement failures. No metadata access occurs unless this observer is selected.
The [silo lifecycle](../Puck.World.Silo/README.md#hosted-test-world) owns drain behavior;
[deployment](../../docs/development/ci.md) owns VMSS policy and Spot qualification.

`AzureApplicationHealth` formats the v2 Application Health extension's rich
health JSON. The silo serves it at `/livez/azure` with HTTP 200 for both
`Healthy` and `Unhealthy`; missing JSON or a non-success response means `Unknown`
to Azure. This uses simulation liveness, independently of storage readiness.
See the [Azure health extension contract](https://learn.microsoft.com/en-us/azure/virtual-machines/extensions/health-extension).

`azure.api-users` wraps the world's federation authenticator at the connection
boundary. Its deployment-owned settings are `tenantId`, `audience` (the API
application ID), `groupId`, and `scope`. A user needs a signed, unexpired delegated
API token from that tenant, with the required scope and group claim. ByteTerrace
API Users is the Puck user group. App-only tokens and group-overage tokens without
the required group claim are refused. Explicitly trusted world peers retain the
existing attestation path.

The desktop opts into this provider with `--authentication-config-file <path>`
alongside `--connect <host:port>`. The file contains `type` and `settings`; clients
also require `remoteKeyHash`, the lowercase SHA-256 fingerprint of the host's
public peer key. Deployment writes this public authentication configuration beside the world artifacts.
The provider uses the existing ambient Azure sign-in with the API's `/.default`
scope; the server independently checks `user_impersonation`. The client verifies
the peer key before sending its token. Tokens stay outside world documents,
checkpoints, logs, and simulation. The verified user ID supplies the client's
local instance namespace together with a per-process session ID, while the public
world's authority remains its endpoint. Reconnecting transport lanes retain that
namespace. A fresh process gets a new one, preventing stale transfer epochs from
the previous process from blocking entry; restoring a previous process's body is
a separate durable travel-recovery operation.

The extension also owns `WorldApiCounterpartResolver` and `HttpCounterpartPublisher`,
which implement the engine's neighbour-resolution and counterpart-publication seams
using Azure credentials. The engine interfaces carry no Azure credential types.
## Host composition

The normal world executable registers `azure.resource` as an installed provider
type. Operators select instances and bindings with
[declarative extension configuration](../Puck.World.Server/ExtensionConfiguration.md).
External access stays disabled unless the operator selects a configuration file.
A document's `addons` section still mounts WASM modules.

Provider `settings.authentication` explicitly selects `managedIdentity`
(optional `clientId`) or `azureCli` (optional `tenantId`). `cloud` selects `public`,
`government`, or `china`. The Azure CLI option uses the CLI's existing sign-in;
managed identity uses the configured host identity. There is no implicit fallback
credential chain and no client-secret or arbitrary token-file configuration.
See the SDK's [managed identity selection](https://learn.microsoft.com/en-us/dotnet/api/azure.identity.managedidentityid).

Custom C# hosts may also reference this project directly. They supply a
`TokenCredential`, using their chosen managed
identity, workload identity, or other Azure authentication arrangement.
The resource-operation provider never searches for credentials or selects a subscription implicitly.

Create a binding for each permitted resource operation. Bindings fix the resource
ID, its incarnation, the method, the API version, and an optional action path or
ETag precondition. `QueryParameters` carries provider-specific query arguments;
names and values are escaped, and cannot override the separately pinned API
version. Resource IDs and action paths are unescaped ARM paths, not
arbitrary service URLs. The payload is an unchanged JSON object in the selected
resource provider's schema. POST and DELETE may omit it.

Use the [shared extension host](../Puck.World.Server/ExtensionHosting.md) to own
dispatch and polling. `server`, `journal`, and `credential` are root-owned
objects. Resource selection and API version come from the host's service
configuration. The caller gets a granted client:

```csharp
var binding = new AzureResourceBinding(
    Name: "creature.delete",
    ResourceId: configuredResourceId,
    Incarnation: resourceIncarnation,
    Method: AzureResourceMethod.Delete,
    ApiVersion: configuredApiVersion);

using var provider = new AzureResourceOperationProvider(credential, binding);
await using var host = new WorldExtensionHost(server, journal, authorityLineage,
    [provider.Register("Delete this creature's associated resource")],
    () => server.CaptureExternalOperationCause(hostRow));
using var client = host.CreateClient(WorldPrincipal.Addon("creature-controller"),
    allowedOperations: [binding.Name], worldRequests: manifest);
host.Start();

var operation = await client.InvokeAsync(binding.Name,
    requestKey: "creature-17/incarnation-3/death-1",
    cancellationToken: cancellationToken);
var progress = await operation.ReadAsync(cancellationToken);
```

For a POST action, set `Method` to `Post` and `Action` to the resource-relative
action name, such as `start`. For PATCH or PUT, pass the JSON object as the
`input` argument to `client.InvokeAsync`. Each operation binding has its own identity;
changing its target, incarnation, method, version, cloud, or precondition refuses
old requests instead of redirecting them.

An incarnation pin protects against host configuration changes. It cannot prevent
someone independently replacing an Azure resource under the same ID. Use
`IfMatch` when that resource API supports ETag preconditions, and have the host
coordinate resource replacement. This adapter does not invent a universal ARM
incarnation check.

Puck's world grants govern observations and gameplay contributions. They do not
authorize Azure operations. The trusted host authorizes access to bindings, and
Azure RBAC independently checks the supplied credential. Do not hand untrusted
code a dispatcher containing bindings it should not invoke. Grant a scoped
client instead; its operation discovery contains only those grants.

## Read resource inventory

For read-only discovery, `AzureResourceInventory` uses the same generic ARM SDK
pipeline to list an explicitly named resource group's resources. The
[granaries deployment](../Puck.World/Assets/hosting/granaries.extensions.json)
shows the complete no-C# wiring. Source settings pin `resourceGroup`, `apiVersion`,
and `resourceType`; optional `namePrefix` and `excludeNames` narrow membership.
`fields` maps disclosure names to slash-separated property paths such as `/id`,
`/location`, and `/sku/name`. Only selected scalar values leave the adapter;
missing or null properties become empty strings. Select only metadata safe for
the world's readers. ARM metadata does not imply usage, health, or grain placement.

All pages must succeed before publication. Defaults limit the query to 16 pages
and each response to 1 MiB (`maximumPages` and `maximumResponseBytes`); the source
also caps inspected resources at 4096 and honors the observation's item ceiling.
Duplicate IDs, out-of-group resources, malformed bodies, and exceeded limits
refuse the whole read. Continuations must remain on the exact approved HTTPS
origin and collection path. Redirects remain disabled. GET reads may retry twice;
the mutation adapter's no-resend policy is unchanged. Resource Graph and telemetry
queries can be added as distinct Azure source capabilities; this implementation
does not yet execute those queries.

## Read resource metrics

`AzureResourceMetrics` reads the latest complete `Microsoft.Insights/metrics`
bucket for an authored list of metric names over one resource. Source settings
pin `resourceId` (an absolute ARM resource ID, not a resource group), `apiVersion`,
`metricNames`, `aggregation` (`Average`, `Total`, `Count`, `Minimum`, or `Maximum`),
and `interval` (one of the closed Azure Monitor grains, `PT1M` through `P1D`).
Each item is keyed by metric name with one `value` field: the aggregated value as
an invariant decimal string, read from the newest bucket that still carries the
requested aggregation—a still-filling trailing bucket is skipped rather than
read as zero. A response missing a requested metric, carrying more than one
timeseries for it, or never completing a bucket refuses the whole read.

`AzureConfiguredProvider.BindObservation` dispatches on the settings' own `kind`
field: absent or `"inventory"` selects the resource-group inventory above,
`"metrics"` selects this reader. The
[Azure hosting example](../Puck.World/Assets/hosting/azure.extensions.example.json)
shows one of each. No credentials ever appear in either kind's settings; both
share the provider's own credential and the same bounded, complete-or-refused
snapshot contract.

## Completion and recovery

The shared host runs service work on workers. The dispatcher claims each operation durably
before sending it. The SDK's automatic transient retries are disabled, and the
default HTTP transport does not follow redirects. The client-request ID is a
correlation aid, not an Azure idempotency guarantee.

An asynchronous response produces a durable `AzureResourceOperationReceipt`.
It retains the status URL, polling kind, HTTP status, service request ID, and
`Retry-After`. The adapter recognizes `Azure-AsyncOperation`, `Operation-Location`,
and `Location`, in that order, and only follows HTTPS status URLs on the configured
ARM origin. Azure status URLs may be outside the original resource path.
See Microsoft's [asynchronous operation protocol](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/async-operations).

`Register` supplies the shared worker with the adapter's `Retry-After` parser.
The worker schedules individual reconciliation calls using that hint and its
minimum polling interval. Each
call uses GET against the saved status URL. Polling does not resend the mutation.
No polling loop sleeps inside the provider or simulation pump. Failed status
reads remain uncertain and preserve their continuation for another observation.

Custom hosts can still use `CreateOperation`, `WorldExternalOperationDispatcher`,
and `AzureResourceOperationReceipt` directly when they own scheduling.

A request whose response was lost before its receipt was persisted can remain
`Unknown`: ARM has no universal lookup by client-request ID. A 202 response
without a status URL also cannot be generically reconciled. The adapter does not
guess from resource existence or current provisioning state, which could belong
to a later operation. It does not interpret a failed GET as a failed mutation.
Resource-specific recovery belongs in a specialist provider only when needed.

Only progress metadata is retained in results. Raw resource bodies and Azure
error messages are discarded because generic actions can return secrets. Request
payloads remain in the authority-private journal and must not contain credentials.
Request and response byte limits bound JSON processing; an oversized or malformed
response produces uncertainty rather than an invented completion.

After completion, submit an ordinary phase-guarded mutation through the recorded
extension to update gameplay. A successful Azure operation and an accepted
gameplay projection are separate outcomes. Follow the shared contract's recovery,
replay suppression, and journal namespace rules before reopening live bindings.

## Delegated observations

`AzureDelegatedServices` binds the existing inventory and metrics readers to
an authenticated Entra caller. The trusted ingress validates the tenant, API
audience, `oid`, scope and expiration before supplying a request-confined user
assertion. Azure Identity performs OBO with the configured managed identity's
federated client assertion. The ARM token is used only for the downstream read;
neither assertion is forwarded to ARM or persisted. There is no fallback to host
authority when consent or authentication fails.

Token exchange, onboarding and ARM responses preserve user-interaction challenges
as `AzureDelegatedAuthenticationException`. Its optional JSON claims request is
limited to 4096 UTF-8 bytes; raw authentication headers, downstream authority and
scope overrides never leave this adapter. The HTTP transport detects challenges
before the Azure SDK can silently retry them. The ingress translates the exception
into its own authentication challenge and lets the caller obtain fresh authorization.
An ordinary permission-denied response remains a refusal.

The optional [MCP host composition](../Puck.Mcp/README.md#host-extension) installs
`puck_service_observe` when its remote configuration contains, for example:

```json
"services": {
  "managedIdentityClientId": "<silo-managed-identity-client-id>",
  "observations": [{
    "name": "storage",
    "kind": "inventory",
    "subjects": ["<allowed-user-oid>"],
    "maximumItems": 64,
    "settings": {
      "resourceGroup": "/subscriptions/<subscription-id>/resourceGroups/<group>",
      "apiVersion": "2021-04-01",
      "resourceType": "Microsoft.Storage/storageAccounts",
      "fields": { "name": "/name", "location": "/location" }
    }
  }]
}
```

Observation subjects need both MCP gateway access and this separate read
grant. `NamesFor` discloses only a subject's granted names for tool discovery;
provider settings remain private and retain the existing scope and scalar-field validation.
Reads are limited to 64 KiB per response page, eight inventory pages, 128 items,
4096 characters per field and 65536 disclosed characters overall. An incomplete
or oversized read fails rather than returning a partial snapshot. The adapter
currently uses Entra and ARM public-cloud endpoints. API permission and downstream
consent remain required; the unified Bicep already declares Azure Service
Management `user_impersonation` and adds the World identity's federated credential.
These request-scoped reads do not introduce durable jobs or cloud writes.

The same `AzureDelegatedServices` adapter accepts optional
`onboardingUrl: "https://<existing-api-host>/api/self-onboard"`, with an empty
`observations` array when only onboarding is installed. `puck_onboard` and
attachment admission call this Function endpoint using an OBO API token.
Because MCP and Functions share the API registration, this exchange requests
`<application-guid>/.default`; Entra rejects the `api://` form for this same-app
exchange with `AADSTS90009`. The delegated `user_impersonation` permission is unchanged.
The Function owns account provisioning, partition routing and protected user
escrow. Responses are bounded to 4 KiB and must name `Ready`, `Migrating` or
`Onboarding`; redirects and failed consent are refused. MCP retains no assertion
after the request; the existing Function owns its accepted escrow and expiration.
## Verification and scope

```text
dotnet test tests/Puck.World.Azure.Tests/Puck.World.Azure.Tests.csproj -c Release
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --filter FullyQualifiedName~WorldExtensionLawTests
```

The Azure suite exercises declarative provider setup and the real SDK authentication and HTTP pipeline against
a scripted in-memory service: generic verbs, unchanged JSON, pinned identities,
preconditions, pending operations, restart polling, ambiguous failures, URL
containment, and byte limits. Inventory tests cover pagination, field disclosure,
scope confinement, duplicate IDs, and refusal of truncated collections. Metrics
tests cover resource-ID validation, aggregation/interval selection, the latest-
complete-bucket scan against a recorded response shape, refusal of an omitted or
still-filling metric, and the provider's kind dispatch.
The server suite exercises durable dispatch,
continuation preservation, authority, and replay. These tests require no Azure
account and perform no live resource changes.

ARM covers the management plane. Data-plane operations such as uploading blob
contents may need their service SDK and a separately scoped provider. The Azure
extension supplies metadata to the shared client's operation catalog. The shared
configuration layer connects ordinary state request/status tables; it introduces
no Azure-specific gameplay rules. MCP transports and additional installed adapter
types remain separate from this provider.

## Documentation

📚 [Worlds and federation](../../docs/architecture/worlds.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
