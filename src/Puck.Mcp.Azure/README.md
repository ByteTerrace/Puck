# Puck.Mcp.Azure

This optional extension serves Puck's Azure delegated services over
[remote MCP](../Puck.Mcp/README.md#host-extension): platform onboarding
(`puck_onboard`) and granted Azure inventory and metrics observations
(`puck_service_observe`). Each call reaches Azure on behalf of the signed-in
caller through a request-confined on-behalf-of exchange; host credentials are
never substituted. The services themselves live in
[Puck.World.Azure](../Puck.World.Azure/README.md#delegated-observations); this
project only connects them to MCP.

## Installation

Publish the project into a host's extensions directory beside `Puck.Mcp`, as the
silo image does:

```text
dotnet publish src/Puck.Mcp.Azure -c Release -o extensions/Puck.Mcp.Azure
```

`AzureMcpExtension` contributes one `McpServicesProvider` under the key `azure`.
It does nothing until the remote MCP configuration has a `services` member, which
holds the delegated-services settings (`managedIdentityClientId`, `observations`,
and optionally `onboardingUrl`). Remote MCP then selects this provider, and
refuses to start if another installed extension also provides one. Without
`services`, remote MCP attaches callers to its Console target and this extension
is unused. `world.extensions.catalog` lists it as
`Puck.Mcp.Azure: McpServicesProvider azure`.

The configuration must use the tenant-specific Entra public-cloud issuer, the
`oid` subject claim and an explicit `tenantId`; any other identity setup is
refused when the provider starts. With a `target`, callers can also attach to the
Console, and attaching first checks onboarding when `onboardingUrl` is set. With
`services` and no `target`, the host is service-only and offers no Console tools.

Nothing in the base engine references this project. `Puck.Mcp` and the silo stay
free of Azure: this assembly reaches them only through the extension load context,
which gives it the installed `Puck.Mcp`'s own types. See
[Extensions](../../docs/reference/extensions.md) for discovery and refusals.

## Authorization

Every Entra exchange runs in `AuthorizeAsync`, before the MCP call is dispatched,
so a sign-in, consent or claims requirement is answered with an HTTP 401 challenge.
A downstream API that refuses the exchanged token once the call is running, as
continuous access evaluation does after a revocation, fails that call and
challenges the caller's next request. Observation names are disclosed per caller:
discovery lists only the observations granted to that subject, as an enum on the
tool's input schema.

## Verification

```text
dotnet test tests/Puck.World.Tests -c Release --filter FullyQualifiedName~ExtensionModelLawTests
dotnet test tests/Puck.World.Azure.Tests -c Release
```

The extension-model laws check that this provider is selected for a `services`
deployment, that `services` with none or two providers is refused, and that an
installed `Puck.Mcp` and `Puck.Mcp.Azure` share the provider type across load
contexts. The Azure suite covers the delegated exchanges, challenges and
disclosure limits against scripted services. No test reaches a live tenant.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
