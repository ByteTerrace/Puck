# Extensions and external operations

An extension may call a service, run an inference model, or use a library that
does not promise deterministic results. Puck reproduces its recorded
contributions without repeating the computation that produced them.

`IWorldExtensionRuntime` separates lifetime from contribution policy:

| Policy | Live behavior | Replay behavior |
|---|---|---|
| `Recorded` | Provider contributes through authoritative ingress | Inject recorded mutations without running the provider |
| `Recomputed` | A constrained runtime executes pinned code | Run that code with the same inputs |
| `PresentationOnly` | Local output has no authoritative contribution | Outside the state replay guarantee |

These are implemented strategies, not a switch that makes arbitrary code
deterministic. `IWorldAddonHost` extends this contract with WASM preparation
and tick methods and implements `Recomputed`. `WorldRecordedExtension`
implements `Recorded`. A package can contain more than one runtime.
The server refuses a tick-pumped host that advertises another replay policy.

## Composing a recorded provider

The hosting composition root creates `WorldRecordedExtension` with an explicit
principal, a `WorldCapabilityRequest` manifest, and a pending-contribution limit.
Use a distinct named addon principal for a provider with its own grants; this
existing principal shape also works for host-composed providers. Console
authority is appropriate only for explicitly trusted local automation.

Manifest matching is shared with WASM through `WorldCapabilityRequests.Contains`.
Requests never grant authority. `Observe` checks the requested subject and the
normal Observe grant, including state visibility. `Submit` checks identity and
requested section or row reach recursively through batches, then copies the mutation through
the strict wire codec. Mutating an input list afterward cannot change queued work.

Provider threads enqueue bounded work. The simulation pump drains it into the
ordinary `SubmissionEnvelope` door before the addon tick and pending-edit drain.
An administrative drain also consumes it while simulation is paused. Admission
still checks grants, budgets, state edit reach, phase guards, and whole-document
validation. No provider delegate or service call runs under the authority lock.
The existing mutation taps record the proposal and its acceptance or refusal.
`Submit` returns a correlation id, **not** an application verdict. Use normal
mutation outcomes and world read-back to observe application.
Recorded contributions use their own host-only connection id so their
correlations cannot collide with local console submissions.

Disposing a runtime retires its queued, not-yet-admitted contributions. Work
already admitted follows the ordinary apply rules. Replay does not restore a
provider's private process memory; its host decides whether a live restart
reconstructs, checkpoints, or resets that state.

This is an opt-in hosting API. The `addons` document section still describes
WASM modules; it does not load managed assemblies or Azure credentials.
In-process providers are trusted code. Untrusted providers need a sandbox or
process boundary, not just an interface.

## Giving a creature an external resource

Keep game semantics in authored state. A death rule can advance an ordinary
request generation and record the creature incarnation. The host maps that
request to a named service binding. A garden can render pending deletion as
dissolving ashes; a text world can print the same transition.

The host supplies a stable operation id incorporating the authority lineage,
request generation, and entity incarnation. Observing the same request again
must produce the same id. Its registered `IWorldExternalOperationProvider`
binding owns the concrete resource, operation, credentials, and payload schema.
World input names the binding, rather than choosing an arbitrary URL with host
credentials.

Each request also pins the binding's `Identity`: its concrete resource
incarnation, operation, and input schema. Dispatch and reconciliation refuse a
binding whose identity moved, so changing host configuration cannot redirect a
pending deletion to a replacement resource that reused the same binding name.

`WorldExternalOperationDispatcher.CommitAsync` writes the request and its causal
recovery image together through `WorldExternalOperationJournal`. Only committed
requests can be dispatched. The journal uses the existing `IObjectBlobStore`
local/cloud abstraction and compare-and-swap tokens, with explicit entry, byte,
and conflict limits. A full journal refuses new work instead of forgetting
deduplication history.

`WorldServer.CaptureExternalOperationCause` captures a versioned checkpoint at a
settled boundary. This is authority-private data, never a provider observation.
It retains the checkpoint engine's limits: pending edits and non-checkpointable
addon or machine state refuse capture. For those worlds,
`WorldReplayTape.CaptureExternalOperationCause` captures a versioned replay prefix
without stopping the recording, at a fully closed tick boundary. Retain the
pinned module assets alongside that recovery image. The journal accepts opaque recovery
evidence; it cannot prove that an arbitrary supplied string is sufficient.
Selecting and restoring that recovery image remains the authority host's job.

Run dispatch and reconciliation on host workers:

```csharp
var cause = server.CaptureExternalOperationCause(hostRow);
await dispatcher.CommitAsync(request, cause, cancellationToken);
var outcome = await dispatcher.DispatchAsync(request.Id, cancellationToken);
```

These calls do not automatically alter gameplay. Project the outcome into an
ordinary mutation through `WorldRecordedExtension.Submit`. Use `TransformState`
with a `PhaseGuard` when a result may apply only to its originating generation.
An external operation can succeed while its gameplay projection is refused
because the creature has been replaced. Reconcile durable operation status with
the loaded world before publishing after a restart.

## Delivery and recovery

The journal moves from `Pending` to `Dispatching` **before** invoking the
provider. Competing dispatchers cannot claim that pending request twice.
Provider results are `Running`, `Succeeded`, `Failed`, or `Unknown`.

`Running` means accepted asynchronous work, not completion. Cancellation, lost
responses, faults, and invalid provider outcomes create uncertainty. A failure
to persist an outcome leaves the earlier claim available for reconciliation.
Exception messages are not journaled because SDK diagnostics can contain secrets.

After a crash, enumerate `ReadAsync` and reconcile `Dispatching`, `Running`, and
`Unknown` entries. `ReconcileAsync` must only observe the existing operation.
It must not repeat the effect. If the service cannot establish what happened,
return `Unknown`. There is no automatic retry of an ambiguous effect or universal
exactly-once claim. Compensation or replacement is a separate authorized request
with a new id.

Identical repeated commits return the existing entry. Reusing an id with a
different binding or payload refuses. Terminal entries remain for deduplication;
dispatching one returns its status without invoking the provider. `ReadAsync`
provides operation read-back for a host console, dashboard, or state projection.

Keep this journal outside rewindable saves. Do not connect a fork to the
original live bindings or reuse its request namespace. A saved gameplay image
cannot undo an Azure deletion. Recovery must consult external history even when
the loaded image predates an operation.

## Replay and lifetime

Offline replay suppresses recorded extensions before invoking the addon factory.
Live replay revokes them before replacing the timeline and drops queued provider
contributions. Suppression remains after a drive ends or forks. The host must
explicitly call `StartRecordedExtensionEpoch` after selecting the appropriate
bindings and journal namespaces, then create fresh runtime instances. Old
instances remain revoked. Reopening is refused while replay is active or an
external call is in flight; nothing reconnects automatically to the original
resources.

An external call holds a lifetime lease, not the simulation lock. A live timeline
reset refuses while that call is in flight; isolated replay remains available.
Disposing the runtime does not erase its operations or prevent an already-sent
result from being journaled. It does prevent the retired runtime from publishing
that result into gameplay.

## Verification

`WorldExtensionLawTests` exercises the real server and tape, the directory blob
backend, and a fake external service. It covers copied bounded contributions,
authority and manifest checks, provider-free replay, duplicate dispatch, crashes
before outcome persistence, lost responses, reconciliation, and late completion.
It does not claim Azure API conformance.

```text
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --filter FullyQualifiedName~WorldExtensionLawTests
```
