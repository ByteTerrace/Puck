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
[Azure GitHub Actions workflow](../../docs/ci.md#azure-production-deployment).
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
consumes this setup. See the [deployment access contract](../../docs/ci.md#azure-production-deployment).

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

`main.bicepparam` accepts `BICEPPARAM_WORLD_MCP` as a JSON object containing
`allowedSubjects` (Entra user object IDs), `certificateSecretName` (a passwordless
base64 PFX secret for the silo DNS name), and optional `observations` using the
[delegated observation format](../Puck.World.Azure/README.md#delegated-observations).
Null disables the listener. The unified root exposes `puck.operator` on the
existing Entra application and supplies the World identity's federated credential.
Its existing ARM delegated permission supports observation OBO after consent.

The existing silo load balancer maps public TLS 443 to unprivileged 8443. The
bootstrap mounts protected configuration and certificate files, and selects the
CLI's `puck mcp --silo ... --http ...` composition from the same image. Standalone
silo assemblies retain no MCP dependency. The world-only release path preserves
the deployed unified root's MCP outputs; deploy the root before enabling or
changing its OAuth policy. Certificate renewal takes effect through the ordinary
drain-and-restart release. No template deploys or grants tenant consent merely
because it has been compiled.

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
dotnet run -c Release --file build/Azure.cs -- publish-template-specs
```

Run that command from the repository root after authenticating Azure CLI and
installing the CI-pinned Bicep version. Bootstrap calls the same publisher.
The publisher compiles sources in dependency order, skips identical published
versions and rejects changed content under an existing version. Bump the manifest
version and consuming Bicep references together; existing published versions are
preserved. Application CI consumes these pins without bootstrapping identities.
