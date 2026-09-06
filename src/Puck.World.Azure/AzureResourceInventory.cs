using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>A bounded resource-group inventory through generic ARM REST. Only explicitly selected scalar fields leave this adapter.</summary>
/// <remarks>Pagination must complete before publishing. A failed or oversized read never masquerades as an empty collection.
/// This source observes metadata, not blob contents, account keys, health, occupancy, or Orleans grain placement.</remarks>
public sealed class AzureResourceInventory : IWorldExtensionObservationSource {
    private readonly AzureResourceInventorySettings m_settings;
    private readonly int m_maximumItems;
    private readonly HttpPipeline m_pipeline;
    private readonly HttpClient? m_httpClient;
    private readonly Uri m_uri;

    /// <summary>Binds a host-authorized resource-group query without making service calls.</summary>
    /// <param name="credential">Host-owned credential.</param>
    /// <param name="environment">Explicit ARM cloud.</param>
    /// <param name="settings">Strict query scope, filters, and field allowlist.</param>
    /// <param name="maximumItems">Positive maximum matching collection size.</param>
    /// <param name="transport">Optional trusted test transport. Must not follow redirects.</param>
    public AzureResourceInventory(TokenCredential credential, ArmEnvironment environment, JsonElement settings,
        int maximumItems, HttpPipelineTransport? transport = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        m_maximumItems = maximumItems;
        m_settings = settings.Deserialize(AzureResourceInventoryJson.Default.AzureResourceInventorySettings)
            ?? throw new JsonException("Inventory settings are required.");
        var segments = m_settings.ResourceGroup.Split('/');
        if (segments.Length != 5 || segments[0].Length != 0 ||
            !segments[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(segments[2], "D", out _) ||
            !segments[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) || segments[4].Length is < 1 or > 90 ||
            segments[4].EndsWith('.') || segments[4].Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '(' or ')'))) {
            throw new ArgumentException("Inventory requires an absolute subscription/resource-group ID without URI syntax or traversal segments.");
        }
        var scope = new ResourceIdentifier(m_settings.ResourceGroup);
        if (scope.ResourceType.ToString() != "Microsoft.Resources/resourceGroups" ||
            string.IsNullOrWhiteSpace(m_settings.ApiVersion) || m_settings.Fields.Count is < 1 or > 16 ||
            m_settings.MaximumPages is < 1 or > 64 || m_settings.MaximumResponseBytes is < 1024 or > 4194304 ||
            m_settings.ResourceType.Split('/').Any(segment => segment.Length == 0 || segment.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))) {
            throw new ArgumentException("Inventory needs a resource-group ID, resource type, API version, field allowlist, and bounded pages/bytes.");
        }
        foreach (var field in m_settings.Fields) {
            if (field.Key.Length is < 1 or > 128 || field.Value.Length is < 2 or > 512 || field.Value[0] != '/' || field.Value.Contains('~') ||
                field.Value[1..].Split('/').Any(segment => segment.Length == 0)) {
                throw new ArgumentException("Inventory fields use slash-separated property paths without JSON-pointer escape sequences.");
            }
        }
        var endpoint = environment.Endpoint;
        if (endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) {
            throw new ArgumentException("Inventory requires an HTTPS ARM origin.");
        }
        var path = string.Join('/', m_settings.ResourceGroup.Split('/').Select(Uri.EscapeDataString));
        m_uri = new Uri(endpoint, path + "/resources?api-version=" + Uri.EscapeDataString(m_settings.ApiVersion) +
            "&%24filter=" + Uri.EscapeDataString("resourceType eq '" + m_settings.ResourceType + "'"));
        if (transport is null) {
            m_httpClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(m_httpClient);
        }
        var options = new ArmClientOptions { Environment = environment, Transport = transport };
        options.Retry.MaxRetries = 2; // Reads may retry; the mutation adapter independently forbids automatic resend.
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        m_pipeline = new InventoryResource(new ArmClient(credential, defaultSubscriptionId: null, options), scope).RequestPipeline;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
        var result = new List<WorldExtensionObservationItem>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pages = new HashSet<string>(StringComparer.Ordinal);
        var uri = m_uri;
        var inspected = 0;
        for (var page = 0; page < m_settings.MaximumPages; page++) {
            if (!pages.Add(uri.AbsoluteUri)) { throw new InvalidOperationException("Inventory pagination repeated a page."); }
            using var message = m_pipeline.CreateMessage();
            message.BufferResponse = false;
            message.Request.Method = RequestMethod.Get;
            message.Request.Uri.Reset(uri);
            message.Request.Headers.SetValue("Accept", "application/json");
            await m_pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
            if (message.Response.Status != 200) { throw new InvalidOperationException("Inventory HTTP " + message.Response.Status); }
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            var stream = message.Response.ContentStream ?? throw new JsonException("Inventory response has no body.");
            int read;
            while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0) {
                if (buffer.Length + read > m_settings.MaximumResponseBytes) { throw new InvalidOperationException("Inventory response exceeds byte budget."); }
                buffer.Write(bytes, 0, read);
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            foreach (var resource in document.RootElement.GetProperty("value").EnumerateArray()) {
                if (++inspected > 4096) { throw new InvalidOperationException("Inventory exceeds inspection budget."); }
                var type = resource.GetProperty("type").GetString()!;
                var name = resource.GetProperty("name").GetString()!;
                var id = resource.GetProperty("id").GetString()!;
                if (!id.StartsWith(m_settings.ResourceGroup.TrimEnd('/') + "/providers/", StringComparison.OrdinalIgnoreCase)) {
                    throw new InvalidOperationException("Inventory returned a resource outside its approved group.");
                }
                if (!type.Equals(m_settings.ResourceType, StringComparison.OrdinalIgnoreCase) ||
                    !name.StartsWith(m_settings.NamePrefix, StringComparison.OrdinalIgnoreCase) ||
                    (m_settings.ExcludeNames?.Contains(name, StringComparer.OrdinalIgnoreCase) ?? false)) { continue; }
                if (!identities.Add(id) || result.Count >= m_maximumItems) { throw new InvalidOperationException("Inventory is duplicate or exceeds item budget."); }
                var fields = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var field in m_settings.Fields) {
                    var value = resource;
                    foreach (var segment in field.Value[1..].Split('/')) {
                        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) { value = default; break; }
                    }
                    fields.Add(field.Key, value.ValueKind switch {
                        JsonValueKind.String => value.GetString()!,
                        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                        JsonValueKind.Null or JsonValueKind.Undefined => "",
                        _ => throw new JsonException("Inventory disclosure must select scalar fields."),
                    });
                }
                result.Add(new(name.ToLowerInvariant(), fields));
            }
            if (!document.RootElement.TryGetProperty("nextLink", out var next) || next.ValueKind == JsonValueKind.Null || next.GetString() is not { Length: > 0 } link) {
                return result;
            }
            if (!Uri.TryCreate(link, UriKind.Absolute, out uri!) || uri.Scheme != m_uri.Scheme ||
                !uri.Authority.Equals(m_uri.Authority, StringComparison.OrdinalIgnoreCase) || uri.UserInfo.Length != 0 ||
                uri.Fragment.Length != 0 || !uri.AbsolutePath.Equals(m_uri.AbsolutePath, StringComparison.OrdinalIgnoreCase)) {
                throw new InvalidOperationException("Inventory continuation left its approved endpoint or collection.");
            }
        }
        throw new InvalidOperationException("Inventory pagination exceeds page budget.");
    }

    /// <summary>Disposes the owned transport; the supplied credential and any custom transport remain host-owned.</summary>
    public void Dispose() => m_httpClient?.Dispose();
    private sealed class InventoryResource(ArmClient client, ResourceIdentifier scope) : ArmResource(client, scope) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}

internal sealed record AzureResourceInventorySettings(string ResourceGroup, string ApiVersion, string ResourceType,
    IReadOnlyDictionary<string, string> Fields, string NamePrefix = "", IReadOnlyList<string>? ExcludeNames = null,
    int MaximumPages = 16, int MaximumResponseBytes = 1048576);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AzureResourceInventorySettings))]
internal sealed partial class AzureResourceInventoryJson : JsonSerializerContext;
