# Hosting service extensions

Host operators can use [declarative composition](ExtensionConfiguration.md) in
the normal executable. This guide owns the underlying C# hosting API used by
that composition and by custom hosts.

`WorldExtensionHost` gives a host one place to register service operations,
grant callers access, and run durable work. A caller receives a
`WorldExtensionClient`, which can discover its permitted operations, invoke
them, read their status, observe permitted world data, and submit ordinary
gameplay mutations. The same client API can sit behind a script bridge or an
AI harness adapter.

## Compose once, grant explicitly

The composition root owns credentials, provider instances, private storage,
and causal recovery capture. Register providers explicitly in code. Neither
world documents nor writable directories discover or load managed assemblies.

```csharp
var journal = new WorldExternalOperationJournal(store, privateTarget,
    new(worldId, "external/operations.json"),
    maximumEntries: 1024, maximumBytes: 16777216, maximumConflicts: 8);

await using var host = new WorldExtensionHost(server, journal, authorityLineage,
    registrations, () => server.CaptureExternalOperationCause(hostRow));

using var client = host.CreateClient(WorldPrincipal.Addon("creature-controller"),
    allowedOperations: ["creature.delete"], worldRequests: manifest);
host.Start();
```

Each `WorldExtensionOperation` registers a description, input JSON schema,
provider, and request factory. The factory validates input and binds it to the
given operation ID, registration name, and provider identity. Discovery metadata
describes input; it does not replace factory validation. An optional polling
delay callback interprets service receipts. The
[Azure provider](../Puck.World.Azure/README.md) supplies these through `Register`.
Provider results must be safe for the authorized caller to see; the host excludes
the private recovery image but cannot sanitize arbitrary provider-defined data.

Only the root creates clients. It authenticates their seat, console, addon, or
peer principal and grants exact service operation names. World capability
requests and world grants independently govern observations and contributions;
a world grant never implicitly grants cloud access. To change a client's policy,
dispose it and issue a replacement. Old handles remain revoked.

An optional [storage namespace](../Puck.Storage/README.md) supplies private
persistence without paths, target credentials, or an arbitrary object selector.
Passing it to `CreateClient` transfers its disposal lifetime to that client.
Even a retained storage handle becomes unusable when replay suppresses the client.

## Invoke and observe

Caller code needs only the granted client:

```csharp
var available = client.Discover();
var operation = await client.InvokeAsync("creature.delete",
    requestKey: "creature-17/incarnation-3/death-1",
    cancellationToken: cancellationToken);
var progress = await operation.ReadAsync(cancellationToken);
```

Invocation durably queues work; it does not wait for the service to finish.
The host derives the operation ID from the authority lineage, authenticated
principal, operation name, and stable request key. Repeated identical requests
return the existing operation. Changing the input or pinned binding while
reusing a key refuses. After a timeout or restart, use
`client.GetOperation(name, requestKey).ReadAsync()` to resolve uncertain admission.
A missing result means no committed request was found at that read.

Caller read-back contains status and provider progress, never the journal's
recovery image. A successful service call does not itself change gameplay.
Project the result with `client.Submit` and an ordinary phase-guarded mutation
that checks the originating creature incarnation and request generation.
That submission returns a correlation ID; normal mutation outcomes determine
whether gameplay accepted it.

## Worker and recovery ownership

`Start` owns dispatch and polling outside the simulation pump. A host with an
existing service scheduler may call `RunOnceAsync` instead; worker passes
serialize. Options bound concurrent submissions, concurrent external calls,
input bytes, client identities, polling cadence, and cancellation deadlines.
The default ceilings are eight submissions, four service calls, 64 KiB inputs,
and 32 distinct client identities per host lifetime. Replacing a revoked client
for the same identity reuses its slot. The journal separately bounds retained
operation history and refuses admission when full.

Pending requests are claimed before execution. Recovered `Dispatching`,
`Running`, and `Unknown` requests are reconciled without resending the effect.
Polling respects the provider's delay and the host's minimum interval; a restart
waits the retained delay again before polling. `LastFailure` exposes the last
worker exception's type without its potentially sensitive message.

Disposal revokes clients, cancels service calls, and drains the worker. Providers
are borrowed and remain the root's disposal responsibility. In-process providers
are trusted code and must cooperate with cancellation. An interface and timeout
cannot contain hostile code; untrusted third-party execution requires a separate
OS process or an appropriate sandbox with explicitly mediated capabilities.

Keep one host and journal per live authority lineage. Recovery capture must
occur at a settled boundary; worlds that cannot checkpoint use the replay-prefix
capture described in [the external-operation contract](Extensions.md).
After replay, explicitly select safe bindings and history, reopen the recorded
extension epoch, and create fresh clients. Forks require separate namespaces
and bindings. No live cloud connection resumes merely because a save was loaded.

`WorldConfiguredExtensions` supplies the configurable state-table connector
described in [declarative composition](ExtensionConfiguration.md). Custom
adapters, including MCP transports, can map other triggers onto this client API;
they must not expose provider objects, credentials, or the private journal.
`WorldExtensionHostLawTests` verifies caller isolation, replay revocation,
bounded admission, worker startup, and recovery without repeating effects.
