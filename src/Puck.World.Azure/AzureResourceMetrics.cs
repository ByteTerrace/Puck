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
    private static readonly IReadOnlyDictionary<string, string> AggregationFields = new Dictionary<string, string>(StringComparer.Ordinal) {
        ["Average"] = "average", ["Total"] = "total", ["Count"] = "count", ["Minimum"] = "minimum", ["Maximum"] = "maximum",
    };
    private static readonly HashSet<string> Intervals = ["PT1M", "PT5M", "PT15M", "PT30M", "PT1H", "PT6H", "PT12H", "P1D"];

    private readonly AzureResourceMetricsSettings m_settings;
    private readonly int m_maximumItems;
    private readonly HttpPipeline m_pipeline;
    private readonly HttpClient? m_httpClient;
    private readonly Uri m_uri;
    private readonly string m_aggregationField;

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
        m_settings = settings.Deserialize(AzureResourceMetricsJson.Default.AzureResourceMetricsSettings)
            ?? throw new JsonException("Metrics settings are required.");
        if (m_settings.Kind != "metrics") { throw new ArgumentException("Metrics settings require kind 'metrics'."); }
        var segments = m_settings.ResourceId.Split('/');
        if (segments.Length < 9 || segments[0].Length != 0 || !segments[1].Equals("subscriptions", StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(segments[2], "D", out _) || !segments[3].Equals("resourceGroups", StringComparison.OrdinalIgnoreCase) ||
            segments[4].Length is < 1 or > 90 || segments[4].EndsWith('.') ||
            segments[4].Any(c => !(char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '(' or ')')) ||
            !segments[5].Equals("providers", StringComparison.OrdinalIgnoreCase) || segments[6..].Any(segment => segment.Length == 0)) {
            throw new ArgumentException("Metrics requires an absolute subscription/resourceGroup/providers resource ID without URI syntax or traversal segments.");
        }
        if (string.IsNullOrWhiteSpace(m_settings.ApiVersion) || m_settings.MetricNames.Count is < 1 or > 16 ||
            m_settings.MetricNames.Any(name => name.Length is < 1 or > 256 || name.Any(char.IsControl)) ||
            m_settings.MetricNames.Distinct(StringComparer.Ordinal).Count() != m_settings.MetricNames.Count ||
            m_settings.MaximumResponseBytes is < 1024 or > 4194304) {
            throw new ArgumentException("Metrics needs an API version, 1-16 distinct metric names, and a bounded response size.");
        }
        if (!AggregationFields.TryGetValue(m_settings.Aggregation, out m_aggregationField!)) {
            throw new ArgumentException("Metrics aggregation must be Average, Total, Count, Minimum, or Maximum.");
        }
        if (!Intervals.Contains(m_settings.Interval)) {
            throw new ArgumentException("Metrics interval must be one of PT1M, PT5M, PT15M, PT30M, PT1H, PT6H, PT12H, P1D.");
        }
        var endpoint = environment.Endpoint;
        if (endpoint.Scheme != "https" || endpoint.AbsolutePath != "/" || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0) {
            throw new ArgumentException("Metrics requires an HTTPS ARM origin.");
        }
        var path = string.Join('/', m_settings.ResourceId.Split('/').Select(Uri.EscapeDataString));
        m_uri = new Uri(endpoint, path + "/providers/microsoft.insights/metrics?api-version=" + Uri.EscapeDataString(m_settings.ApiVersion) +
            "&metricnames=" + Uri.EscapeDataString(string.Join(',', m_settings.MetricNames)) +
            "&aggregation=" + Uri.EscapeDataString(m_settings.Aggregation) + "&interval=" + Uri.EscapeDataString(m_settings.Interval));
        if (transport is null) {
            m_httpClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });
            transport = new HttpClientTransport(m_httpClient);
        }
        var options = new ArmClientOptions { Environment = environment, Transport = transport };
        options.Retry.MaxRetries = 2;
        options.Diagnostics.IsLoggingEnabled = false;
        options.Diagnostics.IsDistributedTracingEnabled = false;
        m_pipeline = new MetricsResource(new ArmClient(credential, defaultSubscriptionId: null, options), new ResourceIdentifier(m_settings.ResourceId)).RequestPipeline;
    }

    /// <inheritdoc/>
    public async ValueTask<IReadOnlyList<WorldExtensionObservationItem>> ReadAsync(CancellationToken cancellationToken) {
        using var message = m_pipeline.CreateMessage();
        message.BufferResponse = false;
        message.Request.Method = RequestMethod.Get;
        message.Request.Uri.Reset(m_uri);
        message.Request.Headers.SetValue("Accept", "application/json");
        await m_pipeline.SendAsync(message, cancellationToken).ConfigureAwait(false);
        if (message.Response.Status != 200) { throw new InvalidOperationException("Metrics HTTP " + message.Response.Status); }
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        var stream = message.Response.ContentStream ?? throw new JsonException("Metrics response has no body.");
        int read;
        while ((read = await stream.ReadAsync(bytes, cancellationToken).ConfigureAwait(false)) != 0) {
            if (buffer.Length + read > m_settings.MaximumResponseBytes) { throw new InvalidOperationException("Metrics response exceeds byte budget."); }
            buffer.Write(bytes, 0, read);
        }
        using var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
        var values = new Dictionary<string, decimal>(StringComparer.Ordinal);
        foreach (var metric in document.RootElement.GetProperty("value").EnumerateArray()) {
            var name = metric.GetProperty("name").GetProperty("value").GetString() ?? throw new JsonException("Metrics response omitted a metric name.");
            if (!m_settings.MetricNames.Contains(name, StringComparer.Ordinal)) { continue; }
            if (!values.TryAdd(name, LatestCompleteBucket(metric, m_aggregationField))) { throw new InvalidOperationException($"Metrics response duplicated metric '{name}'."); }
        }
        var result = new List<WorldExtensionObservationItem>(m_settings.MetricNames.Count);
        foreach (var name in m_settings.MetricNames) {
            if (!values.TryGetValue(name, out var value)) { throw new InvalidOperationException($"Metrics response omitted a complete bucket for '{name}'."); }
            result.Add(new(name, new Dictionary<string, string> { ["value"] = value.ToString(CultureInfo.InvariantCulture) }));
        }
        return result.Count > m_maximumItems ? throw new InvalidOperationException("Metrics exceeds item budget.") : result;
    }

    /// <summary>Finds the newest bucket carrying a numeric value for the requested aggregation, scanning from the
    /// end so a still-filling trailing bucket is skipped rather than read as zero.</summary>
    private static decimal LatestCompleteBucket(JsonElement metric, string aggregationField) {
        if (!metric.TryGetProperty("timeseries", out var series) || series.ValueKind != JsonValueKind.Array || series.GetArrayLength() != 1) {
            throw new InvalidOperationException("Metrics response must carry exactly one timeseries per metric.");
        }
        var timeseries = series[0];
        if (!timeseries.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) {
            throw new InvalidOperationException("Metrics response timeseries has no data.");
        }
        for (var index = data.GetArrayLength() - 1; index >= 0; index--) {
            if (data[index].TryGetProperty(aggregationField, out var value) && value.ValueKind == JsonValueKind.Number) { return value.GetDecimal(); }
        }
        throw new InvalidOperationException("Metrics response has no complete bucket yet.");
    }

    /// <summary>Disposes the owned transport; the supplied credential and any custom transport remain host-owned.</summary>
    public void Dispose() => m_httpClient?.Dispose();
    private sealed class MetricsResource(ArmClient client, ResourceIdentifier scope) : ArmResource(client, scope) {
        public HttpPipeline RequestPipeline => Pipeline;
    }
}

internal sealed record AzureResourceMetricsSettings(string Kind, string ResourceId, string ApiVersion,
    IReadOnlyList<string> MetricNames, string Aggregation, string Interval, int MaximumResponseBytes = 1048576);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectRequiredConstructorParameters = true,
    RespectNullableAnnotations = true)]
[JsonSerializable(typeof(AzureResourceMetricsSettings))]
internal sealed partial class AzureResourceMetricsJson : JsonSerializerContext;
