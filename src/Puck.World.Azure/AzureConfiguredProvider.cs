using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Identity;
using Azure.ResourceManager;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>The explicitly installed azure.resource provider type for declarative host composition.</summary>
public sealed class AzureConfiguredProvider : IWorldConfiguredProvider, IWorldConfiguredObservationProvider {
    private readonly TokenCredential m_credential;
    private readonly ArmEnvironment m_environment;
    private readonly List<AzureResourceOperationProvider> m_operations = [];
    private bool m_disposed;

    /// <summary>Gets the catalog registration. Merely registering or configuring it makes no service calls.</summary>
    public static WorldExtensionProviderType Registration { get; } = new("azure.resource", settings => new AzureConfiguredProvider(settings));

    private AzureConfiguredProvider(JsonElement settings) {
        var configuration = settings.Deserialize(AzureConfiguredProviderJson.Default.AzureConfiguredProviderSettings)
            ?? throw new JsonException("Azure provider settings are required.");
        m_environment = configuration.Cloud switch {
            "public" => ArmEnvironment.AzurePublicCloud,
            "government" => ArmEnvironment.AzureGovernment,
            "china" => ArmEnvironment.AzureChina,
            _ => throw new ArgumentException("Azure cloud must be public, government, or china."),
        };
        m_credential = configuration.Authentication switch {
            "managedIdentity" when configuration.TenantId is null => new ManagedIdentityCredential(configuration.ClientId is { } clientId
                ? ManagedIdentityId.FromUserAssignedClientId(clientId) : ManagedIdentityId.SystemAssigned),
            "azureCli" when configuration.ClientId is null => new AzureCliCredential(new AzureCliCredentialOptions { TenantId = configuration.TenantId }),
            _ => throw new ArgumentException("Select managedIdentity (optional clientId) or azureCli (optional tenantId). No implicit credential chain is used."),
        };
    }

    /// <inheritdoc/>
    public WorldExtensionOperation Bind(string name, string description, JsonElement settings) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        var configuration = settings.Deserialize(AzureConfiguredProviderJson.Default.AzureConfiguredOperationSettings)
            ?? throw new JsonException("Azure operation settings are required.");
        if (!Enum.TryParse<AzureResourceMethod>(configuration.Method, true, out var method) || !Enum.IsDefined(method) ||
            !string.Equals(method.ToString(), configuration.Method, StringComparison.OrdinalIgnoreCase)) {
            throw new ArgumentException("Azure method must be post, patch, delete, or put.");
        }
        var provider = new AzureResourceOperationProvider(m_credential, new(name, configuration.ResourceId,
            configuration.Incarnation, method, configuration.ApiVersion, configuration.Action, configuration.IfMatch,
            configuration.QueryParameters), environment: m_environment);
        try {
            var registration = provider.Register(description);
            m_operations.Add(provider);
            return registration;
        } catch { provider.Dispose(); throw; }
    }

    /// <summary>Disposes bound adapters and the selected credential.</summary>
    public void Dispose() {
        if (m_disposed) { return; }
        m_disposed = true;
        foreach (var operation in m_operations) { operation.Dispose(); }
        (m_credential as IDisposable)?.Dispose();
    }

    /// <inheritdoc/>
    public IWorldExtensionObservationSource BindObservation(JsonElement settings, int maximumItems) {
        ObjectDisposedException.ThrowIf(m_disposed, this);
        return new AzureResourceInventory(m_credential, m_environment, settings, maximumItems);
    }
}

internal sealed record AzureConfiguredProviderSettings(string Authentication, string? ClientId = null,
    string? TenantId = null, string Cloud = "public");
internal sealed record AzureConfiguredOperationSettings(string ResourceId, string Incarnation, string Method,
    string ApiVersion, string? Action = null, string? IfMatch = null, IReadOnlyDictionary<string, string>? QueryParameters = null);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AzureConfiguredProviderSettings))]
[JsonSerializable(typeof(AzureConfiguredOperationSettings))]
internal sealed partial class AzureConfiguredProviderJson : JsonSerializerContext;
