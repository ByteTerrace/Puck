# Puck.Mcp

An optional host extension exposes Puck Console sessions and completed screenshots over local
stdio or OAuth-protected remote Streamable HTTP. Both target MCP **2026-07-28**,
with per-request metadata and discovery; older protocol revisions are unsupported.
It depends only on Puck.Hosting and its substrate. Local attachment supports
Windows and Linux x64 under the host's OS user; in-process hosting uses the
neutral `IControlSessionHost` interface. Clients may run on other platforms.
The CLI requires the .NET 10 ASP.NET Core runtime.

## Host extension

An outer composition installs `builder.AddPuckMcp(options, configurationPath)`.
The application registers `IControlSessionHost`; MCP owns authentication,
attachment admission and its HTTP listener. The host retains its Console pump,
target lifetime and shutdown. Closing MCP ingress precedes host drain.

For an owned World silo, the CLI composes both:

```text
puck mcp --silo silo.json --http remote.json
```

Set `target` to the silo's exact World row and omit `attachmentPath`.
Each attachment gets a dedicated Console session with a fixed row binding.
Retirement closes it; admitting the same row again never revives an old handle.
The headless silo explicitly refuses framebuffer capture. Standalone
`Puck.World.Silo` has no MCP assembly or package dependency; its public
`WorldSiloApplication.RunAsync` permits an outer distribution to add host services.
Neither the desktop World nor the silo installs MCP implicitly.

## Local stdio

Build World and CLI in Release, start World normally, then enter:

```text
world.control start
```

It prints an attachment file path. Configure the MCP client to launch:

```text
dotnet <checkout>/src/Puck.Cli/bin/Release/net10.0/Puck.Cli.dll mcp --profile operator --attach <printed-file>
```

A published CLI uses `puck mcp` with the same arguments. Paths containing spaces
must remain one argument in the client configuration. `world.control status`
repeats the path; `world.control stop` closes attachments. Starting an already
started endpoint retains the same path. Stopping and starting creates a new
incarnation and path. These verbs require the host Console principal and work
without restarting World.

The Operator attachment uses the
[control transport](../Puck.Hosting/README.md#local-console-attachment) to authenticate loopback using a
protected per-user capability file. Do not share its contents. The operator has
the full existing Console registry and ordinary host permissions. There is no
MCP verb allowlist or per-command approval layer. Participant profiles remain
future work; other local profiles are usage errors.

## Remote HTTP and OAuth

Start local control as above. Copy [remote.example.json](remote.example.json),
replace its deployment placeholders, and run:

```text
puck mcp --http remote.json
```

Point an OAuth-capable MCP client at the configured `publicUrl`. The gateway
publishes protected-resource discovery at
`/.well-known/oauth-protected-resource/mcp` and challenges unauthenticated requests
with its canonical metadata URL. Login, PKCE and token issuance belong to the
client and the configured authorization server. Puck validates signed JWT access
tokens using HTTPS OIDC discovery and rotating issuer keys. Opaque-token
introspection is not implemented.

Use the existing Entra tenant's exact v2 issuer, API application/client ID as
`audience`, `subjectClaim: "oid"`, and its tenant UUID as `tenantId`. Register or
reuse an API exposing the delegated `puck.operator` scope and issuing v2 access
tokens. Set `scope` to the short signed `scp` value, and `authorizationScope` to
the full requested scope, such as `api://<api-client-id>/puck.operator`. Configure
the MCP client's pre-registered application ID, redirect URI and delegated API
permission/consent in Entra. Use authorization code with PKCE; the gateway does
not register clients or issue refresh tokens. For a different OIDC issuer, use
`sub` and its signed `scope` claim; `authorizationScope` defaults to `scope`.
See Microsoft's [API claim validation](https://learn.microsoft.com/en-us/entra/identity-platform/claims-validation)
and [authorization code flow](https://learn.microsoft.com/en-us/entra/identity-platform/v2-oauth2-auth-code-flow).

`allowedSubjects` is an explicit grant of **full World Console and framebuffer
authority**, including commands with host filesystem or process effects. Signing
in alone does not grant it. Every request checks signature, exact issuer,
audience, lifetime, delegated scope, subject and configured tenant. App-only
role tokens are refused. Access tokens never enter Console commands or World
state. An explicitly installed service adapter receives the current validated
assertion solely for downstream OAuth exchange. Existing host Console commands
retain their normal credentials and authority.

The owned-silo CLI composition can install `puck_service_observe` through optional
`services` settings. It reuses [Azure delegated observations](../Puck.World.Azure/README.md#delegated-observations)
for inventory and metrics reads, with per-observation subject grants and field
allowlists. Entra exchanges the user's API assertion plus the configured managed
identity's federated client assertion for an ARM token. Failed consent or expiry
returns an error; it never substitutes host authority. Service calls are bounded
by the current request, token expiry and live grant revocation. Assertions and
downstream tokens are not persisted. These reads do not create durable cloud jobs
or cloud writes; mutation services still require their scoped operation and
recovery composition.

`publicUrl` must be HTTPS and end in `/mcp`. A loopback HTTP `listenUrl` is for a
TLS reverse proxy on the same machine. Preserve the public Host header, disable
response buffering, and allow at least the 125-second request deadline. Direct
HTTPS accepts an IP-literal listener and requires a PFX `certificatePath`; its
optional password comes from `certificatePasswordEnvironmentVariable`. Relative
file paths resolve beside the configuration file. Public plaintext listeners,
unknown Hosts and unapproved browser Origins are refused. Add browser clients'
exact HTTPS origins to `allowedOrigins` when required. Forwarded headers cannot
change public identity. Grants and the local capability path reload every second.
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
attachments; excess work fails immediately. In local capability mode these also
consume the host's four control slots alongside local clients. Each attachment admits one operation. Idle attachments
expire after `idleTimeoutSeconds` (default 300, range 10–3600). Every HTTP request
has a 125-second ceiling shortened to its token expiry; exec/capture retain their
own shorter deadlines. Cancellation or expiry during a dispatched operation
closes its attachment, leaving any already-applied effects uncertain. An idle
handle can be resumed by the same subject with a fresh valid token before its
idle deadline. Restart, explicit detach or unknown host outcomes invalidate it.
Never automatically reattach and replay a mutation. HTTP bodies are limited to
64 KiB, headers to 16 KiB, connections to 64, with a ten-second header deadline.

## Tools and results

| Tool | Arguments | Result |
|---|---|---|
| `puck_exec` | Required `command`: one console line, at most 8192 characters. Optional integer `timeoutMs`: 1–120000, default 30000. | Console output plus structured decimal-string `requestId`, `status`, `output`, `isError`, `clearTranscript`. Empty output is `submitted`, not authoritative application. |
| `puck_capture_frame` | Optional `timeoutMs`, same range/default. No path. | Completed PNG image content, up to 16 MiB, plus completion metadata. Includes the composed view and overlays; requires an initialized renderer. |

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
protocol's rules; the adapter sends no extra late result. Restart and inspect
state before deciding whether to retry. Nothing is automatically replayed.
EOF and adapter crashes release the attachment while World and its recordings
retain their own lifetimes. Explicit `quit` and reload keep their normal meaning.

## SDK and verification

The adapter uses official **ModelContextProtocol.Core/ASP.NET Core 2.2.0** and
ASP.NET Core JWT bearer authentication 10.0.11, with explicit schemas and low-level
handlers. It installs no Harness or model provider.
Result metadata uses a typed, source-generated serializer; standalone schemas use
.NET 10's `JsonElement.Parse`. The SDK owns the asynchronous message channel.
Admission counters bound that transport without adding another queue; a single
console result uses a completion source, and the ordinary engine pump keeps its
synchronous drain and session barriers.
World uses `Puck.Hosting` and `Puck.Networking`; CLI references this optional adapter.
The SDK stays outside the base engine. Networking and the adapter retain exact
architecture profiles, and all three libraries retain AOT/trim analysis. MCP input
is strictly UTF-8 with a 64 KiB JSON-RPC line ceiling before SDK buffering. At most
128 nonblank messages may await SDK consumption; malformed messages consume that
budget too. Stdout admits at most four pending replies with a five-second send
deadline, including time waiting for the SDK's send lock. Exceeding either budget
or failing an input/output operation closes the attachment and exits with failure.
Clean EOF exits successfully. The adapter owns both streams and closes them on
shutdown, including when a pending read ignores cancellation. Stdout is protocol-only;
diagnostics use stderr.

Official C# SDK 2.2.0 clients launch the real CLI in the tests. The target is
`2026-07-28` discovery/per-request metadata:
listing, exec, images, argument errors, cancellation, timeout, EOF and reconnect.
Adversarial tests also cover split UTF-8 characters, invalid encodings, oversized
and malformed input, stalled output, reply flooding and cancellation-resistant reads.
Both servers explicitly select this revision. Remote tests use real Kestrel,
signed JWTs, OIDC discovery/JWKS, direct TLS, tenant/subject isolation, scope and
audience refusal, token expiry, cancellation, idle expiry, capacity and shutdown.
Raw HTTP tests check required metadata, header mismatches and absent session
endpoints. This is not a claim that every IDE client or the live Entra tenant
has been tested. The protocol contract is the
[current specification](https://modelcontextprotocol.io/specification/2026-07-28).

A real Direct3D smoke in offscreen and windowed presentation submitted a dynamics
parameter edit (the normal host reported it applied), decoded PNGs through the
producer and unified overlay, answered human console status during an agent wait,
and captured again after adapter restart. Existing render
writer boundary tests remain in Commands, Abstractions and Shaders. A physical
device-loss event or live Entra deployment is not claimed by this run.

The remote offscreen smoke exercised the same edit and decoded a 640×480 RGBA PNG
over HTTPS using signed Entra-shaped JWTs and controlled OIDC discovery. Human
input answered during an attachment wait; cancellation and a fresh attachment
allowed another capture. This verifies the complete gateway-to-renderer path,
while leaving live tenant registration, consent and deployment to their own check.

Run `dotnet test tests/Puck.Cli.Tests -c Release` for SDK interop and the
[Hosting verification](../Puck.Hosting/README.md#-verification) for engine attachment contracts.
For a live smoke, edit a parameter through MCP and decode the captured PNG; hold
this attachment with `world.wait` and confirm human input still answers. Close
the adapter during the wait, reconnect with the same file, then capture again.
Stopping control must remove discovery without stopping World; headless hosts
must refuse capture. These checks run the ordinary engine, with no alternate
simulation loop. See the
[remaining plan](../../docs/reviews/puck-mcp-plan.md).
