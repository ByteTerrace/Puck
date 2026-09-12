# Azure.Resources

[![Board Status](https://dev.azure.com/byteterrace/0fdb7e64-61cc-4f63-b6aa-ee63e5426233/e42b904c-8125-438b-8415-988be75611ea/_apis/work/boardbadge/7cc7ad7f-7b3c-4702-8791-ca64a83d02cb?columnOptions=1)](https://dev.azure.com/byteterrace/0fdb7e64-61cc-4f63-b6aa-ee63e5426233/_boards/board/t/e42b904c-8125-438b-8415-988be75611ea/Stories/)  
[![Release Status](https://dev.azure.com/byteterrace/Koholint/_apis/build/status%2FDeploy%20Infrastructure?branchName=main)](https://dev.azure.com/byteterrace/Koholint/_build/latest?definitionId=88&branchName=main)

This repository contains a comprehensive **Infrastructure-as-Code (IaC)** foundation for deploying a secure application environment within Microsoft Azure.

## ✨ Key Features

- *Zero Trust:* Uses one CI deployment identity and scoped runtime permissions, including repository ABAC, with managed identities and OIDC.
- *Virtual Networking:* Supports private endpoints, subnet integration, and service-specific network restrictions.
- *HTTP Delivery:* Uses Azure Front Door with Web Application Firewall (WAF); the world silo exposes QUIC separately.
- *Dynamic DNS:* Automatically maintains both public and private DNS zones.
- *Azure DevOps Integration:* Includes managed agent pools to provide self-hosted CI/CD agents that can securely deploy into the virtual network.
- *Serverless Compute:* Uses Azure Functions Flex Consumption for scalable, event-driven API logic.

## 📐 Architecture

::: mermaid
graph TB
    AzureDevOps(["🔗 dev.azure.com/&lt;org&gt;"])
    Internet(["🌐 Internet"])

    FrontDoor(["🛡️🚪 Front Door + WAF"])
    DnsApi(["🔗 api.&lt;domain&gt;.com"])
    DnsDevOps(["🔗 devops.&lt;domain&gt;.com"])
    DnsPortal(["🔗 portal.&lt;domain&gt;.com"])
    FunctionApp(["⚙️ Function App"])
    RedisCache(["🪣 Redis Cache"])
    StorageAccountFunction(["🗄️ Storage Account (Function)"])
    StorageAccountPublic(["🗄️ Storage Account (Public)"])
    ConfigurationStore(["🎛️ Configuration Store"])
    KeyVault(["🔐 Key Vault"])
    DevOpsPool(["🖥️ DevOps Pool"])

    subgraph Networking ["🖧 Virtual Network"]
        DevOpsPoolSubnet(["🕸️ DevOps Pool Subnet"])
        FunctionAppSubnet(["🕸️ Function App Subnet"])
        PrivateEndpointSubnet{{"🕸️ Private Endpoint Subnet"}}
        NatGateway(["🚪➡️ NAT Gateway"])
    end

    Internet --> FrontDoor
    FrontDoor --> DnsApi
    FrontDoor --> DnsDevOps
    FrontDoor --> DnsPortal
    DnsApi --> |🌐🔒 HTTPS| FunctionApp
    DnsDevOps --> |🌐🔒 HTTPS| AzureDevOps
    DnsPortal --> |🌐🔒 HTTPS| StorageAccountPublic
    FunctionApp -.-> |🕸️🔒 PE| ConfigurationStore
    FunctionApp -.-> |🕸️🔒 PE| RedisCache
    FunctionApp -.-> |🕸️🔒 PE| StorageAccountFunction
    FunctionApp --> FunctionAppSubnet
    ConfigurationStore -.-> |🕸️🔒 PE| KeyVault
    RedisCache -.-> |🕸️🔒 PE| KeyVault
    StorageAccountFunction -.-> |🕸️🔒 PE| KeyVault
    StorageAccountPublic -.-> |🕸️🔒 PE| KeyVault
    DevOpsPool <--> DevOpsPoolSubnet
    DevOpsPoolSubnet --> NatGateway
    FunctionAppSubnet --> NatGateway
    NatGateway --> Internet
:::

## ⚠️ Prerequisites

- An [Azure](https://azure.microsoft.com/en-us/resources/cloud-computing-dictionary/what-is-azure) subscription with the permissions required to create a resource group and assign roles.
- An [Azure DevOps](https://learn.microsoft.com/en-us/azure/devops/user-guide/what-is-azure-devops) organization that is [integrated with Microsoft Entra ID](https://learn.microsoft.com/en-us/azure/devops/organizations/accounts/connect-organization-to-azure-ad) and a project that one has ownership rights to.
- [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/what-is-azure-cli) v2.70 (or greater) installed.
- [PowerShell](https://learn.microsoft.com/en-us/powershell/scripting/overview) v7.2 (or greater) installed.

## 🧭 Getting Started

Use the provided [📋checklist](./CHECKLIST.md) to help track your progress.

The current monorepo entry point is the
[Azure GitHub Actions workflow](../../docs/development/ci.md#azure-production-deployment).
It pins Bicep 0.46.1 and authenticates with the CI managed identity before
restoring the published, versioned `ts/bvm` Template Specs. The `bvm` alias in
`bicepconfig.json` selects their subscription and resource group; the local
`avm-temp` sources do not replace those published deployment dependencies. The
`main.bicep` template owns the existing production platform. Supply `actorsImage` as an immutable registry digest
when updating the shared platform with a new actor release.

The `.azure-devops/pipelines` definitions describe the older standalone
repository deployment. Their checkout paths and service connections are not
the GitHub Actions deployment contract. A new platform still needs the identity
and access setup described in the [checklist](./CHECKLIST.md) and bootstrap below.

### Bootstrap Process

#### 📦 *.NET*

The identity team runs [bootstrap.cs](./bootstrap.cs) from this directory with
.NET 10 and its operator credentials. It creates the CI identity and federation,
authors its constrained Owner delegation and Graph grants, and prepares the
groups, custom roles, Azure DevOps connection and Template Specs. Application CI
consumes this setup. See the [deployment access contract](../../docs/development/ci.md#azure-production-deployment).

The script header lists its required platform arguments. For the existing Puck
GitHub environment, also supply:

```text
--managed-identity-name bytrcidpzzz
--github-credential-name 77854d2e-f1a3-4c91-bd8b-b05a5e2b9608
--github-subject repo:ByteTerrace@18753984/Puck@1271519029:environment:Puck
```

Reuse the existing credential name when reconciling federation. Compile without
executing setup using `dotnet build bootstrap.cs -p:PublishAot=false`; run setup
with `dotnet run --file bootstrap.cs --` followed by the required arguments.

### Naming and module configuration

Azure resource names follow `<prefix><resource abbreviation>p<index>` in the
production parameter file. For example, `bytrccap001` is the Actors Container App,
`bytrcvmssp000` is the world VM scale set, and `bytrcidp008` is its managed
identity. Existing resource names remain stable. Human names belong in `PetName`
tags, such as `Puck Actors` and `Puck World`. Shared `Application`, `Environment`
and `ManagedBy` tags are combined with resource-specific tags wherever supported.
DNS names and identity-owned blob container names retain their service contracts.

Role assignments use AVM's `guid(scopeResourceId, principalId, roleDefinitionId)`
formula, with the fully qualified role-definition ID also used in the assignment
properties. The resource-role-assignment helper supplies this default. Existing
grants with older names need a one-time operator migration before deployment;
Azure cannot rename an assignment or create its duplicate under a new name.
One-time repairs stay outside the repository and CI. The bootstrap remains the
repeatable identity-team setup contract.

## Remote MCP

`main.bicepparam` accepts `BICEPPARAM_WORLD_MCP` as a JSON object with
`participants: [{ "subject": "<Entra user oid>", "grants": [] }]`, optional
`observations` in the [delegated format](../Puck.World.Azure/README.md#delegated-observations),
and optional `testLocations` for Azure availability probes. Null disables MCP.
Each participant explicitly receives Replica disclosure for the configured World;
the same list generates gateway access and authored OAuth admission. Empty grants
permit no World writes. Additional grants use the ordinary admission schema.
The root exposes delegated `user_impersonation` on the existing Entra application and
supplies the World managed identity's federated credential. The existing Function
onboarding endpoint and ARM delegation still require user consent.

The existing load balancer maps public TCP 443 to Caddy 2.11.4 on 8443. Caddy uses
ACME TLS-ALPN-01 to issue, renew and hot-swap the certificate, forwarding only to
the loopback MCP listener. No DNS plugin, stored Azure credential, PFX secret,
port 80 listener or renewal restart is needed. Certificate/account state persists
in `/var/lib/puck-tls` across releases, under a separate container UID, outside
the World container's mounts. Keep that directory across ordinary upgrades.
DNS must resolve to this load balancer and CAA policy must permit Let's Encrypt.
The release probe validates the public hostname and certificate before succeeding.
Azure availability tests check HTTPS readiness and seven days of certificate
lifetime every fifteen minutes, alerting the existing World hosting action group.

This avoids an additional paid gateway and network hop for the current single
worker. It does not turn the worker into a highly available cluster: the release
lane still refuses multiple authoritative workers. The CLI hosts MCP as a silo
extension in the same published image; base silo assemblies have no MCP dependency.
For an existing World platform, apply MCP policy through the shared
[worldMcp.bicep](./worldMcp.bicep) module without redeploying unrelated resources:

```powershell
# Set BICEPPARAM_WORLD_MCP to the participant/observation JSON above.
puck azure deploy-world-mcp --plan-only
puck azure deploy-world-mcp
```

The command resolves the existing API and World identity from deployed outputs,
checks the enabled delegated scope, retains the what-if plan and refuses deletion.
It installs the same identity trust and monitoring used by `main.bicep`, then
merges the resulting MCP policy into deployment outputs. Publish the candidate
image and run the ordinary `deploy-world` release lane to apply runtime policy,
with its drain, persistence snapshot, verification and rollback. Later World
platform deployments preserve the newest successful MCP policy. An ABAC-enabled
ACR quick build needs `--source-acr-auth-id '[caller]'` to use the signed-in Entra
identity; do not enable registry passwords.

Compilation and local tests do not issue certificates, deploy resources or grant live consent.
Deployment choices belong in `main.bicepparam`. Typed configuration objects carry
names, tags, DNS settings, ports and capacity into modules. Deployment scripts use
the same configuration through Bicep outputs. Module sources retain protocol and
schema constants but contain no production-specific silo names or hostnames.

Public AVM references pin the latest published versions validated by this change.
Reusable platform patterns are deployed Template Specs under `ts/bvm`; the alias
continues to select the existing `byteterrace` registry. Their source paths,
versions and dependency order are recorded in [template-specs.json](./template-specs.json).
Publish changed patterns before updating application deployments:

```powershell
puck azure publish-template-specs
```

Run that command from the repository root after authenticating Azure CLI and
installing the CI-pinned Bicep version. Bootstrap calls the same publisher.
The publisher compiles sources in dependency order, skips identical published
versions and rejects changed content under an existing version. Bump the manifest
version and consuming Bicep references together; existing published versions are
preserved. Application CI consumes these pins without bootstrapping identities.
