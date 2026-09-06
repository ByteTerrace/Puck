# Azure resource extension

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

## Host composition

Reference this project from an extension-capable host. The base world executable
does not automatically load it, and a document's `addons` section still mounts
WASM modules. The host supplies a `TokenCredential`, using its chosen managed
identity, workload identity, or other Azure authentication arrangement.
This library never searches for credentials or selects a subscription implicitly.

Create a binding for each permitted resource operation. Bindings fix the resource
ID, its incarnation, the method, the API version, and an optional action path or
ETag precondition. `QueryParameters` carries provider-specific query arguments;
names and values are escaped, and cannot override the separately pinned API
version. Resource IDs and action paths are unescaped ARM paths, not
arbitrary service URLs. The payload is an unchanged JSON object in the selected
resource provider's schema. POST and DELETE may omit it.

The following belongs in a trusted host, after it has authorized the request.
`server`, `extension`, `journal`, and `credential` are host-owned objects from the
shared extension contract. Resource selection and API version come from the
host's service configuration; the operation ID comes from the world request's
authority lineage, entity incarnation, and generation.

```csharp
var binding = new AzureResourceBinding(
    Name: "creature.delete",
    ResourceId: configuredResourceId,
    Incarnation: resourceIncarnation,
    Method: AzureResourceMethod.Delete,
    ApiVersion: configuredApiVersion);

using var provider = new AzureResourceOperationProvider(credential, binding);
var dispatcher = new WorldExternalOperationDispatcher(extension, journal,
    new Dictionary<string, IWorldExternalOperationProvider> {
        [binding.Name] = provider,
    });

var request = provider.CreateOperation(stableRequestId);
var cause = server.CaptureExternalOperationCause(hostRow);
await dispatcher.CommitAsync(request, cause, cancellationToken);
var outcome = await dispatcher.DispatchAsync(request.Id, cancellationToken);
```

For a POST action, set `Method` to `Post` and `Action` to the resource-relative
action name, such as `start`. For PATCH or PUT, pass the JSON object as the
second argument to `CreateOperation`. Each operation binding has its own identity;
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
code a dispatcher containing bindings it should not invoke.

## Completion and recovery

Run service work on host workers. The dispatcher claims each operation durably
before sending it. The SDK's automatic transient retries are disabled, and the
default HTTP transport does not follow redirects. The client-request ID is a
correlation aid, not an Azure idempotency guarantee.

An asynchronous response produces a durable `AzureResourceOperationReceipt`.
It retains the status URL, polling kind, HTTP status, service request ID, and
`Retry-After`. The adapter recognizes `Azure-AsyncOperation`, `Operation-Location`,
and `Location`, in that order, and only follows HTTPS status URLs on the configured
ARM origin. Azure status URLs may be outside the original resource path.
See Microsoft's [asynchronous operation protocol](https://learn.microsoft.com/en-us/azure/azure-resource-manager/management/async-operations).

The host schedules individual `dispatcher.ReconcileAsync` calls, respecting
`Retry-After` or its own bounded polling policy when that header is absent. Each
call uses GET against the saved status URL. Polling does not resend the mutation.
No polling loop sleeps inside the provider or simulation pump. Failed status
reads remain uncertain and preserve their continuation for another observation.

```csharp
var receipt = AzureResourceOperationReceipt.Parse(outcome.Result);
// Schedule according to receipt.RetryAfter, then on a host worker:
outcome = await dispatcher.ReconcileAsync(request.Id, cancellationToken);
```

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

## Verification and scope

```text
dotnet test tests/Puck.World.Azure.Tests/Puck.World.Azure.Tests.csproj -c Release
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --filter FullyQualifiedName~WorldExtensionLawTests
```

The Azure suite exercises the real SDK authentication and HTTP pipeline against
a scripted in-memory service: generic verbs, unchanged JSON, pinned identities,
preconditions, pending operations, restart polling, ambiguous failures, URL
containment, and byte limits. The server suite exercises durable dispatch,
continuation preservation, authority, and replay. These tests require no Azure
account and perform no live resource changes.

ARM covers the management plane. Data-plane operations such as uploading blob
contents may need their service SDK and a separately scoped provider. The Azure
extension does not add an MCP server, managed-assembly loader, agent tool catalog,
or automatic world-state watcher; those remain host composition concerns.
