# Puck.Azure

Puck.Azure contains shared Azure integration for application hosts: Data
Protection key-ring registration, its health check, on-behalf-of identity
utilities, and Azure Monitor export that finishes before the host exits. Hosts
use these helpers without duplicating their Azure SDK setup.

## Usage

`DataProtectionExtensions.TryAddDataProtection` reads the configured blob and
Key Vault key URIs. When both are supplied as valid absolute URIs, it registers
Azure-backed key persistence and protection. Otherwise it registers the default
Data Protection services. Hosts that exchange protected payloads must use the
same application name. The helper also registers the Data Protection health
check and returns whether the Azure key-ring configuration was selected.

`IdentityUtilities.ToOnBehalfOfCredential` creates a credential from the calling
credential, application/tenant options, and a user assertion. The client
assertion credential has its own keyed-service registration so a host can
choose it independently from the ambient Azure credential.

`AzureMonitorTelemetryExtensions.UseAzureMonitorExporterWithBoundedShutdown`
exports a host's OpenTelemetry signals to Azure Monitor and makes sure the last
batch leaves the process before it exits. A provider's own disposal waits only
five seconds for a send in flight, so once every hosted service has stopped, the
host shuts the providers down itself and waits up to twenty seconds. Each
transmission fails after ten seconds, so that wait always ends: the batch is sent,
or a warning says it was not. The host's runtime configuration must also set the
exporter's `DisablePersistOnShutdown` switch. Without it, a shutting-down provider
writes its batch to local disk instead of sending it. Telemetry recorded after the
providers shut down, while the container is disposed, is not exported.

The [Functions host](../Puck.Azure.Functions/README.md) is a consumer. This
library does not provision resources or host HTTP endpoints; the
[resource project](../Puck.Azure.Resources/README.md) owns infrastructure.
Reflection-based SDK and key-ring serialization also mean the project does
not enable the repository's trimming and Native AOT compatibility checks.

Run the [Puck.Azure.Tests](../../tests/Puck.Azure.Tests/README.md) suite after
changing the telemetry export.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Development](../../docs/development/README.md)
