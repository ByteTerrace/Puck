# Puck.Azure

Puck.Azure contains shared Azure integration for application hosts: Data
Protection key-ring registration, its health check, and on-behalf-of identity
utilities. Hosts use these helpers without duplicating their Azure SDK setup.

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

The [Functions host](../Puck.Azure.Functions/README.md) is a consumer. This
library does not provision resources or host HTTP endpoints; the
[resource project](../Puck.Azure.Resources/README.md) owns infrastructure.
Reflection-based SDK and key-ring serialization also mean the project does
not enable the repository's trimming and Native AOT compatibility checks.

## Documentation

📚 [CI and releases](../../docs/development/ci.md) · 🛠️ [Development](../../docs/development/README.md)
