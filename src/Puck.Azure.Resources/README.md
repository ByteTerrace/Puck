# Azure.Resources

[![Board Status](https://dev.azure.com/byteterrace/0fdb7e64-61cc-4f63-b6aa-ee63e5426233/e42b904c-8125-438b-8415-988be75611ea/_apis/work/boardbadge/7cc7ad7f-7b3c-4702-8791-ca64a83d02cb?columnOptions=1)](https://dev.azure.com/byteterrace/0fdb7e64-61cc-4f63-b6aa-ee63e5426233/_boards/board/t/e42b904c-8125-438b-8415-988be75611ea/Stories/)  
[![Release Status](https://dev.azure.com/byteterrace/Koholint/_apis/build/status%2FDeploy%20Infrastructure?branchName=main)](https://dev.azure.com/byteterrace/Koholint/_build/latest?definitionId=88&branchName=main)

This repository contains a comprehensive **Infrastructure-as-Code (IaC)** foundation for deploying a secure application environment within Microsoft Azure.

## ✨ Key Features

- *Zero Trust:* Implements a strict RBAC only approach using managed identities + OIDC for passwordless authentication between all services.
- *Virtual Networking:* All non-public resources are isolated from the internet and accessed exclusively via private endpoints.
- *Global Scale & Protection:* Uses Azure Front Door with Web Application Firewall (WAF) as the single global entry point.
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

1) Download [bootstrap.cs](./bootstrap.cs) script from repository.
2) Run script via dotnet.

