# Puck.World.AgentHarness.Azure

This optional extension gives the
[agent harness](../Puck.World.AgentHarness/README.md#running-as-a-world-participant)
a model: an Azure OpenAI chat deployment, which is also how Azure AI Foundry
serves its OpenAI models. It contributes one `ChatClientProvider` named
`azure.openai`.

The provider calls the resource's OpenAI v1 endpoint (`<endpoint>/openai/v1/`)
with the OpenAI client, carrying a bearer token for the Cognitive Services scope.
It signs in with identity, never a key. It uses a
`DefaultAzureCredential`: a managed identity on a host, or your own Azure sign-in
on a workstation. Its settings have no key member, so a configuration that adds
one, such as `apiKey`, is refused by name.

## Installation

Publish the project into a host's extensions directory beside
`Puck.World.AgentHarness`:

```text
dotnet publish src/Puck.World.AgentHarness -c Release -o extensions/Puck.World.AgentHarness
dotnet publish src/Puck.World.AgentHarness.Azure -c Release -o extensions/Puck.World.AgentHarness.Azure
```

`world.extensions.catalog` then lists
`Puck.World.AgentHarness.Azure: ChatClientProvider azure.openai`. Installing it
does nothing until an extension configuration lists an `agent.harness`
participant; see
[Run an agent participant](../Puck.World.Server/ExtensionConfiguration.md#run-an-agent-participant).

## Settings

A participant's `providerSettings` hold:

| Setting | Meaning |
|---|---|
| `endpoint` | The resource endpoint, an absolute `https` URI such as `https://<resource>.openai.azure.com/`. Required. |
| `deployment` | The chat model deployment name. Required. |
| `tenantId` | The tenant the credential signs in to, when it is not the default. |
| `managedIdentityClientId` | A user-assigned managed identity's client ID, when the host has more than one identity. |

The identity needs a data-plane role on the resource that allows chat
completions, such as Cognitive Services OpenAI User. Creating the client makes no
service call: a wrong endpoint or a missing role shows up as the participant's
failure in `world.extensions`, not as a startup refusal.

The OpenAI client marks the constructor that accepts a token credential as
evaluation-only (`OPENAI001`). This project suppresses that diagnostic at its one
call site, because it is the only way the client authenticates with identity; a
change to that constructor in a later OpenAI package shows up as a build error
here. `Azure.AI.OpenAI` is not used: its current release calls an OpenAI client
constructor that the OpenAI package no longer ships, and fails when it runs.

## Verification

```text
dotnet test tests/Puck.World.Agents.Tests -c Release --filter FullyQualifiedName~AzureOpenAiChatClientLawTests
dotnet test tests/Puck.World.Tests -c Release --filter FullyQualifiedName~ExtensionModelLawTests
```

The first shows that the provider registers under its name, creates a client
without calling the service, and refuses a key, a non-`https` endpoint, a missing
endpoint, and a blank deployment. The second shows that an installed harness
selects this installed provider across load contexts, and that a local World and
a silo compose them identically. No test reaches a live Azure resource.

## Documentation

📚 [Engine manual](../../docs/README.md) · 🛠️ [Development](../../docs/development/README.md)
