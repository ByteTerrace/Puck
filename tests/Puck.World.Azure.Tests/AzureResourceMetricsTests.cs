using System.Net;
using System.Text;
using System.Text.Json;
using Azure.Core;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureResourceMetricsTests {
    private const string ResourceId = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck/providers/Microsoft.Storage/storageAccounts/bytrcstp001";
    private static CancellationToken Cancel => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck")]
    [InlineData("/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/../providers/Microsoft.Storage/storageAccounts/a")]
    [InlineData("subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck/providers/Microsoft.Storage/storageAccounts/a")]
    public void MalformedResourceIdsRefuseBeforeServiceAccess(string resourceId) {
        using var wire = new Wire();
        Assert.Throws<ArgumentException>(() => Source(wire, resourceId: resourceId));
        Assert.Empty(wire.Uris);
    }

    [Theory]
    [InlineData("median")]
    [InlineData("")]
    public void UnrecognizedAggregationRefuses(string aggregation) {
        using var wire = new Wire();
        Assert.Throws<ArgumentException>(() => Source(wire, aggregation: aggregation));
    }

    [Fact]
    public void UnrecognizedIntervalRefuses() {
        using var wire = new Wire();
        Assert.Throws<ArgumentException>(() => Source(wire, interval: "PT2M"));
    }

    [Fact]
    public async Task LatestCompleteBucketSkipsAStillFillingTrailingBucket() {
        using var wire = new Wire(Metrics(("Percentage CPU", [(10.5, true), (null, false)])));
        using var source = Source(wire);
        var items = await source.ReadAsync(Cancel);
        var item = Assert.Single(items);
        Assert.Equal("Percentage CPU", item.Key);
        Assert.Equal("10.5", item.Fields["value"]);
        Assert.Contains("aggregation=Average", wire.Uris[0].Query);
        Assert.Contains("metricnames=Percentage%20CPU", wire.Uris[0].Query);
    }

    [Fact]
    public async Task AResponseOmittingARequestedMetricRefusesTheWholeRead() {
        using var wire = new Wire(Metrics(("Other Metric", [(1.0, true)])));
        using var source = Source(wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Cancel).AsTask());
    }

    [Fact]
    public async Task AMetricWithNoCompleteBucketYetRefusesTheWholeRead() {
        using var wire = new Wire(Metrics(("Percentage CPU", [(null, false)])));
        using var source = Source(wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Cancel).AsTask());
    }

    [Fact]
    public void TheAzureProviderDispatchesOnAnAuthoredKindDefaultingToInventory() {
        using var provider = AzureConfiguredProvider.Registration.Create(JsonDocument.Parse(
            """{"authentication":"azureCli"}""").RootElement);
        var factory = (IWorldConfiguredObservationProvider)provider;
        using var inventory = factory.BindObservation(JsonDocument.Parse("""
            {"resourceGroup":"/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/puck",
             "resourceType":"Microsoft.Storage/storageAccounts","apiVersion":"2021-04-01","fields":{"name":"/name"}}
            """).RootElement, 8);
        Assert.IsType<AzureResourceInventory>(inventory);
        using var metrics = factory.BindObservation(JsonDocument.Parse($$"""
            {"kind":"metrics","resourceId":"{{ResourceId}}","apiVersion":"2018-01-01","metricNames":["Percentage CPU"],
             "aggregation":"Average","interval":"PT1M"}
            """).RootElement, 8);
        Assert.IsType<AzureResourceMetrics>(metrics);
    }

    private static AzureResourceMetrics Source(Wire wire, string resourceId = ResourceId, string aggregation = "Average", string interval = "PT1M") {
        using var settings = JsonDocument.Parse($$"""
            {"kind":"metrics","resourceId":"{{resourceId}}","apiVersion":"2018-01-01","metricNames":["Percentage CPU"],
             "aggregation":"{{aggregation}}","interval":"{{interval}}"}
            """);
        return new(new Credential(), ArmEnvironment.AzurePublicCloud, settings.RootElement, 8, new HttpClientTransport(wire.Client));
    }
    private static string Metrics(params (string Name, (double? Value, bool Present)[] Buckets)[] metrics) {
        var values = metrics.Select(metric => {
            var buckets = metric.Buckets.Select(bucket => bucket.Present ? "{\"timeStamp\":\"2024-01-01T00:00:00Z\",\"average\":" +
                bucket.Value!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}" : "{\"timeStamp\":\"2024-01-01T00:01:00Z\"}");
            return "{\"name\":{\"value\":\"" + metric.Name + "\"},\"timeseries\":[{\"data\":[" + string.Join(',', buckets) + "]}]}";
        });
        return "{\"value\":[" + string.Join(',', values) + "]}";
    }
    private sealed class Wire(params string[] replies) : HttpMessageHandler {
        private readonly Queue<string> m_replies = new(replies);
        public List<Uri> Uris { get; } = [];
        public HttpClient Client => m_client ??= new(this, disposeHandler: false);
        private HttpClient? m_client;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Uris.Add(request.RequestUri!);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(m_replies.Dequeue(), Encoding.UTF8, "application/json") });
        }
        protected override void Dispose(bool disposing) { if (disposing) { m_client?.Dispose(); } base.Dispose(disposing); }
    }
    private sealed class Credential : TokenCredential {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("test-token", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
