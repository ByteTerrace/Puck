# Puck.Azure.Functions

Puck.Azure.Functions is Puck's Azure Functions application. It composes HTTP
and Event Grid handlers with identity, storage, attestation, caching, feature
gates, and telemetry. It is a hosted application rather than a reusable engine
package.

## Structure

HTTP handlers live in `HttpTriggers/`; event handlers live in
`EventGridTriggers/`. `Middleware/` contains authorization, feature gating,
targeting context, and exception handling. `Services/` holds the supporting
storage and identity operations. `Program.cs` composes the host and reads its
configuration.

The host uses [Puck.Azure](../Puck.Azure/README.md) for shared identity and
Data Protection helpers, [Puck.Storage](../Puck.Storage/README.md) for storage
integration, and [Puck.Attestation](../Puck.Attestation/README.md) for signed
claims. The [resource project](../Puck.Azure.Resources/README.md) describes the
infrastructure those services use.

## Configuration and deployment

Follow [CI and releases](../../docs/development/ci.md) for shared deployment
identity, infrastructure, and release procedures. The application requires its
host configuration and Azure services; building the project alone does not
verify a deployed identity, storage policy, or endpoint. Keep credentials out
of source-controlled configuration.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Development](../../docs/development/README.md)
