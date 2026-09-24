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
    private readonly HttpClient? m_httpClient;
    private readonly int m_maximumItems;
    private readonly HttpPipeline m_pipeline;
    private readonly AzureResourceInventorySettings m_settings;
    private readonly Uri m_uri;

    /// <inheritdoc/>
    public string Kind => "inventory";

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
        m_settings = (settings.Deserialize(AzureResourceInventoryJson.Default.AzureResourceInventorySettings)
            ?? throw new JsonException(message: "Inventory settings are required."));
        var segments = m_settings.ResourceGroup.Split('/');

        if (
            (segments.Length != 5) ||
            (segments[0].Length != 0) ||
            !segments[1].Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "subscriptions"
        ) ||
            !Guid.TryParseExact(
            segments[2],
            "D",
            out _
        ) ||
            !segments[3].Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "resourceGroups"
        ) ||
            (segments[4].Length is < 1 or > 90) ||
            segments[4].EndsWith(value: '.') ||
            segments[4].Any(predicate: c => !(char.IsLetterOrDigit(c: c) || (c is '_' or '-' or '.' or '(' or ')')))
        ) {
            throw new ArgumentException(message: "Inventory requires an absolute subscription/resource-group ID without URI syntax or traversal segments.");
        }
        var scope = new ResourceIdentifier(resourceId: m_settings.ResourceGroup);

        if (
            (scope.ResourceType.ToString() != "Microsoft.Resources/resourceGroups") ||
            string.IsNullOrWhiteSpace(value: m_settings.ApiVersion) ||
            (m_settings.Fields.Count is < 1 or > 16) ||
            (m_settings.MaximumPages is < 1 or > 64) ||
            (m_settings.MaximumResponseBytes is < 1024 or > 4194304) ||
            m_settings.ResourceType.Split('/').Any(predicate: segment => ((segment.Length == 0) || segment.Any(predicate: c => !(char.IsAsciiLetterOrDigit(c: c) || (c is '.' or '-')))))
        ) {
            throw new ArgumentException(message: "Inventory needs a resource-group ID, resource type, API version, field allowlist, and bounded pages/bytes.");
        }
        foreach (var field in m_settings.Fields) {
            if (
                (field.Key.Length is < 1 or > 128) ||
                (field.Value.Length is < 2 or > 512) ||
                (field.Value[0] != '/') ||
                field.Value.Contains(value: '~') ||
                field.Value[1..].Split('/').Any(predicate: segment => (segment.Length == 0))
            ) {
                throw new ArgumentException(message: "Inventory fields use slash-separated property paths without JSON-pointer escape sequences.");
            }
        }
        var endpoint = environment.Endpoint;

        if (
            (endpoint.Scheme != "https") ||
            (endpoint.AbsolutePath != "/") ||
            (endpoint.UserInfo.Length != 0) ||
            (endpoint.Query.Length != 0) ||
            (endpoint.Fragment.Length != 0)
        ) {
            throw new ArgumentException(message: "Inventory requires an HTTPS ARM origin.");
        }
        var path = string.Join(
            separator: '/',
            values: m_settings.ResourceGroup.Split('/').Select(selector: Uri.EscapeDataString)
        );

        m_uri = new Uri(
            baseUri: endpoint,
            relativeUri: ((((path + "/resources?api-version=") + Uri.EscapeDataString(stringToEscape: m_settings.ApiVersion)) +
            "&%24filter=") + Uri.EscapeDataString(stringToEscape: (("resourceType eq '" + m_settings.ResourceType) + "'")))
        );
        if (transport is null) {
            m_httpClient = new(handler: new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(client: m_httpClient);
        }
        var options = new ArmClientOptions { Environment = environment, Transport = transport };

        options.Retry.MaxRetries = 2; // Reads may retry; the mutation adapter independently forbids automatic resend.
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        m_pipeline = new InventoryResource(
            client: new ArmClient(
                credential,
                defaultSubscriptionId: null,
                options
            ),
            scope: scope
        ).RequestPipeline;
    }

    /// <summary>Disposes the owned transport; the supplied credential and any custom transport remain host-owned.</summary>
    public void Dispose() => m_httpClient?.Dispose();
    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
        var result = new List<WorldExtensionObservationItem>();
        var identities = new HashSet<string>(comparer: StringComparer.OrdinalIgnoreCase);
        var pages = new HashSet<string>(comparer: StringComparer.Ordinal);
        var uri = m_uri;
        var inspected = 0;

        for (var page = 0; (page < m_settings.MaximumPages); page++) {
            if (!pages.Add(item: uri.AbsoluteUri)) { throw new InvalidOperationException(message: "Inventory pagination repeated a page."); }
            using var message = m_pipeline.CreateMessage();

            message.BufferResponse = false;
            message.Request.Method = RequestMethod.Get;
            message.Request.Uri.Reset(value: uri);
            message.Request.Headers.SetValue(
                name: "Accept",
                value: "application/json"
            );
            await m_pipeline.SendAsync(
                cancellationToken: cancellationToken,
                message: message
            ).ConfigureAwait(continueOnCapturedContext: false);
            if (message.Response.Status != 200) { throw new InvalidOperationException(message: ("Inventory HTTP " + message.Response.Status)); }
            using var buffer = new MemoryStream();
            var bytes = new byte[8192];
            var stream = (message.Response.ContentStream ?? throw new JsonException(message: "Inventory response has no body."));
            int read;

            while ((read = await stream.ReadAsync(
                buffer: bytes,
                cancellationToken: cancellationToken
            ).ConfigureAwait(continueOnCapturedContext: false)) != 0) {
                if ((buffer.Length + read) > m_settings.MaximumResponseBytes) { throw new InvalidOperationException(message: "Inventory response exceeds byte budget."); }
                buffer.Write(
                    buffer: bytes,
                    count: read,
                    offset: 0
                );
            }
            using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(
                0,
                ((int)buffer.Length)
            ));

            foreach (var resource in document.RootElement.GetProperty(propertyName: "value").EnumerateArray()) {
                if (++inspected > 4096) { throw new InvalidOperationException(message: "Inventory exceeds inspection budget."); }
                var type = resource.GetProperty(propertyName: "type").GetString()!;
                var name = resource.GetProperty(propertyName: "name").GetString()!;
                var id = resource.GetProperty(propertyName: "id").GetString()!;

                if (!id.StartsWith(
                    (m_settings.ResourceGroup.TrimEnd(trimChar: '/') + "/providers/"),
                    StringComparison.OrdinalIgnoreCase
                )) {
                    throw new InvalidOperationException(message: "Inventory returned a resource outside its approved group.");
                }
                if (
                    !type.Equals(
                    m_settings.ResourceType,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                    !name.StartsWith(
                    m_settings.NamePrefix,
                    StringComparison.OrdinalIgnoreCase
                ) ||
                    (m_settings.ExcludeNames?.Contains(
                    name,
                    StringComparer.OrdinalIgnoreCase
                ) ?? false)
                ) { continue; }
                if (
                    !identities.Add(item: id) ||
                    (result.Count >= m_maximumItems)
                ) { throw new InvalidOperationException(message: "Inventory is duplicate or exceeds item budget."); }
                var fields = new Dictionary<string, string>(comparer: StringComparer.Ordinal);

                foreach (var field in m_settings.Fields) {
                    var value = resource;

                    foreach (var segment in field.Value[1..].Split('/')) {
                        if (
                            (value.ValueKind != JsonValueKind.Object) ||
                            !value.TryGetProperty(
                            propertyName: segment,
                            value: out value
                        )
                        ) { value = default; break; }
                    }
                    fields.Add(
                        key: field.Key,
                        value: value.ValueKind switch {
                            JsonValueKind.String => value.GetString()!,
                            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
                            JsonValueKind.Null or JsonValueKind.Undefined => "",
                            _ => throw new JsonException(message: "Inventory disclosure must select scalar fields."),
                        }
                    );
                }
                result.Add(item: new(
                    name.ToLowerInvariant(),
                    fields
                ));
            }
            if (
                !document.RootElement.TryGetProperty(
                propertyName: "nextLink",
                value: out var next
            ) ||
                (next.ValueKind == JsonValueKind.Null) ||
                (next.GetString() is not { Length: > 0 } link)
            ) {
                return result;
            }
            if (
                !Uri.TryCreate(
                result: out uri!,
                uriKind: UriKind.Absolute,
                uriString: link
            ) ||
                (uri.Scheme != m_uri.Scheme) ||
                !uri.Authority.Equals(
                m_uri.Authority,
                StringComparison.OrdinalIgnoreCase
            ) ||
                (uri.UserInfo.Length != 0) ||
                (uri.Fragment.Length != 0) ||
                !uri.AbsolutePath.Equals(
                m_uri.AbsolutePath,
                StringComparison.OrdinalIgnoreCase
            )
            ) {
                throw new InvalidOperationException(message: "Inventory continuation left its approved endpoint or collection.");
            }
        }
        throw new InvalidOperationException(message: "Inventory pagination exceeds page budget.");
    }

    private sealed class InventoryResource(ArmClient client, ResourceIdentifier scope) : ArmResource(
        client,
        scope
    ) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}

internal sealed record AzureResourceInventorySettings(string ResourceGroup, string ApiVersion, string ResourceType,
    IReadOnlyDictionary<string, string> Fields, string NamePrefix = "", IReadOnlyList<string>? ExcludeNames = null,
    int MaximumPages = 16, int MaximumResponseBytes = 1048576);
[JsonSerializable(typeof(AzureResourceInventorySettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
internal sealed partial class AzureResourceInventoryJson : JsonSerializerContext;
