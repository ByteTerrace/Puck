using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>A bounded latest-value read over one resource's Azure Monitor metrics. Only the requested aggregation of
/// each authored metric leaves this adapter, as an invariant decimal string keyed by metric name.</summary>
/// <remarks>The read is one authority call over a fixed metric list; it never enumerates dimensions or resources.
/// A failed, oversized, or incomplete read never masquerades as a value.</remarks>
public sealed class AzureResourceMetrics : IWorldExtensionObservationSource {
    private static readonly IReadOnlyDictionary<string, string> AggregationFields = new Dictionary<string, string>(comparer: StringComparer.Ordinal) {
        ["Average"] = "average",
        ["Total"] = "total",
        ["Count"] = "count",
        ["Minimum"] = "minimum",
        ["Maximum"] = "maximum",
    };
    private static readonly HashSet<string> Intervals = ["PT1M", "PT5M", "PT15M", "PT30M", "PT1H", "PT6H", "PT12H", "P1D"];

    private readonly string m_aggregationField;
    private readonly HttpClient? m_httpClient;
    private readonly int m_maximumItems;
    private readonly HttpPipeline m_pipeline;
    private readonly AzureResourceMetricsSettings m_settings;
    private readonly Uri m_uri;

    /// <inheritdoc/>
    public string Kind => "metrics";

    /// <summary>Binds a host-authorized single-resource metrics query without making service calls.</summary>
    /// <param name="credential">Host-owned credential.</param>
    /// <param name="environment">Explicit ARM cloud.</param>
    /// <param name="settings">Strict resource, metric list, aggregation, and time-grain selection.</param>
    /// <param name="maximumItems">Positive maximum matching collection size.</param>
    /// <param name="transport">Optional trusted test transport. Must not follow redirects.</param>
    public AzureResourceMetrics(TokenCredential credential, ArmEnvironment environment, JsonElement settings,
        int maximumItems, HttpPipelineTransport? transport = null) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        m_maximumItems = maximumItems;
        m_settings = (settings.Deserialize(AzureResourceMetricsJson.Default.AzureResourceMetricsSettings)
            ?? throw new JsonException(message: "Metrics settings are required."));
        if (m_settings.Kind != "metrics") { throw new ArgumentException(message: "Metrics settings require kind 'metrics'."); }
        var segments = m_settings.ResourceId.Split('/');

        if (
            (segments.Length < 9) ||
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
            segments[4].Any(predicate: c => !(char.IsLetterOrDigit(c: c) || (c is '_' or '-' or '.' or '(' or ')'))) ||
            !segments[5].Equals(
            comparisonType: StringComparison.OrdinalIgnoreCase,
            value: "providers"
        ) ||
            segments[6..].Any(predicate: segment => (segment.Length == 0))
        ) {
            throw new ArgumentException(message: "Metrics requires an absolute subscription/resourceGroup/providers resource ID without URI syntax or traversal segments.");
        }
        if (
            string.IsNullOrWhiteSpace(value: m_settings.ApiVersion) ||
            (m_settings.MetricNames.Count is < 1 or > 16) ||
            m_settings.MetricNames.Any(predicate: name => ((name.Length is < 1 or > 256) || name.Any(predicate: char.IsControl))) ||
            (m_settings.MetricNames.Distinct(comparer: StringComparer.Ordinal).Count() != m_settings.MetricNames.Count) ||
            (m_settings.MaximumResponseBytes is < 1024 or > 4194304)
        ) {
            throw new ArgumentException(message: "Metrics needs an API version, 1-16 distinct metric names, and a bounded response size.");
        }
        if (!AggregationFields.TryGetValue(
            key: m_settings.Aggregation,
            value: out m_aggregationField!
        )) {
            throw new ArgumentException(message: "Metrics aggregation must be Average, Total, Count, Minimum, or Maximum.");
        }
        if (!Intervals.Contains(item: m_settings.Interval)) {
            throw new ArgumentException(message: "Metrics interval must be one of PT1M, PT5M, PT15M, PT30M, PT1H, PT6H, PT12H, P1D.");
        }
        var endpoint = environment.Endpoint;

        if (
            (endpoint.Scheme != "https") ||
            (endpoint.AbsolutePath != "/") ||
            (endpoint.UserInfo.Length != 0) ||
            (endpoint.Query.Length != 0) ||
            (endpoint.Fragment.Length != 0)
        ) {
            throw new ArgumentException(message: "Metrics requires an HTTPS ARM origin.");
        }
        var path = string.Join(
            separator: '/',
            values: m_settings.ResourceId.Split('/').Select(selector: Uri.EscapeDataString)
        );

        m_uri = new Uri(
            baseUri: endpoint,
            relativeUri: ((((((((path + "/providers/microsoft.insights/metrics?api-version=") + Uri.EscapeDataString(stringToEscape: m_settings.ApiVersion)) +
            "&metricnames=") + Uri.EscapeDataString(stringToEscape: string.Join(
                separator: ',',
                values: m_settings.MetricNames
            ))) +
            "&aggregation=") + Uri.EscapeDataString(stringToEscape: m_settings.Aggregation)) + "&interval=") + Uri.EscapeDataString(stringToEscape: m_settings.Interval))
        );
        if (transport is null) {
            m_httpClient = new(handler: new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(client: m_httpClient);
        }
        var options = new ArmClientOptions { Environment = environment, Transport = transport };

        options.Retry.MaxRetries = 2;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        m_pipeline = new MetricsResource(
            client: new ArmClient(
                credential,
                defaultSubscriptionId: null,
                options
            ),
            scope: new ResourceIdentifier(resourceId: m_settings.ResourceId)
        ).RequestPipeline;
    }

    /// <summary>Finds the newest bucket carrying a numeric value for the requested aggregation, scanning from the
    /// end so a still-filling trailing bucket is skipped rather than read as zero.</summary>
    private static decimal LatestCompleteBucket(JsonElement metric, string aggregationField) {
        if (
            !metric.TryGetProperty(
            propertyName: "timeseries",
            value: out var series
        ) ||
            (series.ValueKind != JsonValueKind.Array) ||
            (series.GetArrayLength() != 1)
        ) {
            throw new InvalidOperationException(message: "Metrics response must carry exactly one timeseries per metric.");
        }
        var timeseries = series[0];

        if (
            !timeseries.TryGetProperty(
            propertyName: "data",
            value: out var data
        ) ||
            (data.ValueKind != JsonValueKind.Array)
        ) {
            throw new InvalidOperationException(message: "Metrics response timeseries has no data.");
        }
        for (var index = (data.GetArrayLength() - 1); (index >= 0); index--) {
            if (
                data[index].TryGetProperty(
                propertyName: aggregationField,
                value: out var value
            ) &&
                (value.ValueKind == JsonValueKind.Number)
            ) { return value.GetDecimal(); }
        }
        throw new InvalidOperationException(message: "Metrics response has no complete bucket yet.");
    }

    /// <summary>Disposes the owned transport; the supplied credential and any custom transport remain host-owned.</summary>
    public void Dispose() => m_httpClient?.Dispose();
    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
        using var message = m_pipeline.CreateMessage();

        message.BufferResponse = false;
        message.Request.Method = RequestMethod.Get;
        message.Request.Uri.Reset(value: m_uri);
        message.Request.Headers.SetValue(
            name: "Accept",
            value: "application/json"
        );
        await m_pipeline.SendAsync(
            cancellationToken: cancellationToken,
            message: message
        ).ConfigureAwait(continueOnCapturedContext: false);
        if (message.Response.Status != 200) { throw new InvalidOperationException(message: ("Metrics HTTP " + message.Response.Status)); }
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        var stream = (message.Response.ContentStream ?? throw new JsonException(message: "Metrics response has no body."));
        int read;

        while ((read = await stream.ReadAsync(
            buffer: bytes,
            cancellationToken: cancellationToken
        ).ConfigureAwait(continueOnCapturedContext: false)) != 0) {
            if ((buffer.Length + read) > m_settings.MaximumResponseBytes) { throw new InvalidOperationException(message: "Metrics response exceeds byte budget."); }
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
        var values = new Dictionary<string, decimal>(comparer: StringComparer.Ordinal);

        foreach (var metric in document.RootElement.GetProperty(propertyName: "value").EnumerateArray()) {
            var name = (metric.GetProperty(propertyName: "name").GetProperty(propertyName: "value").GetString() ?? throw new JsonException(message: "Metrics response omitted a metric name."));

            if (!m_settings.MetricNames.Contains(
                name,
                StringComparer.Ordinal
            )) { continue; }
            if (!values.TryAdd(
                key: name,
                value: LatestCompleteBucket(
                    aggregationField: m_aggregationField,
                    metric: metric
                )
            )) { throw new InvalidOperationException(message: $"Metrics response duplicated metric '{name}'."); }
        }
        var result = new List<WorldExtensionObservationItem>(capacity: m_settings.MetricNames.Count);

        foreach (var name in m_settings.MetricNames) {
            if (!values.TryGetValue(
                key: name,
                value: out var value
            )) { throw new InvalidOperationException(message: $"Metrics response omitted a complete bucket for '{name}'."); }
            result.Add(item: new(
                name,
                new Dictionary<string, string> { ["value"] = value.ToString(provider: CultureInfo.InvariantCulture) }
            ));
        }
        return ((result.Count > m_maximumItems)
            ? throw new InvalidOperationException(message: "Metrics exceeds item budget.")
            : result
        );
    }

    private sealed class MetricsResource(ArmClient client, ResourceIdentifier scope) : ArmResource(
        client,
        scope
    ) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}

internal sealed record AzureResourceMetricsSettings(string Kind, string ResourceId, string ApiVersion,
    IReadOnlyList<string> MetricNames, string Aggregation, string Interval, int MaximumResponseBytes = 1048576);
[JsonSerializable(typeof(AzureResourceMetricsSettings))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
internal sealed partial class AzureResourceMetricsJson : JsonSerializerContext;
