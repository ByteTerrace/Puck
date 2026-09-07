# Puck MCP: adversarial review and implementation plan

Reviewed and revised after technical rebuttal on 2026-09-07 against checkout base
`621127f0b67a`. This is a future
implementation handoff, not a claim that an MCP server is implemented. Source
links are repository-relative so the plan survives another machine or checkout.

**Decision: deliver live local pairing first, with explicit Operator and Participant
profiles.** A trusted co-developer gets the same console control plane as the
human at the terminal. An embodied participant gets the scoped bridge. Remote
cloud deployment has additional boundaries and is a later, independent release.
Body grants do not authorize host files, composed images, or Azure resources;
that fact must not become a reason to restrict an explicitly trusted operator.

## 1. Findings that change the design

| Priority | Finding and evidence | Required correction |
|---|---|---|
| P0 | Giving a participant arbitrary `puck_exec` would expose host operations beyond body grants. [Recording commands](../../src/Puck.World/WorldRecordingCommandModule.cs) open paths and devices without consulting an acting principal; [replay commands](../../src/Puck.World.Console/WorldReplayCommandModule.cs) manage the session tape directly. | Participant has no exec. Explicit local Operator uses Console identity and the full existing command registry, without an MCP allowlist or per-verb approval layer. |
| P0 | `puck_doc.write` equates a `Mutate` check with permission to replace a disk file. Puck's [mutation authority](../../.agents/skills/puck-world/references/authority.md) instead checks section/row scope, mutation-kind masks, additional state `Edit`, and dispatch budgets. | Separate live authoritative mutations from offline document persistence. Neither may bypass the other's admission or storage boundary. |
| P0 | Azure delegation is asserted but not composed. [World.Azure](../../src/Puck.World.Azure/README.md) explicitly separates host binding grants from world grants and Azure RBAC. Its normal configured credentials are managed identity or Azure CLI, not automatic per-user OBO. | Build a separately authenticated remote host and scoped extension-client path. Never fall back from delegated credentials to a broader host identity. |
| P1 | There is no world connection/lifecycle design. [AgentBridge](../../src/Puck.World.AgentBridge/README.md) uses an in-process principal-aware link and a mailbox drained by the host; the base World executable does not install it. Adding a CLI switch does not supply those objects. | First prove the local console attachment and independent adapter lifecycle; compose the participant bridge in its own milestone. Text queueing is reusable, while IPC still needs implementation. |
| P1 | The plan calls four ingress kinds the entire principal model and proposes `WorldPrincipal.Peer(oid)`. [WorldPrincipal](../../src/Puck.World.Schema/WorldPrincipal.cs) also models Document, World, and Group; `Peer` takes an index and positive admission generation. | Authenticate the external caller separately, then resolve an admitted world principal and body incarnation. Never cast a cloud subject into a body or seat. |
| P1 | [WorldCostReport.Generate](../../src/Puck.World.Schema/WorldCostReport.cs) currently marks recurring work unmodeled, search reservations unresolved when present, and admission false. It does not provide a calibrated integer runtime estimate. | Return those facts intact. Separate cost-model coverage, structural validation, and actual host admission. Do not make calibration part of the MCP adapter. |
| P1 | [Action receipts](../../src/Puck.World.AgentBridge/WorldAgentContracts.cs) carry local correlation, not dispatch status or an authorization/application verdict. | Preserve submission uncertainty. Add authoritative completion plumbing only with evidence from the server; an observed pose alone cannot attribute a particular request. |
| P1 | A screenshot requests the **next** composed frame, can be busy, and needs a renderer. [WorldUiCommandModule](../../src/Puck.World/WorldUiCommandModule.cs) does not produce bytes synchronously. A composed viewport can include other seats, overlays, and external screens. | Operator gets one awaitable call returning the completed PNG. Participant gets no framebuffer tool. Remote capture requires explicit disclosure authority. Internal asynchrony does not require client polling. |
| P1 | The verification plan pins `2024-11-05` and treats Azure transport as SSE. The [2026-07-28 transport specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports) uses stdio and Streamable HTTP with per-request metadata; the current era no longer requires the old initialize exchange. | Pin a tested protocol/SDK/client matrix. Do not implement an old handshake and label it current compliance. |
| P1 | “No tests” contradicts the [agent project's documented focused suite](../../src/Puck.World.AgentBridge/README.md) and [Azure suite](../../src/Puck.World.Azure/README.md). A build and five happy-path messages cannot establish the security boundary. | Use focused adapter/authority tests and real executable canaries. Keep quarantined Post out of the plan. |
| P2 | Capture promises exceed the listed tools: parity and device ingress have no operations; `verify` is mentioned but absent from the capture action enum; `press` omits its value; “five” primitives enumerates six. | Publish exact schemas, defaults, legal combinations, availability, and separate release gates. |

Two additional findings from the second technical review affect the first release:

- Desktop [WorldSingleWaitGateResolver](../../src/Puck.World/WorldSingleWaitGateResolver.cs)
  returns a shared wait gate. [Launcher registration](../../src/Puck.Launcher/LauncherServiceRegistration.cs)
  installs it as `TextCommandSource.HoldGate`, which stops the whole source.
  Separate sessions alone do **not** establish independent human input during
  `world.wait`. Make waits session-scoped as part of local pairing and verify
  human commands during an MCP wait; retain the existing tick semantics.
- [WorldRenderProbe](../../src/Puck.World/WorldRenderProbe.cs) only holds render
  references. Actual PNG writers are `UnifiedOverlayNode`, `FullscreenPassNode`,
  and `SdfEngineNode`. A success-only `Action<string>` event added to the holder
  would not establish completion across that chain, report failures, or prove
  that a preceding deferred edit reached the captured frame.

The native-capture citation also moved: the service is now in
[Puck.Platform.Windows](../../src/Puck.Platform.Windows/Win32NativeImageCaptureService.cs).
Its [feed](../../src/Puck.Platform.Windows/Win32GraphicsCaptureFeed.cs) supports a
CPU transport and an optional shared-GPU transport. That does not imply zero-copy
PNG output over MCP. Recording negotiates available factories, drops and counts
frames under pressure, and has distinct wall and simulation clocks; simulation
clock recording forbids audio rows. Replay tapes have no guaranteed kilobyte size
and can refuse recording after addon execution, machine stepping, or screen
operations. Preserve these constraints rather than treating every live session
as recordable. [Recording contract](../../src/Puck.Recording/README.md),
[replay implementation](../../src/Puck.World.Console/WorldReplayCommandModule.cs).

## 2. Product and trust contract

The launch/host configuration selects one of two profiles. The host authorizes
the selected endpoint/profile; an untrusted client requesting Operator does not
grant itself Console. Tool arguments cannot switch profile or supply a new acting
principal. Publish both recipes explicitly; do not infer trust from a model name
or from network loopback alone.

| Profile | Identity and tools | Boundary |
|---|---|---|
| `OperatorProfile` | `CommandPrincipal.Console`; full `puck_exec`, direct frame capture, and document/recording conveniences as they land. No body binding required. | Explicitly trusted local co-developer, with the same existing console behavior and process/OS permissions as the human operator. No per-verb MCP allowlist, no approval ceremony for each material or parameter adjustment. |
| `ParticipantProfile` | Host-issued principal/body binding; `puck_affordances`, `puck_observe`, `puck_act`, all through `WorldAgentBridge`. | No console, raw files, full viewport, session tape, or host devices. The server checks live grants. Other legitimately granted gameplay capabilities are not removed from the principal, but these three tools expose only perception and actuation. |

An operator can use `world.row.step`, inspection verbs, and any other registered
console command on the same terms as terminal input. The profile itself grants
console access; it is not a promise of filesystem sandboxing. Existing engine
validation, world admission, and OS restrictions still apply. Client-side approval
settings remain the client's concern. MCP does not depend on AgentHarness or a
model provider and does not implicitly inherit Harness approval behavior.

A participant **binding** identifies the authority lineage, principal, and body
incarnation. Invalidate it on revocation, body replacement, peer readmission,
transfer, or lineage change. Refresh live channels on definition reload and
recheck validity. Do not silently follow a reused body index. Operator connections
instead identify the chosen running host and Console session; replacing a body
does not revoke a co-developer's console.

Local disconnect releases the connection and pending waits, not the user's world.
Reconnect to the same verified host without silently retrying uncertain commands.
An explicit world reload or quit submitted by the operator retains its ordinary
meaning. HTTP deployment later authenticates each request and checks every
binding/artifact against the caller; it does not expose the local Console endpoint
to the network. Host extension grants and Azure RBAC remain independent of world
grants. Those remote requirements do not gate local pairing.

## 3. Hosting and project layout

Choose **attachment to the human's running world** as the first usable workflow:

```text
human-visible Puck.World
  -> opt-in local control endpoint -> dedicated TextCommandSession -> ordinary pump
IDE MCP client
  -> puck mcp --profile operator --attach <pipe> -> local control endpoint
```

The example is proposed CLI syntax. An optional `--control-pipe <name>` deployment
override is reasonable, but must not be the only way to enable attachment: provide
live start/stop/status control through the ordinary console so an already-running
world can opt in. Keep durable settings in host configuration; avoid a second
ambiguous `--control` alias unless the normal CLI convention needs it. All these
names are proposed, not existing flags or verbs. No mandatory headless boot or
restart of the world for an IDE reconnect. MCP stdin/stdout belong to the CLI
adapter, while World retains its human console and window. The adapter's EOF or
crash does not terminate World. An owned-host launch recipe can be added for
automation, with explicit ownership and shutdown behavior; it is not required to
make live pairing work.

On Windows, prefer a named pipe scoped to the current OS user, rejecting remote
clients, with a host-instance identifier and protocol revision handshake. Use
the supported .NET pipe options/ACLs and verify both ends: `CurrentUserOnly` on
Windows also checks elevation level. A name containing the username is not an
ACL, and current-user access alone is not proof that remote clients are rejected.
Enforce that separately and test it. First-instance protection, host-owned
discovery, and an incarnation ID distinguish the intended host; self-reported
IDs alone do not authenticate it. This trusts the local OS user, not individual
processes running with that user's privileges. [PipeOptions](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0),
[Windows named-pipe flags](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-createnamedpipea).
Resolve
the intended endpoint through host-owned discovery and verify its identity; do
not attach to an arbitrary reused name. Unix-domain sockets with user-only access
are the corresponding portable option. A loopback TCP alternative requires an
explicit authentication secret; a port number is not permission to be Console.
These are local transport controls, not an Entra/OBO project.

`TextCommandSource.CreateSession` supplies queueing, identity, and result routing;
it does not already supply IPC. An existing terminal's stdin cannot generally be
retrofitted into a shared writable pipe. Add a thin host-owned endpoint feeding a
dedicated Console session with request-correlated replies. Preserve per-session
drain barriers and callback ordering; implement session-scoped waits to retain
independent human input. Bound the
endpoint's queue before it reaches the existing unbounded text queue. Do not
scrape a shared console log to guess which reply belongs to which request.

Use a small versioned UTF-8 JSON envelope for private IPC, with an explicit
bounded frame size, request ID, operation, and payload. Newline-delimited JSON
with JSON-escaped strings is sufficient; raw `[id]\t[status]\t[output]` is not
defined for tabs or multiline output. Handle partial reads, multiple frames in
one read, duplicate IDs, oversized input, and serialized writes. This private
console/capture protocol is not a second implementation of MCP in World.
For initial exec, admit one command at a time per session and correlate its
callback to the envelope, not to command text. Reject blank/comment-only commands
before enqueueing because the source drops them without a result callback.
Literal multiline batches need a separately defined split/aggregation contract;
do not accidentally interpret them as several IPC requests.

Proposed ownership:

- `Puck.World.Mcp`: optional protocol adapter, schemas, result mapping, binding
  validation, and adapters over injected authorized services. Keep Azure SDK,
  native GPU types, and model frameworks out of its core dependency closure.
- World owns the transport-neutral local console/capture endpoint and its ordinary
  pump integration. This endpoint needs no agent or MCP package reference. An
  optional agent-capable composition above World installs `WorldAgentBridge` and
  its mailbox for Participant. Do not copy `WorldBootComposition`, create another
  simulation loop, or use `InternalsVisibleTo`.
- `Puck.Cli`: command routing, MCP stdio, local attachment, and exit status.
  Other CLI verbs retain their existing behavior. Keep framing logic in one owner.
- A later ASP.NET Core host owns HTTP authentication, request identity resolution,
  OBO, and world-owner routing. Native capture remains in a suitable desktop/render
  worker, not presumed available in every Azure Functions/App Service instance.

The base World, Schema, Protocol, Server, Client, Console, and Addons projects
retain independence from agent/MCP packages. First settle and build the local
endpoint's neutral contracts and architecture profile. Extract public hosting
seams only where participant composition or owned-host automation needs them;
that extraction is not a prerequisite for attaching the operator console.

Evaluate the [official C# SDK](https://github.com/modelcontextprotocol/csharp-sdk)
in milestone 0, beginning with `ModelContextProtocol.Core` (2.2.0 is the inspected
candidate, not a requirement to use that version regardless of later evidence).
Measure the actual
dependency delta, startup/working set, publish size, target-client interoperability,
and resolved architecture build. HTTP's ASP.NET Core package is separately
packaged; do not presume it is mandatory for stdio. Pin the chosen package/version
only after evaluation. A small repository-owned implementation is an explicit
alternative if the SDK is a poor fit; choose one implementation, not two stacks.

The decision needs more than restore plus `puck architecture`:

1. Restore in an isolated spike using the repository SDK/settings; inspect the
   package graph and version changes. Adding a package necessarily adds lock-file
   entries. Preserve unrelated pins, review required changes, then verify a
   reproducible locked restore. An offline feed or expected lock-file change is
   not evidence of SDK incompatibility. [NuGet lock-file behavior](https://learn.microsoft.com/en-us/nuget/consume-packages/package-references-in-project-files#locking-dependencies).
2. Build the affected integration projects in Release with the resolved-reference
   architecture gate enabled. `puck architecture` remains a useful report, not a
   substitute; its [implementation](../../src/Puck.Cli/Architecture/ArchitectureCommand.cs)
   states that distinction explicitly.
3. Run the actual pairing client through tools/list, exec, image return, errors,
   cancellation, EOF, and reconnect for the chosen protocol revision; measure
   the dependency/runtime cost. A package restoring on .NET 10 proves neither
   protocol compatibility nor correct integration.
4. Choose the SDK if that evidence is satisfactory. If not, diagnose the failure
   and evaluate the smallest remedy against the cost of an owned implementation.
   A native implementation must pass the same wire/lifecycle tests before it can
   be selected. There is no automatic fallback to an assumed 150-line server.

That alternative must implement the advertised protocol revision's framing,
discovery/lifecycle, request IDs, notifications, cancellation, errors, schemas,
content blocks, output serialization, and input/resource limits, with real-client
interop and malformed/concurrent-input tests. Approximately 150 lines can frame
JSON-RPC; it is not an estimate for a complete production MCP server. Do not add
unneeded protocol capabilities merely to match the SDK's breadth.

For stdio, adapter stdout contains only protocol messages, from startup through
shutdown. Send its diagnostics to stderr and serialize concurrent replies. The
attached World retains its existing streams. EOF cancels adapter waits and closes
IPC; it leaves World and host-owned recording/replay state running. Report uncertain
already-dispatched work honestly. Only an explicitly owned-host recipe disposes
its world at adapter shutdown.

Evaluate `2026-07-28` first, but select a revision supported by the actual pairing
client and chosen implementation before promising compatibility. Its
per-request metadata and optional discovery replace the old handshake model;
Streamable HTTP has request metadata headers and request-scoped streaming.
If a required client only supports an older revision, document and test that
specific tested mode rather than adding speculative compatibility code. The
[release announcement](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
and [transport specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports)
are the protocol baseline to recheck at implementation time.

## 4. Tool contracts

Use closed argument objects and operation-specific validation. A single tool
with several actions is acceptable only if the schema and runtime reject every
invalid combination. Do not optimize for an arbitrary tool count at the cost of
misleading descriptions or incompatible read-only/destructive annotations.

| Tool | Contract |
|---|---|
| `puck_affordances` | Returns the bridge's principal, body index, Observe/Drive verdicts and authority text, plus live channel names, ordinals, shapes, and motion-role flags. Host metadata separately describes available tool surfaces and limits. Results are advisory, not a cached authorization token. |
| `puck_observe` | Requires exactly one of pose/channels/state/targets/contacts/properties. Return the complete typed observation, including `Refused`, not just text. Preserve the bridge's body-coordinate translations. Do not invent a coherent tick or coordinate-frame stamp absent from the source result. |
| `puck_act` | Closed move/press/stop union. Move requires positive duration; omitted axes default to zero. Press requires exact channel name, defaults value to 1, and defaults omitted hold duration to the bridge's single-host-step behavior. Stop accepts no motion fields. Every mutation takes a binding-scoped request key. |
| `puck_doc` | Operator convenience over existing authoring contracts. Distinguishes live mutation from offline file editing; read, validate, cost, mutate, and save have explicit meanings. Local Operator may use paths under its ordinary OS permissions. Participant has no document tool. Remote scoped authoring, if added, uses constrained handles. |
| `puck_capture_frame` | Operator requests the current composed view in one call. Internally arm, await completion without blocking the pump, read/encode off-pump as needed, and return PNG image content directly. No start/status/download loop on the success path. |
| `puck_capture` | Recording/start,stop,status; replay/start,stop,cancel,status,verify. Local controls address the host's existing recorder/tape, with instance/generation metadata to reject stale requests. Long verification/parity jobs may return handles; a generic job scheduler is not required for ordinary start/stop/status. |
| `puck_exec` | Operator only. A fixed Console `TextCommandSession` exposes the full existing registry with its parser, ordering, callbacks, and barriers. No MCP allowlist for old or new verbs; ordinary engine errors and authority checks remain. The tool evaluates Puck console text, not a new OS-shell API. |

The existing [TextCommandSource.CreateSession](../../src/Puck.Commands/TextCommandSource.cs)
already provides stamped command sessions. Use a dedicated Console session for
operator reply attribution and independent barriers. The default administrative
enqueue path has Console identity too, but lacks a dedicated connection's result
routing. Participant movement uses the bridge; Operator retains normal console
movement commands without being forced through a participant profile.

Define conservative host limits for input bytes/depth, finite numeric values,
duration, pending actions, action-queue horizon, response bytes, artifacts,
capture duration, and request timeouts. Reject durations that underflow to zero
when converted to the bridge's float representation. Reject overflow before
fixed-point conversion. The server still owns channel shaping, grant ceilings,
and liveness. Document exact duration conversion and effective tick semantics;
wall-clock deadlines never enter deterministic simulation state.

The bridge's affordance reads are several operations, not an atomic grant/channel
snapshot. Either expose them as advisory as today, or add a genuine revisioned
snapshot at the owning seam. An action resolves channels at dispatch and receives
the server's live grant checks regardless of earlier discovery.

## 5. Ordering, results, and bounded execution

For Participant, serialize mutations per binding in admission order, reusing
mailbox capacity and per-frame drain limits. For Operator, preserve the existing
text session's FIFO and simulation/read barriers; bound IPC admission before
enqueueing. Preserve independent human input without pretending the entire world
is locked to the agent. Report overflow without silently dropping actions.

In the current [CommandRegistry](../../src/Puck.Commands/CommandRegistry.cs),
queueing a Simulation-routed command returns `CommandResult.None`; the eventual
handler can refuse later. The [session callback](../../src/Puck.Commands/TextCommandSource.cs)
therefore cannot universally mean "applied successfully." IPC/MCP must distinguish
submission from execution results. Carry a request correlation through deferred
dispatch if returning an authoritative command verdict; do not infer it from an
empty output or scrape the shared error counter. Preserve existing `IsError`,
`Output`, and `ClearTranscript` fields where they are available.

Frame capture after exec must enter the same session ordering/barrier before it
arms the render request. An out-of-band mailbox capture can otherwise overtake
the preceding mutation or `world.wait`. Do not solve this with a fixed sleep:
reuse the barrier. Capture completion proves a frame exists; it does not prove
the prior edit was accepted or prevent subsequent human edits entering that frame.

For each request key, retain the request digest and result for a bounded binding
lifetime. Identical retries return the same receipt; changed input with the same
key refuses. Expired or lost binding state requires rebinding and never silently
resubmits an old mutation. Do not advertise exactly-once execution across process
crashes. Local exec carries correlation in the IPC envelope without making the
model manage a job handle for every command. Do not automatically retry an exec
after an uncertain disconnect, even when the command seems harmless. The durable
external-operation host has its own stronger request-key and recovery contract;
reuse it for cloud effects.

Distinguish invalid arguments, denied access, stale binding, busy/overload,
unsupported capability, timeout, submitted, completed, and unknown outcome.
Follow the selected protocol's protocol-error versus tool-execution-error rules.
Return structured fields plus concise text. Decimal-encode 64-bit correlation
IDs, ticks, and hashes when JSON client number precision would lose information.

Cancellation can remove queued mailbox work. Once dispatch has begun, client
cancellation does not roll back an input, mutation, recording, or cloud request.
When the protocol still permits a response, report an honest status or unknown
outcome and never retry automatically. Explicit cancellation may prohibit further
messages for that request: follow the selected revision, retain internal outcome
state for a later permitted query, and do not send a late "cancelled" or "unknown"
reply. The current [stdio specification](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/stdio)
requires this distinction. A lost connection does not make a mutation safe to resend.
Stop is a real authority-checked action that clears the body's tape and held
channels, potentially including another controller's contributions. Disconnect
cleanup must not issue an unannounced privileged Stop; finite action duration
bounds residual movement. Any host-owned drive lease cleanup must use its own
explicitly defined ownership semantics.

Keep MCP/IPC serialization, network waits, and still-result waiting off the pump.
Existing console verbs retain their current execution semantics; exposing the
console does not require first refactoring every synchronous handler. Typed
recording/replay conveniences should move expensive finalization/verification to
bounded workers through the owning subsystem when safe. Capture immutable inputs
on the owning thread and marshal short state transitions back. Report remaining
pump stalls rather than claiming all console commands are nonblocking. A paused
or zero-rate world needs a named outcome for simulation-progress waits;
cancellation remains responsive.

## 6. Documents and cost reporting

Local pairing first uses existing console authoring verbs, including normal
validation, admission, journal, and replay behavior. No new revision protocol is
required before a co-developer can step a material parameter. Human and agent
edits enter the ordinary ordered authority path; neither implicitly owns a lock.

The later `puck_doc` convenience distinguishes live writes from offline saves.
Whole-document read-modify-write operations should carry an expected revision or
content hash to avoid overwriting concurrent edits; enforce live preconditions at
application, not just argument parsing. This is a requirement for that new
replacement operation, not an MCP wrapper changing every existing console verb.
A save atomically persists a validated candidate and does not imply the world
installed it. A live mutation does not imply the file was saved. Report both
outcomes when one operation requests both.

Reuse deserialization, import resolution, extension preservation, and whole-
document validation. Local Operator file paths retain normal host/OS semantics;
do not claim confinement while also granting a console that can read/write paths.
For a future remote or explicitly restricted authoring service, use host-issued
document handles and enforce containment across imports/assets, including reparse
points, UNC/device paths, and alternate streams. Bound bytes/depth/count and
authorize document disclosure there. Those restrictions are not a prerequisite
for trusted local console editing.

`world.cost [json]` is a useful independent console addition if shared inspection
needs it. Snapshot the addressed live definition through existing instance routing,
compute `WorldCostReport.Generate` off the pump, and return model ID, issues,
unmodeled bounds, heuristic work units, and admission status without alteration.
Keep `Admitted=false` distinct from “validator rejected this document.” Never
convert heuristic weights into cycles or claim a wall-clock performance estimate.

## 7. Capture roadmap and artifact contract

**Single stills have a direct success path:** call `puck_capture_frame`, receive
the PNG. The handler arms the existing next-frame capture and awaits an internal
completion signal with cancellation and a deadline. Use a request-correlated
completion seam at the render owner; a `pending` console echo or mere file
existence is not completion. Read only completed bytes using a unique temporary
destination if the existing path requires a file, then clean it up. Return an
image block with actual media type/size and available frame metadata. No persistent
artifact registry, poll handle, separate download, or assumed 50 ms deadline is
required for a successful small still. Bound image dimensions and payload bytes.

Extend the neutral [ICaptureRequestTarget](../../src/Puck.Abstractions/Presentation/ICaptureRequestTarget.cs)
contract with a request-correlated terminal result, forwarded unchanged through
decorators. Update all current implementers:
[UnifiedOverlayNode](../../src/Puck.Overlays/UnifiedOverlayNode.cs),
[FullscreenPassNode](../../src/Puck.Shaders/FullscreenPassNode.cs), and
[SdfEngineNode](../../src/Puck.SdfVm/SdfEngineNode.cs). Signal success only after
the serving node's PNG write is complete; signal failure on unavailable capture,
readback/write failure, and shutdown. The current code clears a pending path
before attempting the write, so clearing that path is not a success signal.
Preserve the one-pending-request behavior and arbitrate check-and-arm on the
owning thread across screenshots, parity captures, and MCP.

Prefer one completion per request over a global success-only event keyed by path.
A `WorldRenderProbe` event may forward a typed terminal result, but it cannot be
the sole place completion is invented. Complete a task with asynchronous
continuations, or enqueue a short notification; never run arbitrary MCP callback
work inline in the render pass. Define cancellation/late completion cleanup and
prevent a cancelled request from deleting or completing a newer capture. This
orders a result after actual work; it does not make GPU/file completion timing
deterministic or eliminate the existing synchronous readback cost. Reuse the
same signal for the parity scheduler where practical rather than retaining
contradictory completion definitions.

Recording/replay outputs may use local paths accessible to the operator. Long
verification/parity work can use operation handles and status. Remote artifacts
later need owner-bound IDs, authorized retrieval, expiry, retention limits, and
integrity/provenance metadata. Avoid unbounded base64 for large media and unusable
remote server paths. MIME type and extension must match the actual container;
do not promise H.264 as standards-conforming WebM. Do not fabricate provenance.

- **Stills:** Operator captures the composed view the human sees. Participant
  exposes no frame tool. Return busy when the existing pending slot is occupied;
  fail clearly without a renderer, after device loss, or on timeout. Internal
  asynchronous rendering does not impose a client-side state machine. Cancelled
  capture cleanup must not overwrite another request or return stale bytes.
- **Recording:** reuse the recording document and negotiated factories. Report
  actual codec, audio tracks, drops, synchronous GPU readback cost, and failures.
  Local Operator intentionally controls the same recorder as the human, so may
  inspect or stop a human-started recording. Validate host/capture generation to
  avoid stopping a later recording by stale handle. Host limits and existing
  device consent still apply. Remote recording additionally requires caller/source
  grants. Crash-safe flushed clusters do not equal a cleanly finalized artifact.
- **Replay:** preserve the tape's current arming refusals and name/storage rules.
  Verification runs against an isolated world on bounded workers and compares
  against the recorded live result. Export is host-authorized because the tape
  contains more than the bound body's observations. Local Operator can invoke
  drive/fork through the full console, preserving its live-session reset semantics.
  Participant cannot export or control the host tape.
- **Parity:** [captures rows](../../src/Puck.World.Schema/WorldCaptures.cs) are
  boot-authored schedules, not mutable camera endpoints. A parity job boots the
  approved fixture in isolated output/state directories and invokes the existing
  `puck parity` workflow. Its manifest/hash alone is not a pass: require the content,
  state, and pixel-comparison verdicts from the real runner.
- **Ingress:** distinguish acquiring a desktop/camera source from exporting a
  rendered world image. Expose only host-approved source handles with explicit
  consent and revocation; no arbitrary window-title search or monitor enumeration
  for an embodied caller. Surface unavailable devices/platforms honestly. Do not
  claim deterministic replay of external video/audio unless those inputs are
  actually recorded and replayed by a supported mechanism.

## 8. Remote hosting and Azure delegation

This is a separate deployment milestone, not a prerequisite for local pairing.
Add Streamable HTTP only after the local authority and lifecycle gates pass.
Follow the selected revision's [authorization requirements](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization),
including protected-resource discovery, audience validation, and authentication
on every request. Validate signature, issuer, tenant, expiry, audience, and
required delegated scopes. Enforce TLS, appropriate Origin/Host checks, bounded
requests, and caller-specific caches. Never trust protocol clientInfo as identity.

Resolve a tenant-qualified subject such as `(tid, oid)` through trusted admission
and host policy. Bind it to the world principal/index/generation and approved
body; a signed-in user is not automatically a Console or local Seat. Requests
route to the owner of that live authority lineage. Stateless MCP transport does
not make an in-memory world safely replaceable by another HTTP replica.

Use OBO only for a valid user access token intended for this middle-tier API;
obtain a distinct downstream ARM token. Configure confidential-client credentials,
consent, scopes, tenant/cloud, and user-isolated token caching. App-only callers
need a separately specified authentication mode. Token passthrough and fallback
to Azure CLI/managed identity are forbidden in the delegated profile.
[Microsoft OBO contract](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-on-behalf-of-flow).

Expose Azure through the existing
[WorldExtensionClient](../../src/Puck.World.Server/ExtensionHosting.md): discover
only granted bindings, invoke by stable request key, and read progress. Use
explicit `puck_operation` discover/invoke/status operations or generated tools
from granted binding schemas; do not hide service mutations inside body actions.
The host pins resource, incarnation, method, API version, and preconditions.
Keep provider objects, raw credentials, and private journals inaccessible.

Preserve durable claim-before-send, no blind mutation resends, unknown outcomes,
safe polling, and replay suppression already supplied by the extension system.
OBO credential expiry during a durable job needs a defined reauthentication and
recovery policy before release: never persist a raw user assertion into a replay
or operation journal, and never resume as another user after restart. Cloud
success and gameplay projection are separate outcomes.

## 9. Implementation sequence and release gates

The first release is useful local pairing: attach, execute console edits, see
the completed frame, reconnect without losing the world. Participant isolation
and remote services have distinct release gates. The full roadmap is complete
only after all requested slices land; a local release does not claim Azure support.

| Milestone | Deliverable | Exit evidence |
|---|---|---|
| 0: local transport and protocol spike | Select SDK or owned implementation from measured evidence; pin actual client/protocol matrix; build a user-scoped IPC endpoint feeding a dedicated Console session. | Resolved architecture build and client interop; reviewed lock delta; correct host/profile identity; escaped multiline replies, fragmented/coalesced reads, blank/comment handling, bounded input/queueing, and no stdout contamination. No MCP package in base World. |
| 1: live operator pairing | `--profile operator --attach <pipe>`, full `puck_exec`, session-scoped waits, and one-call frame capture ordered behind prior edits. | Human enables attachment in a running world; agent changes a parameter and receives a fresh PNG. Human console responds during an MCP wait. Deferred rejection is not reported as success; overlay, shader-pass, and bare-producer captures all complete or fail explicitly. Adapter restart preserves world. Busy/timeout/device-loss, wrong-user/elevation, and remote pipe access checks pass. |
| 2: participant tools | Explicit participant composition, three bridge tools, binding lifecycle, bounded admission, honest receipts and retry semantics. | Participant cannot reach exec/files/frame/tape even by guessing tool names or changing profile arguments. Grant/revoke, channel reorder, body reuse, observe denial, cancellation, and saturation tests run against the real host. |
| 3: authoring and recording conveniences | `puck_doc` with live/save distinction and replacement preconditions, honest cost reports, recording/replay controls, and long verification handles where needed. | Concurrent document edits and malformed candidates; disk failures; codec declines, drops, stale recording handles, timeout after dispatch, replay arming refusal/mismatch. Existing console operations remain available throughout. |
| 4: parity and ingress | Existing parity runner as isolated job; approved device handles with revocation. | All parity verdicts retained; invalid schedule/unsupported backend; source access denial/revocation; device disappearance; no unauthorized audio/desktop disclosure. |
| 5: remote and services | HTTP auth, owner routing, tenant-qualified bindings, OBO and scoped external operations. | Wrong tenant/audience, expired token, cross-user handle reuse, replica routing, SDK interop, consent/reauthentication failures, durable-job restart, lost ARM response, binding revocation, and replay cannot resend cloud effects. |

Use existing focused gates where applicable:

```powershell
dotnet test tests/Puck.World.Agents.Tests/Puck.World.Agents.Tests.csproj -c Release
dotnet src/Puck.Cli/publish/Puck.Cli.dll architecture
dotnet test tests/Puck.World.Azure.Tests/Puck.World.Azure.Tests.csproj -c Release
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --filter FullyQualifiedName~WorldExtensionLawTests
```

Add adapter tests in an explicitly owned MCP test project, and real-process MCP
canaries to the CLI's supported verification workflow. Run the ordinary World
executable for composition/presentation changes following the current
[world verification guidance](../../.agents/skills/puck-world/SKILL.md). Route
rendering changes through its owning skill and `puck parity`; do not resurrect
quarantined Post or treat builds as behavioral proof. Recheck the current
`puck landing` routes before wiring a new canary into them.

Wire schemas must also be tested through the actual selected client: required
fields, unknown fields, Unicode, integer precision, errors, cancellation, bounded
responses, resources, and supported protocol versions. Measure concurrent-call
latency and host-frame impact with limits enabled. Saturation must remain bounded
and fail predictably while the world continues advancing.

Document operator configuration, capability disclosure, authority and ownership,
result semantics, retry rules, capture restrictions, and failure recovery in the
new project's README. Synchronize CLI help/README, agent bridge documentation,
World/Console documentation where changed, project-map generation inputs, XML
comments, and owning skills. Generate schemas from executable contracts and test
examples against those schemas; do not create a second hand-maintained catalog.

## 10. Review evidence and limits

This review used the portable Puck CLI's `worktree-base`, `search -M 0`,
`declarations`, and compiler-backed `references`. The semantic query loaded the
Agents.Tests project closure in Release with no reported workspace failure;
it establishes bridge consumers in that closure, not a solution-wide usage audit.
Direct source inspection established the authority, capture, and cost findings.
Official MCP and Microsoft documentation supplied protocol and OBO checks.

The existing Agents.Tests Release run passed all 15 tests (zero failures or
skips). `puck architecture` reported 87 projects and every declared check passed;
that command is a declared-reference report, not a replacement for the resolved
reference build gate. All local links in this handoff were checked and resolve.

The four rebuttals changed the plan: full trusted Operator access is explicit;
attachment leads the roadmap; SDK adoption is an evaluated choice; still capture
returns directly in one call. Async rendering never required async client polling.
The earlier review's participant restrictions were inappropriate for trusted
Console use, and owned-host-only delivery was the wrong first product slice.

The SDK rebuttal's dependency concern is worth measuring, but its specific premises
need correction. At this review the [published Core package](https://www.nuget.org/packages/ModelContextProtocol.Core)
is `2.2.0`, not `0.1.0-preview`; the [SDK package split](https://github.com/modelcontextprotocol/csharp-sdk)
separates low-level Core, hosting, and ASP.NET Core. Puck itself has package-bearing
[CLI](../../src/Puck.Cli/Puck.Cli.csproj) and
[Harness](../../src/Puck.World.AgentHarness/Puck.World.AgentHarness.csproj) projects.
The [architecture gate](../../build/PuckArchitectureGate.cs) checks the resolved
Puck graph and refuses Puck assemblies smuggled around project references; this
is not a blanket ban on third-party dependencies. No MCP package restore/build
spike was performed here, so package cost and compatibility remain unmeasured.

The rebuttal revision was documentation-only: local links and internal wording
were checked; the 15-test result above belongs to the initial review, not a new
protocol implementation. No MCP server, rendered capture, parity job, or live
Azure operation was run. Existing shader and world-asset changes belong to other
work and were left alone. This handoff is the only intended tracked change.

The second volley was checked against current source, including compiler-backed
`references ICaptureRequestTarget --implementers` in the World Release project
closure. It resolved the three render-node implementations above without a
reported workspace-load failure. Queue/barrier and PNG-write findings come from
source inspection, not a newly run graphical reproduction.

The inspected [Core 2.2.0 metadata](https://www.nuget.org/packages/ModelContextProtocol.Core)
lists a native net10.0 dependency group with AI.Abstractions >=10.8.3 and
Logging.Abstractions >=10.0.10. Current CLI locks Logging.Abstractions 10.0.11;
Harness locks AI.Abstractions 10.9.0 and Logging.Abstractions 10.0.11. Those
minimums show no obvious version-floor conflict with these existing choices,
but the CLI does not thereby already contain the whole SDK graph. This is
preflight evidence only; actual restore, resolved build, footprint measurement,
and target-client interoperability remain milestone 0 work. A release plan
should state that uncertainty instead of declaring either implementation chosen.
