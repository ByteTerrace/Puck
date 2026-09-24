using System.Text.Json;
using System.Text.Json.Serialization;
using System.ClientModel.Primitives;
using Azure.Identity;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using Puck.Abstractions;
using Puck.World.Agents.Harness.Azure;

[assembly: PuckExtension(typeof(AzureOpenAiChatClientExtension))]

namespace Puck.World.Agents.Harness.Azure;

/// <summary>Contributes the <c>azure.openai</c> <see cref="ChatClientProvider"/>: an Azure OpenAI (Azure AI Foundry)
/// chat deployment reached with identity through the resource's OpenAI v1 endpoint (<c>&lt;endpoint&gt;/openai/v1/</c>),
/// with a bearer token for <see cref="TokenScope"/>. The credential is a <see cref="DefaultAzureCredential"/> — a managed identity
/// on a host, a developer sign-in on a workstation — and the settings have no key member, so an unmapped
/// <c>apiKey</c> is refused by name. Registration and client creation make no service call.</summary>
public sealed class AzureOpenAiChatClientExtension : IPuckExtension {
    /// <summary>The provider name a participant's settings select.</summary>
    public const string ProviderName = "azure.openai";
    /// <summary>The token scope Azure OpenAI accepts for a bearer token.</summary>
    public const string TokenScope = "https://cognitiveservices.azure.com/.default";

    /// <inheritdoc/>
    public string Name => "Puck.World.AgentHarness.Azure";

    /// <summary>Validates the settings and creates the chat client for one deployment.</summary>
    /// <param name="settings">The provider settings: <c>endpoint</c> (the resource's absolute https URI),
    /// <c>deployment</c> (the model deployment name), and optionally <c>tenantId</c> and
    /// <c>managedIdentityClientId</c>, which narrow the default credential.</param>
    /// <returns>The chat client; the caller owns and disposes it.</returns>
    /// <exception cref="ArgumentException">A setting is missing, unknown, or malformed; the message names it.</exception>
    public static IChatClient Create(JsonElement settings) {
        AzureOpenAiChatClientSettings parsed;

        try {
            parsed = (settings.Deserialize(jsonTypeInfo: AzureOpenAiChatClientJson.Default.AzureOpenAiChatClientSettings)
                ?? throw new JsonException(message: "settings must be an object."));
        } catch (JsonException error) {
            throw new ArgumentException(message: error.Message);
        }
        if (
            !Uri.TryCreate(
                result: out var endpoint,
                uriKind: UriKind.Absolute,
                uriString: parsed.Endpoint
            ) ||
            (endpoint.Scheme != Uri.UriSchemeHttps)
        ) {
            throw new ArgumentException(message: $"endpoint '{parsed.Endpoint}' must be an absolute https URI.");
        }
        if (string.IsNullOrWhiteSpace(value: parsed.Deployment)) { throw new ArgumentException(message: "deployment must be non-blank."); }
        var credential = new DefaultAzureCredential(options: new DefaultAzureCredentialOptions {
            ManagedIdentityClientId = parsed.ManagedIdentityClientId,
            TenantId = parsed.TenantId,
        });

        // OPENAI001 marks the OpenAI client's authentication-policy constructor as evaluation-only; it is the one way the
        // client takes a TokenCredential, and identity is the only authentication this provider offers.
#pragma warning disable OPENAI001
        return new ChatClient(
            authenticationPolicy: new BearerTokenPolicy(
                scope: TokenScope,
                tokenProvider: new AzureTokenProvider(
                    credential: credential,
                    scope: TokenScope
                )
            ),
            model: parsed.Deployment,
            options: new OpenAIClientOptions {
                Endpoint = new Uri(
                baseUri: endpoint,
                relativeUri: "openai/v1/"
            ),
            }
        ).AsIChatClient();
#pragma warning restore OPENAI001
    }
    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        registry.AddChatClient(
            name: ProviderName,
            provider: new ChatClientProvider(Create: Create)
        );
    }
}

/// <summary>The <c>azure.openai</c> provider's settings.</summary>
/// <param name="Endpoint">The Azure OpenAI or Azure AI Foundry resource endpoint, an absolute https URI.</param>
/// <param name="Deployment">The chat model deployment name.</param>
/// <param name="TenantId">The tenant the default credential signs in to, or <see langword="null"/> for its default.</param>
/// <param name="ManagedIdentityClientId">The user-assigned managed identity's client ID, or <see langword="null"/> for
/// the system-assigned identity.</param>
internal sealed record AzureOpenAiChatClientSettings(string Endpoint, string Deployment, string? TenantId = null, string? ManagedIdentityClientId = null);
[JsonSerializable(typeof(AzureOpenAiChatClientSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
internal sealed partial class AzureOpenAiChatClientJson : JsonSerializerContext;
