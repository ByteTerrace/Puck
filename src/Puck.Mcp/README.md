# Puck.Mcp

An optional host extension exposes Puck Console sessions and completed screenshots over local
stdio or OAuth-protected remote Streamable HTTP. Both target MCP **2026-07-28**,
with per-request metadata and discovery. Local stdio also answers the initialize
handshake of the earlier revisions, which is what an editor or an agent harness
opens with; remote HTTP serves 2026-07-28 alone.
It depends only on Puck.Hosting and its substrate. Local attachment supports
Windows and Linux x64 under the host's OS user; in-process hosting uses the
neutral `IControlSessionHost` interface. Clients may run on other platforms.
The CLI requires the .NET 10 ASP.NET Core runtime.

## Host extension

Remote MCP is an installed [extension](../../docs/reference/extensions.md).
`McpControlExtension` contributes the host's one `HostedControl`: a host that
exposes `IControlSessionHost` starts it when the deployment names a control
configuration, and a second hosted control is refused at composition. MCP owns
authentication, attachment admission and its HTTP listener. The host retains its
Console pump, target lifetime and shutdown. Closing MCP ingress precedes host drain.

For an owned World silo with `Puck.Mcp` installed in its extensions directory:

```text
puck mcp --silo silo.json --http remote.json
```

which is the silo's own `--silo silo.json --mcp remote.json`.

The configuration's `services` member decides which remote host the server uses.
Without it, every caller attaches to the configured `target`. With it, exactly one
installed extension must contribute an `McpServicesProvider`, which interprets the
settings and adds its service tools; `services` with no provider, or with more
than one, refuses startup by name. [Puck.Mcp.Azure](../Puck.Mcp.Azure/README.md)
is the provider this repository ships. Puck.Mcp itself references no provider.

Set `target` to the silo's exact World row. Remote configuration has no local capability path.
Each attachment gets a dedicated text session with a fixed row binding and an admitted peer identity.
Retirement closes it; admitting the same row again never revives an old handle.
Discovery describes the admitted command surface from the host's actual registry.
The headless silo omits framebuffer capture from discovery and refuses direct calls. Standalone
`Puck.World.Silo` has no MCP assembly or package dependency; MCP reaches it only
as an installed extension, and only a named control configuration starts it.
Neither the desktop World nor the silo installs MCP implicitly.

An existing ASP.NET Core host can call `RemoteMcpServer.AddServices(services, options)`
and build `RemoteMcpServer.CreateRequestDelegate(app.Services)` once. Dispatch the
original `/mcp`, `/.well-known/oauth-protected-resource/mcp`, and `/healthz` contexts
to that delegate before the host's other endpoint authorization. Preserve the full
request path and scoped `RequestServices`. This composition omits `listenUrl` and
certificate settings: the outer host owns TLS and its listener. MCP keeps its own
authentication schemes, CORS policy and admission limiter, preserving the host's
authentication defaults. The body limit, request deadline, shutdown cancellation
and grant checks still apply. Configure the outer server's header, connection and
header-timeout limits too. Plaintext connections must come from loopback.

A services host may override `RemoteMcpHost.SupportsAttachments` to return
false, as the Azure provider does for a configuration with `services` and no
`target`. Then Console tools are neither advertised nor callable. The provider
still owns each service's authorization and disclosure limits.

A host decides each `tools/call`'s delegated authorization in
`RemoteMcpHost.AuthorizeAsync`, which runs after the caller's bearer token is
validated and before the request reaches the MCP dispatcher. That is where any
token exchange that can require user interaction belongs; a
`RemoteMcpAuthorizationException` thrown there answers that request with an HTTP
401 challenge. The state it returns, such as an exchanged downstream token,
reaches `AttachAsync` or `CallServiceAsync` as `RemoteMcpCaller.Authorization`.
Those two run after dispatch, when the transport may already have streamed the
response headers. When a downstream service rejects the delegated token there,
as continuous access evaluation does after a revocation, they throw
`RemoteMcpAuthorizationException` too: the call fails with a tool error that asks
the caller to sign in again, and the resource server remembers the claims
challenge for that subject. The subject's next request of any kind that presents
the rejected token, or one issued before it, is answered with the 401 challenge
without being dispatched. A token issued since the rejection is the
re-authentication the challenge asked for; it clears the challenge and is served.
One remembered challenge is held per granted subject, and removing a subject's
grant drops it. Any other exception from these two reaches the client as an
ordinary tool failure.

## Local stdio

[Install the checkout's CLI on PATH](../../docs/reference/cli.md#installing-the-checkouts-cli-on-path),
start World normally, then enter:

```text
world.control start
```

It prints an attachment file path. The MCP client launches:

```text
puck mcp --profile operator
```

which follows the newest running World, so one configuration serves every run.
The checkout carries it for both clients: `.mcp.json` at the root and
`.vscode/mcp.json`. The adapter outlives Worlds and attaches on each tool call:
started before any World, or after the World exits, a call is refused with
nothing dispatched; once a World runs `world.control start`, the next call
attaches. A call whose attachment closes (timeout, cancellation, World exit or
`world.control stop`) reports an unknown outcome and is never replayed; the next
call attaches anew, possibly to a restarted World with fresh state.
`--attach <printed-file>` pins one World where several run. Never launch the
server from a project's build output: the running server holds every assembly
in that folder, and the next build fails to replace them. Paths containing spaces
must remain one argument in the client configuration. `world.control status`
repeats the path; `world.control stop` closes attachments. Starting an already
started endpoint retains the same path. Stopping and starting creates a new
incarnation and path. These verbs require the host Console principal and work
without restarting World.

The Operator attachment uses the
[control transport](../../docs/reference/hosting.md#local-console-attachment) to authenticate loopback using a
protected per-user capability file. Do not share its contents. The operator has
the full existing Console registry and ordinary host permissions. There is no
MCP verb allowlist or per-command approval layer. Participant profiles remain
future work; other local profiles are usage errors.

## Remote HTTP and OAuth

Copy [remote.example.json](remote.example.json), supply your deployment values,
and use the silo composition above. Point an OAuth-capable MCP client at
`publicUrl`. Protected-resource discovery is published at
`/.well-known/oauth-protected-resource/mcp`. Clients use authorization code with
PKCE and a registered client ID; the configured authorization server owns login,
consent and tokens. JWT signatures, exact issuer, audience, expiry, delegated
scope, subject and tenant are checked on every request. App-only role tokens and
opaque access tokens are refused.

For Entra, use the tenant-specific v2 issuer, API application ID as `audience`,
`subjectClaim: "oid"`, and the tenant UUID as `tenantId`. The delegated scope is
`user_impersonation`; `authorizationScope` is its full `api://<application-id>/user_impersonation`
spelling. First-party and external public clients use the same registration,
redirect URI, PKCE and consent contract. Other standards-based OIDC issuers use
`sub` and `scope`. Puck does not register clients or issue refresh tokens.

`allowedSubjects` permits gateway access. It grants no Console, filesystem or
process authority. The World must separately author an `OAuth` admission row
whose `domain` is the exact issuer, whose `subject` matches, and whose `algorithm`
and `publicKey` are empty. The silo requires explicit `Replica` disclosure for
its text surface: these commands can read the full authored state. This reuses
the existing disclosure contract; peers cannot hold `Observe/all`.

The remote command surface is `world.wait`, `world.peers`, `world.admission`,
`world.links`, `world.state`, `world.state.cell.set` and `world.state.cell.remove`.
A live command guard refuses other verbs, including newly registered local admin
commands. Every accepted command retains its generation-bound Peer principal.
State writes additionally require the ordinary `Mutate/section:state` grant with
budget and mutation-kind mask, plus `Edit/state:<row>`. Empty admission grants
permit no writes. World reset preserves existing revocations; retirement closes
sessions and removes their identity. No bearer token enters command text,
simulation state or recordings. The immutable target binds the session to the
configured owner/World; neither a command nor `silo.use` can select another row.
This release deliberately supports one authoritative worker; it does not offer
distributed attachment routing or arbitrary user-owned World discovery.

The optional [Puck.Mcp.Azure](../Puck.Mcp.Azure/README.md) services provider
reuses [delegated services](../Puck.World.Azure/README.md#delegated-observations).
`puck_onboard` calls the existing Function `/api/self-onboard` with an exchanged
OBO token. It reuses account provisioning, partition selection and protected user
escrow. Attachment also checks onboarding; `Onboarding` requires an explicit retry,
while `Ready` and `Migrating` proceed to World admission. No second account store
or provisioning workflow exists in MCP.

`puck_service_observe` uses the current caller's assertion and the managed
identity's federated client assertion to obtain an ARM token. Per-observation
subject grants and field allowlists still apply. Failed consent never falls back
to host credentials. Discovery includes only the caller's granted observation
names, as an input-schema enum. The Entra exchange for `puck_onboard`,
`puck_attach` and `puck_service_observe` runs before dispatch. A sign-in, consent
or claims challenge from it returns HTTP 401 with `WWW-Authenticate`, the MCP
resource metadata URI and its qualified `user_impersonation` scope, however long
the exchange takes. Validated claims are bounded and encoded; downstream
authority and scope headers are never forwarded. The client must obtain fresh
user authorization before retrying. A downstream API that refuses the exchanged
token once the call is running fails that call and challenges the caller's next
request, as described above. MCP retains no user/downstream tokens after the
request; a remembered challenge holds only the rejected token's SHA-256
fingerprint and issue time. The existing platform onboarding service owns its
protected escrow lifetime.
World simulation grants authorize World changes; they are not Azure permissions.
Host filesystem, process, deployment and cloud-job commands are unavailable
remotely. Durable delegated cloud mutation services remain outside this surface.
For a proxy that authenticates origin requests, configure `trustedProxy` with its
exact `issuer`, `audience`, `subjectClaim`, `subject`, and optional `tenantId`.
Entra uses the proxy managed identity's `oid` and requires `tenantId`. The proxy
must overwrite `ClientAuthorization` with the incoming caller Authorization
header and put its own API access token in Authorization. It must remove any
client-supplied `ClientAuthorization` when no caller Authorization was supplied.
MCP validates the origin token and configured proxy identity before accepting the
forwarded token, then independently validates the caller. An absent forwarded
token never falls back to the origin identity. Without `trustedProxy`, MCP ignores
`ClientAuthorization`. Delegation obtains the saved token from the caller's
validated authentication ticket, never from a raw header.

Discovery remains anonymous to callers through the authenticated proxy. Direct
requests to that origin, including discovery and readiness, require the proxy
identity. Configure the origin health probe accordingly. The Function App's
Front Door/OBO design is the identity reference; MCP stays an optional ASP.NET
Core host extension. Its live Console attachments require deliberate owner
routing and lifetime management, so the Function worker is not its hosting target.

`publicUrl` must be HTTPS and end in `/mcp`. A loopback HTTP `listenUrl` is for a
TLS reverse proxy on the same machine. Preserve the public Host header, disable
response buffering, and allow at least the 125-second request deadline. Direct
HTTPS accepts an IP-literal listener and requires a PFX `certificatePath`; its
optional password comes from the deployment-secret file `certificatePasswordFile`. Relative
file paths resolve beside the configuration file. Public plaintext listeners,
unknown Hosts and unapproved browser Origins are refused. Add browser clients'
exact HTTPS origins to `allowedOrigins` when required. Forwarded headers cannot
change public identity. Gateway access reloads every second.
Removing a subject closes its existing attachments. An unreadable, invalid, or
incompatible replacement revokes all grants until corrected. Endpoint, issuer,
scope, certificate and target changes require restart. Changing the capability
path affects only explicit new attachments; old handles never reconnect.

`GET /healthz` reads host readiness without consuming an attachment or an operation
slot. In local capability mode it checks the private descriptor; an in-process
host supplies target readiness. Operation logs include subject, tool, outcome,
request correlation and elapsed time, excluding arguments, handles and tokens.
Existing OpenTelemetry exporters can subscribe to the `Puck.Mcp` Meter and
ActivitySource. Metrics carry tool and outcome labels without caller identities.

Remote clients first call `puck_attach` with no arguments. It returns an
`attachmentId`; include it in every exec/capture request and release it with
`puck_detach`. The handle preserves the caller's Console session across stateless
HTTP requests and is bound to the validated subject. There are no MCP protocol
session IDs, GET event streams or resumable responses. Route an attachment back
to the same gateway instance; handles are neither distributed nor portable.

The gateway admits four concurrent HTTP requests and four live/opening
attachments, with at most two of each per authenticated subject. Excess HTTP work
returns 429 with `Retry-After: 1`; excess attachments return a tool refusal.
There is no waiting queue. In local capability mode these also
consume the host's four control slots alongside local clients. Each attachment admits one operation. Idle attachments
expire after `idleTimeoutSeconds` (default 300, range 10–3600). Every HTTP request
has a 125-second ceiling shortened to its token expiry; exec/capture retain their
own shorter deadlines. Cancellation or expiry during a dispatched operation
closes its attachment, leaving any already-applied effects uncertain. An idle
handle can be resumed by the same subject with a fresh valid token before its
idle deadline. Restart, explicit detach or unknown host outcomes invalidate it.
Never automatically reattach and replay a mutation. HTTP bodies are limited to
64 KiB, including chunked requests in an embedded host. The standalone listener
also limits headers to 16 KiB and connections to 64, with a ten-second header deadline.

## Tools and results

| Tool | Arguments | Result |
|---|---|---|
| `puck_exec` | Required `command`: one console line, at most 8192 characters. Optional integer `timeoutMs`: 1–120000, default 30000. | Console output plus structured decimal-string `requestId`, `status`, `output`, `isError`, `clearTranscript`. Empty output is `submitted`, not authoritative application. |
| `puck_capture_frame` | Optional `timeoutMs`, same range/default. No path. | Completed PNG image content, up to 16 MiB, plus completion metadata. Includes the composed view and overlays; requires an initialized renderer. |
| `puck_state_vector_write` | Required `row` and `vector`; optional `key` (defaults to `$value`) and `timeoutMs`, same range/default. | Writes the admitted unit vector into that state cell through `world.state.cell.set`, returning the same structured result shape as `puck_exec`. |

Mutation commands that provide an authority settlement wait for its applied or
refused verdict. This is console output, not a durable persistence receipt.
Local rebuild and undo commands wait for their tick-boundary echo. A transport
without a local verdict reports that the outcome is unknown.

For HTTP, add the required `attachmentId` to these arguments. Call serially per
attachment; concurrent calls return busy. Capture enters this session's
ordering and simulation barrier, including its `world.wait`, then returns one
completed image without polling. Completion proves the writer closed a PNG; it
does not establish an exact simulation tick or acceptance of a prior mutation.
Human edits may also enter that frame. Human console input remains independent
while this attachment waits.

Both tools advertise an output schema. Metadata is returned as structured content
and matching JSON text, so clients that consume only text retain the same facts.
`requestId` is null when no reliable host reply exists. `status` is `completed`,
`submitted`, `refused` or `unknown`; an uncertain outcome is never certified as success.

Invalid tool arguments, ordinary command errors and capture failures return
`isError`; unknown tools remain protocol errors. Malformed JSON string escapes
are invalid protocol parameters before tool dispatch. Timeout closes the attachment and reports an
uncertain dispatched outcome. Explicit cancellation follows the selected SDK
protocol's rules; the adapter sends no extra late result.

A closed attachment never ends the local adapter. A timeout, a cancellation, a
host that stopped control or exited, or a transport failure closes only the
attachment: the call that found it closed reports an unknown outcome (a
cancelled request gets no reply), and the next call attaches anew, to the pinned
file or the newest World. A new attachment is a new Console session, so waits or
ordering held by the old one do not carry over. Inspect state before deciding
whether to retry. Nothing is automatically replayed. With no World to attach, a
call is refused and nothing runs.
EOF and adapter crashes release the attachment while World and its recordings
retain their own lifetimes. Local Operator `quit` and reload retain their normal meaning; the remote guard refuses them.

## SDK and verification

The adapter uses official **ModelContextProtocol.Core/ASP.NET Core 2.2.0** and
ASP.NET Core JWT bearer authentication 10.0.11, with explicit schemas and low-level
handlers. It installs no Harness or model provider.
Result metadata uses a typed, source-generated serializer; standalone schemas use
.NET 10's `JsonElement.Parse`. The SDK owns the asynchronous message channel.
Admission counters bound that transport without adding another queue; a single
console result uses a completion source, and the ordinary engine pump keeps its
synchronous drain and session barriers.
World uses `Puck.Hosting` and `Puck.Networking`; the CLI references this adapter for
the local stdio profile, and hosts install it for remote HTTP.
The SDK stays outside the base engine. Networking and the adapter retain exact
architecture profiles, and all three libraries retain AOT/trim analysis. MCP input
is strictly UTF-8 with a 64 KiB JSON-RPC line ceiling before SDK buffering. At most
128 nonblank messages may await SDK consumption; malformed messages consume that
budget too. Stdout admits at most four pending replies with a five-second send
deadline, including time waiting for the SDK's send lock. Every adapter-side
deadline runs on the `TimeProvider` passed to `OperatorMcpServer.RunAsync` (the
system clock by default): each attachment's five-second connect and handshake,
each call, the control connection's own call deadline and each reply write. The
World's side runs its handshake and request deadlines on the clock its
`LocalControlServer` was given. Exceeding either budget
or failing an input/output operation closes the attachment and exits with failure.
Clean EOF exits successfully. The adapter owns both streams and closes them on
shutdown, including when a pending read ignores cancellation. Stdout is protocol-only;
diagnostics use stderr.

The process tests launch the real CLI and connect the official C# SDK 2.2.0
client over its stdio, pinned to a World and through `--attach latest`: listing,
exec, images and argument errors, plus EOF and oversized-input exits that leave
the World serving. Each gives the child a private temporary directory, so
`latest` can only find that test's World. Each connection ends as the stdio
transport specifies: the client closes the adapter's stdin and the adapter must
exit with code 0. The SDK's own `StdioClientTransport` never closes that stdin,
so it kills any server once its `ShutdownTimeout` expires
([csharp-sdk#1836](https://github.com/modelcontextprotocol/csharp-sdk/issues/1836)).
The adapter's laws run in process, with the adapter and the World each on a
test clock that fires a deadline only when the test expires it. They check that
the adapter's deadline, the World's deadline and a client cancellation each
close only the attachment, that the next call attaches anew, that calls with no
World are refused while discovery answers, and that following a directory picks
the newest World that answers, skipping one that is gone. They also cover split
UTF-8 characters, invalid encodings, oversized and malformed input, stalled
output, reply flooding and cancellation-resistant reads.
The remote server explicitly selects this revision; the local one pins none, because a
pinned revision turns the initialize handshake off, and its interop test runs the
handshake revisions beside it. Remote tests use real Kestrel,
signed JWTs, OIDC discovery/JWKS, direct TLS, tenant/subject isolation, scope and
audience refusal, token expiry, cancellation, idle expiry, capacity and shutdown.
Raw HTTP tests check required metadata, header mismatches and absent session
endpoints, claims challenges before and after dispatch and recovery with a newly
issued token, caller fairness, and headless discovery.
This is not a claim that every IDE client or the live Entra tenant
has been tested. The protocol contract is the
[current specification](https://modelcontextprotocol.io/specification/2026-07-28).

Render writer boundary tests are in Commands, Abstractions and Shaders. No
automated check covers a physical device-loss event or a live Entra deployment.

Run `dotnet test tests/Puck.Cli.Tests -c Release` for SDK interop and the
[Hosting tests](../../tests/Puck.Hosting.Tests/README.md) for engine attachment contracts.
For a live smoke, edit a parameter through MCP and decode the captured PNG; hold
this attachment with `world.wait` and confirm human input still answers. Close
the adapter during the wait, reconnect with the same file, then capture again.
Stopping control must remove discovery without stopping World; headless hosts
must refuse capture. These checks run the ordinary engine, with no alternate
simulation loop. See the
[play programme](../../docs/plans/play.md#mcp--the-local-surface).

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
