# Puck MCP: adversarial review and implementation plan

Initially reviewed after technical rebuttal on 2026-09-07 against checkout base
`621127f0b67a`; the engine prerequisites were reviewed again at `5f63f7a533c4`.
Local and remote OAuth-protected Operator MCP, optional host composition and OBO observation reads are implemented; the remaining milestones
below are future work. Source links are repository-relative.

The adapter is now `Puck.Mcp`, an optional extension over `Puck.Hosting` with no
World dependency. CLI composes the existing silo with `AddPuckMcp`; standalone
silo and World retain no MCP reference. Named control targets keep independent
Console sessions and revoke them at row retirement. Windows and Linux x64 local
capabilities, live grant reload/revocation, readiness, operation logs and Meter/
ActivitySource instrumentation support the same hosting seam. The unified Bicep
reuses the existing silo, TLS load balancer, Key Vault and Entra registration.

The CLI's optional Azure service adapter exposes configured inventory/metrics
reads through OBO, using the existing observation providers and federated managed
identity pattern. These request-scoped reads have no persisted assertion or host
credential fallback. Delegated mutation jobs and Participant tools remain later
slices; their recovery and authority gates below still apply.

**Decision: deliver live local pairing first, with explicit Operator and Participant
profiles.** A trusted co-developer gets the same console control plane as the
human at the terminal. An embodied participant gets the scoped bridge. Remote
cloud service delegation has additional boundaries and remains a separate slice.
Body grants do not authorize host files, composed images, or Azure resources;
that fact must not become a reason to restrict an explicitly trusted operator.

## Local Operator implementation, 2026-09-08

Implemented from corrected prerequisite base `b1066e2973e0`: neutral
[Hosting console attachment](../../src/Puck.Hosting/README.md#local-console-attachment)
over [Networking capabilities and framing](../../src/Puck.Networking/README.md#local-endpoint-capabilities), optional official-SDK
[Puck.Mcp](../../src/Puck.Mcp/README.md), live
`world.control start|stop|status`, `puck_exec`, and `puck_capture_frame`.
The launch recipe and exact limits now live in those project READMEs.

The initial transport is Windows authenticated loopback, the alternative this
plan allowed: user-only held-open discovery, mutual challenge-response, four
connections, one admitted operation each, bounded framing, deadlines and no
implicit retries. It does not implement pipes or a separate elevation boundary;
it trusts the OS user. The attachment argument is a capability **file path**,
not a pipe name. Unix control support remains separate work; the remote gateway
below runs beside the Windows World and accepts clients from other platforms.

Official Core 2.2.0 builds under AOT/trim analysis. Resolved architecture builds
pass. CLI adds Core 2.2.0 and AI.Abstractions 10.8.3; existing package versions do
not change. Console and capture policy belong to Hosting; capability authentication
and the shared frame grammar belong to Networking. Hosting now references
Networking, so its consumers inherit Networking and Attestation. The exact
profiles must include those transitive dependencies while preserving the absence
of MCP packages from the engine. There is no separate control assembly.

Earlier assembly-size and startup observations predate this consolidation and
are not measurements of the resulting build. Focused contracts are owned by
Hosting and Networking tests; official SDK client interop against the real CLI
belongs to CLI tests, now targeting only 2026-07-28, authentication
failures, malformed framing, duplicate IDs, cancellation, timeout, EOF and reconnect.
Real Direct3D offscreen and windowed hosts accepted a parameter edit and returned
completed PNGs through the producer and unified overlay respectively. Human
console status answered in 15 ms during the windowed agent wait; adapter restart
preserved World and captured again. No specific IDE installation or physical
GPU-loss event was tested. Participant, document/recording conveniences, parity/ingress
and downstream services remain milestones 2–5. The acceptance table below remains the release checklist,
with pipe-specific checks superseded by the selected transport's trust contract.

## Implementation adversarial review, 2026-09-08

The initial happy-path checks missed failures at the asynchronous boundaries.
The correction keeps the official SDK as the MCP parser and the existing Console
as the operator's authority; it changes failure handling and resource bounds:

- Host deadlines now stop waiting on an injected session that ignores its token.
  Disconnect and timeout dispose the ingress once and observe abandoned work.
  Direct Console-session disposal also ends an accepted capture wait while its
  temporary path remains owned until the actual writer completes.
- Private JSON rejects duplicate/missing fields and invalid nulls. Both peers
  validate result identity and semantics. Invalid host results and unexpected
  execution failures report `unknown`, rather than crashing or claiming success.
- Stdio shutdown closes the underlying streams. Invalid UTF-8, oversized input
  and read failures propagate to a controlled nonzero CLI exit. Split Unicode
  scalars remain valid. Input admission and SDK output sends both have bounds;
  stalled stdout cannot retain an unlimited queue of replies.
- Tool validation failures are tool errors; unknown tools remain protocol errors.
  Malformed JSON string escapes are rejected as invalid protocol parameters
  before typed dispatch, rather than becoming an internal SDK error. This is
  tested through raw JSON because the official client rejects them before transmission.
  Structured metadata has an advertised output schema and identical JSON text.
  A missing reliable host reply uses a null request ID and preserves uncertainty;
  local admission refusals cannot borrow the next host sequence number.
  These choices follow the [tool-result contract](https://modelcontextprotocol.io/specification/2026-07-28/server/tools).

The regressions reproduced ten host failures, three stdio failures and the raw
Unicode error-classification failure before
correction. Additional tests exercise blocked writes, reply flooding, fragmented
UTF-8 and I/O faults. The Windows null-DACL attack was rejected by the existing
authentication implementation; its test records a successful defense, not a
fixed vulnerability. Inspection also confirmed that cancellation cannot lose a
capture request once its pump delegate has begun: the existing operation state
machine preserves that ownership until the delegate returns.

Verification on the corrected tree includes the Hosting and Networking suites,
official-client and adversarial CLI tests, a clean World Release build, and a real
Direct3D offscreen edit/capture/wait/disconnect/reconnect smoke. World reported the
dynamics edit applied, PNGs decoded, and human console input answered during the
agent wait. This review does not establish remote, multi-user or physical
device-loss behavior; those limits remain explicit above.

The final focused runs passed 35 Hosting tests, 152 Networking tests (one Unix-only
skip), and 14 MCP tests. CLI publish, documentation links and the length ledger
passed. The broader CLI attempt also exposed seven schema/official-build failures
from concurrent identity-refactor changes with stale generated schemas; those
other-session files were left to their owner. This is a verified MCP change,
not a claim that the entire shared checkout is green.

A .NET 10 ergonomics/performance pass used SDK 10.0.400 and runtime 10.0.11,
the newest installed versions, plus Puck's content and compiler-backed reference
queries. The SDK already owns a channel; layering a second queue would add
another admission/lifetime boundary. Single-result completion sources, `Lock`,
`Interlocked`, cancellation-aware stream I/O and `Task.WaitAsync` retain their
specific roles. Metadata now uses source-generated `SerializeToElement` over a
typed value instead of a mutable JSON tree followed by serialization, parsing
and cloning. Schema parsing uses the standalone .NET 10 `JsonElement.Parse` API.
A warmed Release file-app probe on this runtime measured 2,288 versus 952 allocated
bytes per metadata-plus-text result with 128 output characters, across two runs.
This is a component allocation result; timing varied and establishes no
end-to-end latency improvement. Existing MCP interop tests verify the unchanged
wire results, including equality of structured metadata and JSON text.

## Engine prerequisites implemented on 2026-09-07

Commit `5f63f7a533c464021644abdfa87db684f4024750` supplies the engine seams
for local pairing:

- `world.wait` now holds only its issuing text session, with independent
  deadlines on the host-work clock. Both desktop and Silo use this behavior.
- `TextCommandSession.InvokeAsync` queues short host operations behind that
  session's commands, simulation barriers and waits, inside its host scope.
  Queued cancellation skips the operation; cancellation after execution starts
  cannot undo it. Disposal closes the ingress and refuses its queued work.
- `SdfWorldRender.RequestCapture` returns a `FrameCaptureRequest`. All three
  render writers carry that same request and resolve its `Completion` with a
  `FrameCaptureResult` after writing or on failure. Busy targets cannot replace
  accepted requests. The scheduled-capture consumer uses this completion too.

The engine prerequisites are integrated. The original verification below
describes the earlier implementation worktree; it is not a new gate run against
the landed commit. Current attachment verification is recorded above.

Adversarial review of the landed commit found three lifecycle defects:
`FrameCaptureRequest.Write` swallowed the host's `DeviceLostException`
recovery signal, Silo row retirement did not dispose its command session, and
a clock reset could revive an expired wait before the pump observed its expiry.
Corrections complete the failed capture before rethrowing device loss, close
retired sessions and refuse their queued work, and treat stdin racing retirement
as a row refusal. Clock reset invalidates all previous deadlines.
Regression tests exercise the real request, routing, and wait APIs.
These review corrections must accompany the prerequisites into the MCP work.

The local implementation reuses these engine APIs instead of inventing a
`WorldRenderProbe` success event. Authoritative per-mutation receipts remain
separate work: a submission callback or a completed capture does not prove an
edit was accepted. Capture waiting needs an adapter deadline and unique-path
cleanup; cancelling the wait leaves an accepted render request alive.

## 1. Findings that change the design

| Priority | Finding and evidence | Required correction |
|---|---|---|
| P0 | Giving a participant arbitrary `puck_exec` would expose host operations beyond body grants. [Recording commands](../../src/Puck.World/WorldRecordingCommandModule.cs) open paths and devices without consulting an acting principal; [replay commands](../../src/Puck.World.Console/WorldReplayCommandModule.cs) manage the session tape directly. | Participant has no exec. Explicit local Operator uses Console identity and the full existing command registry, without an MCP allowlist or per-verb approval layer. |
| P0 | `puck_doc.write` equates a `Mutate` check with permission to replace a disk file. Puck's [mutation authority](../../.claude/skills/puck-world/references/authority.md) instead checks section/row scope, mutation-kind masks, additional state `Edit`, and dispatch budgets. | Separate live authoritative mutations from offline document persistence. Neither may bypass the other's admission or storage boundary. |
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

- The base checkout installed the desktop's shared wait gate as a source-wide
  hold. The landed engine changes remove that registration and put the deadline on the
  issuing `TextCommandSession`. Independent session tests now cover that engine
  boundary; simultaneous human/IPC use still belongs to the attachment release
  gate.
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
  -> puck mcp --profile operator --attach <file> -> local control endpoint
```

This is implemented CLI syntax. `world.control start|stop|status` supplies live
opt-in without a deployment flag or a World restart. MCP stdin/stdout belong to
the CLI adapter, while World retains its human console and window. Adapter EOF
or crash does not terminate World. An owned-host launch recipe can be added
later with explicit ownership and shutdown behavior.

The following pipe criteria remain guidance for any future pipe transport; the
initial implementation selects authenticated loopback as described above.
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
drain barriers and callback ordering; use the session-scoped waits to retain
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

- `Puck.Mcp`: optional protocol adapter, schemas, result mapping, binding
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
in milestone 0. The implementation selected `ModelContextProtocol.Core` 2.2.0
after restore, resolved builds, client interop and footprint measurement.
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

Frame capture after exec must use `TextCommandSession.InvokeAsync` to enter
the same session ordering/barrier before arming the render request. An out-of-band mailbox capture can otherwise overtake
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
completion seam at the render owner (`FrameCaptureRequest.Completion`); a `pending` console echo or mere file
existence is not completion. Read only completed bytes using a unique temporary
destination if the existing path requires a file, then clean it up. Return an
image block with actual media type/size and available frame metadata. No persistent
artifact registry, poll handle, separate download, or assumed 50 ms deadline is
required for a successful small still. Bound image dimensions and payload bytes.

Use the neutral [ICaptureRequestTarget](../../src/Puck.Abstractions/Presentation/ICaptureRequestTarget.cs)
contract and its `FrameCaptureRequest`, now forwarded unchanged through all
current implementers:
[UnifiedOverlayNode](../../src/Puck.Overlays/UnifiedOverlayNode.cs),
[FullscreenPassNode](../../src/Puck.Shaders/FullscreenPassNode.cs), and
[SdfEngineNode](../../src/Puck.SdfVm/SdfEngineNode.cs). Signal success only after
the serving node's PNG write is complete; signal failure on unavailable capture,
readback/write failure, and shutdown. The pending path may clear before the write finishes; only the request
completion reports success or failure.
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
same signal already consumed by `WorldCaptureScheduler`.

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

Remote Operator now uses the official ASP.NET Core SDK 2.2.0 and .NET 10 JWT bearer
authentication in the existing optional adapter. Its executable configuration and
deployment contract live in the [MCP README](../../src/Puck.Mcp/README.md#remote-http-and-oauth).
Both local and remote servers select MCP 2026-07-28 explicitly, following its
[authorization requirements](https://modelcontextprotocol.io/specification/2026-07-28/basic/authorization).
The gateway implements protected-resource discovery, TLS, Host/Origin validation,
bounded HTTP admission and signature/issuer/audience/lifetime/delegated-scope
checks on every request. Explicit subjects receive full Operator authority.
Entra uses oid/tid and separates requested authorization scopes from signed scp
values. Authentication never trusts protocol clientInfo. The CLI now requires
the ASP.NET Core runtime; base World still carries no MCP packages.

Stateless HTTP requests use explicit caller-bound application attachments to
preserve Console ordering and waits. Cancellation, active token expiry, unknown
outcomes and gateway shutdown release them; idle expiry bounds abandoned handles.
The gateway never reconnects or replays commands implicitly. Attachments are local
to their gateway instance. Tenant deployment values remain configurable; local
cryptographic tests do not establish live Entra registration/consent or deployment.

The final focused run passed 37 MCP tests, including HTTP saturation and recovery.
The World Release build passed without warnings or errors, and CLI publish,
locked restore, documentation links and the length ledger passed. The declared
architecture report passed for 88 projects; resolved checks also ran in the
affected builds. The HTTP dependencies advance IdentityModel to 8.19.2 in CLI's
lock graph; the earlier local-only package observations above predate this step.
A real Direct3D offscreen World accepted a dynamics edit through HTTPS with signed
Entra-shaped JWTs and returned a fully decoded 640×480 RGBA PNG. Human Console
status answered during the attachment's wait; cancelling its pending capture and
creating a fresh attachment allowed another capture. This used a controlled OIDC
issuer, not live tenant consent. That run did not verify deployment or downstream OBO.
The broader CLI run passed 114 of 121 tests. Its seven failures are the schema
drift check and six official-tree checks blocked by that drift: four generated
World schemas still describe the concurrently renamed identity types. Those
other-session source and generated files were left to their owner.

The subsequent integration adds optional `Puck.Mcp` host composition, live grant
revocation, readiness and diagnostics, Linux x64 capabilities, unified Bicep wiring,
and request-bound delegated inventory and metrics observations. The OBO tests use
the real Azure Identity and ARM pipelines with a controlled token endpoint;
they establish assertion exchange and no host-identity fallback, not live consent.
Participant admission and durable delegated mutations remain future work:

Keep the remaining integration on the existing platform. The unified
[Bicep entry point](../../src/Puck.Azure.Resources/main.bicep) already owns Entra
application registration, delegated scopes, federated credentials, ingress,
configuration and monitoring; MCP now extends that deployment and the existing host
lifecycle. The API and Actors also implement federated OBO through
[IdentityUtilities](../../src/Puck.Azure.Functions/Utilities/IdentityUtilities.cs);
the API also has a request-scoped
[credential context](../../src/Puck.Azure.Functions/UserCredentialContext.cs).
The Azure operation provider accepts a TokenCredential, while WorldExtensionClient
already owns granted discovery, durable invocation/status and revocation. Connect
authenticated MCP callers to those durable-operation seams; preserve caller identity
across durable work and reauthentication. Existing silo health/drain and
configuration-refresh mechanisms remain the operating foundation.
Live Entra/client acceptance remains verification.

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
| Engine prerequisites: before milestone 0 | Landed in `5f63f7a533c4`: session waits, ordered host operations, and correlated capture completion. Include the review corrections for device-loss propagation, row retirement, and clock reset. | Pass the affected builds and tests on the tree used for MCP work. Prior verification is not evidence for a new merged tree. |
| 0: local transport and protocol spike | Select SDK or owned implementation from measured evidence; pin actual client/protocol matrix; build a user-scoped IPC endpoint feeding a dedicated Console session. | Resolved architecture build and client interop; reviewed lock delta; correct host/profile identity; escaped multiline replies, fragmented/coalesced reads, blank/comment handling, bounded input/queueing, and no stdout contamination. No MCP package in base World. |
| 1: live operator pairing | `--profile operator --attach <file>`, full `puck_exec`, session-scoped waits, and one-call frame capture ordered behind prior edits. | Human enables attachment in a running world; agent changes a parameter and receives a fresh PNG. Human console responds during an MCP wait. Deferred rejection is not reported as success; overlay, shader-pass, and bare-producer captures all complete or fail explicitly. Adapter restart preserves world. Busy/timeout/device-loss and the selected transport's user/host authentication and local-only binding checks pass; pipe-specific elevation/remote checks apply only to a future pipe transport. |
| 2: participant tools | Explicit participant composition, three bridge tools, binding lifecycle, bounded admission, honest receipts and retry semantics. | Participant cannot reach exec/files/frame/tape even by guessing tool names or changing profile arguments. Grant/revoke, channel reorder, body reuse, observe denial, cancellation, and saturation tests run against the real host. |
| 3: authoring and recording conveniences | `puck_doc` with live/save distinction and replacement preconditions, honest cost reports, recording/replay controls, and long verification handles where needed. | Concurrent document edits and malformed candidates; disk failures; codec declines, drops, stale recording handles, timeout after dispatch, replay arming refusal/mismatch. Existing console operations remain available throughout. |
| 4: parity and ingress | Existing parity runner as isolated job; approved device handles with revocation. | All parity verdicts retained; invalid schedule/unsupported backend; source access denial/revocation; device disappearance; no unauthorized audio/desktop disclosure. |
| 5a: remote Operator | Implemented: OAuth-protected HTTP, explicit subject grants and caller-bound gateway attachments. Latest MCP only. | Real Kestrel/SDK, signed JWT/OIDC/JWKS, TLS, wrong tenant/audience/scope/subject, active expiry, cross-user handles, cancellation, bounds and shutdown. Live Entra consent/deployment still requires deployment configuration and verification. |
| 5b: delegated services | Implemented: scoped inventory/metrics reads through OBO. Remaining: participant authority bindings and durable delegated mutations. | Request identity, grant refusal, token exchange and no fallback are tested. Remaining gates cover owner routing, live consent, reauthentication, durable-job restart, lost ARM response, binding revocation, and replay suppression. |

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
[world verification guidance](../../.claude/skills/puck-world/SKILL.md). Route
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
is not a blanket ban on third-party dependencies. This original review did not
run an SDK spike; the implementation evidence above now supplies it.

The earlier rebuttal revisions were documentation-only. Their 15-test result
belongs to the initial review. The later engine-prerequisite implementation and
real rendering checks are recorded below; those earlier runs did not exercise
MCP, cross-backend parity or live Azure operations. Concurrent shader and world-asset
changes in the primary checkout were left alone.

The second volley was checked against current source, including compiler-backed
`references ICaptureRequestTarget --implementers` in the World Release project
closure. It resolved the three render-node implementations above without a
reported workspace-load failure. Queue/barrier and PNG-write findings come from
source inspection; the later graphical checks below validate the implemented
capture behavior.

The inspected [Core 2.2.0 metadata](https://www.nuget.org/packages/ModelContextProtocol.Core)
lists a native net10.0 dependency group with AI.Abstractions >=10.8.3 and
Logging.Abstractions >=10.0.10. Current CLI locks Logging.Abstractions 10.0.11;
Harness locks AI.Abstractions 10.9.0 and Logging.Abstractions 10.0.11. Those
minimums show no obvious version-floor conflict with these existing choices,
but those metadata minimums alone did not establish compatibility. They were
preflight evidence; the implementation results above now record the actual graph
and chosen SDK/client matrix.

Original engine prerequisite verification in the implementation worktree: World and Silo Release builds
passed with zero warnings, and 516 tests passed across Commands (454),
Abstractions (10), Shaders (42), and the focused World command suite (10).
The Puck CLI compiler-backed implementer query resolved all three capture targets
in the World Release closure; the architecture report passed all 87 projects.
A real headless host released a session wait. A real Direct3D offscreen host
wrote and decoded captures, refused a second busy request, reported an intentional
write failure, recovered on the following request, and wrote both scheduled
capture manifests through the completion result. This is not a cross-backend
parity result. Offscreen mode currently omits post-render extensions, so it
cannot validate that composed windowed path.

The windowed Direct3D smoke also wrote a PNG through the unified overlay.
Fullscreen-pass forwarding, busy refusal and disposal are covered by the shader
suite; a live fullscreen-pass write and Vulkan parity are not claimed by this run.

The independent-session wait assertion was also checked with an intentionally
inverted production hold predicate: it failed, and passed again after restoring
the implementation. Host-scope entry failure completes the queued operation
with an error rather than stranding its caller.

Review verification for the correction patch against `5f63f7a533c4`:
the device-loss, retired-session, and expired-wait-reset regressions each failed
before correction. After correction, the full World suite passed 1,974 tests
with two skips (Azure configuration and Windows symlink support); Commands
passed 454, Abstractions 11, and Shaders 42. World and Silo Release builds
passed. The initial sandboxed World run failed during Windows cryptographic
key setup and skipped replay writes; the normal-user rerun resolved those
environment failures. Documentation links and the length ledger passed.
Device loss was injected at the capture request boundary; a live GPU reset
and Vulkan parity were not exercised in this review.

The pre-commit check also regenerated stale browser-WASM and trimming entries
in shared-library dependency locks. Locked restores now pass for both the
native World test graph and the browser project; retained native dependencies
are unchanged.
