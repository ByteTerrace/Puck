using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Text;
using Azure.Core.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Puck.Azure.Tests;

/// <summary>A collection of its own, never run beside another test: the laws set process-wide switches.</summary>
[CollectionDefinition(name: Name, DisableParallelization = true)]
public sealed class AzureMonitorProcessSwitchesCollection {
    public const string Name = "Azure Monitor process switches";
}

/// <summary>
/// CONTRACT UNDER TEST: a host exporting through
/// <see cref="AzureMonitorTelemetryExtensions.UseAzureMonitorExporterWithBoundedShutdown{TBuilder}"/>, under the
/// exporter's <c>DisablePersistOnShutdown</c> switch the Functions worker's runtime configuration sets, has delivered
/// its final telemetry when its stop returns, even through an ingestion endpoint that stalls longer than the five
/// seconds a provider's own disposal waits; and an endpoint that never answers holds the whole shutdown less than
/// <see cref="AzureMonitorTelemetryExtensions.ShutdownBudget"/>, because the transmission gives up at
/// <see cref="AzureMonitorTelemetryExtensions.NetworkTimeout"/>.
/// </summary>
[Collection(name: AzureMonitorProcessSwitchesCollection.Name)]
public sealed class AzureMonitorShutdownLawTests {
    private const string FinalItem = "puck-final-item";
    private const string SourceName = "Puck.Azure.Tests.Shutdown";

    // An ingestion endpoint that holds each request for its stall, then accepts it, recording whether a delivered
    // request carried the final item. A request the client abandons is abandoned here too.
    private sealed class StallingIngestion(TimeSpan stall) : HttpMessageHandler {
        private int m_finalItemDelivered;

        public bool FinalItemDelivered => (Volatile.Read(location: ref m_finalItemDelivered) != 0);

        private static string Body(HttpRequestMessage request) {
            if (request.Content is null) {
                return string.Empty;
            }

            using var raw = request.Content.ReadAsStream();
            using var decoded = (request.Content.Headers.ContentEncoding.Contains(value: "gzip")
                ? new GZipStream(stream: raw, mode: CompressionMode.Decompress)
                : raw);
            using var reader = new StreamReader(stream: decoded, encoding: Encoding.UTF8);

            return reader.ReadToEnd();
        }
        private HttpResponseMessage Accept(HttpRequestMessage request, CancellationToken cancellationToken) {
            var body = Body(request: request);
            var items = body.Split(separator: '\n', options: StringSplitOptions.RemoveEmptyEntries).Length;

            _ = cancellationToken.WaitHandle.WaitOne(timeout: stall);
            cancellationToken.ThrowIfCancellationRequested();

            if (body.Contains(value: FinalItem, comparisonType: StringComparison.Ordinal)) {
                Volatile.Write(location: ref m_finalItemDelivered, value: 1);
            }

            return new HttpResponseMessage(statusCode: HttpStatusCode.OK) {
                Content = new StringContent(content: $"{{\"itemsReceived\":{items},\"itemsAccepted\":{items},\"errors\":[]}}", encoding: Encoding.UTF8, mediaType: "application/json"),
            };
        }

        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken) => Accept(cancellationToken: cancellationToken, request: request);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(result: Accept(cancellationToken: cancellationToken, request: request));
    }

    // Runs a host exporting to the ingestion endpoint through one span, the final item, then shuts it down, returning
    // whether the item had been delivered when the stop returned, and how long the stop and disposal took together.
    private static async Task<(bool DeliveredWhenStopped, TimeSpan Shutdown)> RunAndShutDown(StallingIngestion ingestion) {
        AppContext.SetSwitch(switchName: "Azure.Monitor.OpenTelemetry.Exporter.DisablePersistOnShutdown", isEnabled: true);
        // The exporter's telemetry about itself has its own transport, which would reach for real endpoints.
        Environment.SetEnvironmentVariable(variable: "APPLICATIONINSIGHTS_STATSBEAT_DISABLED", value: "true");
        Environment.SetEnvironmentVariable(variable: "APPLICATIONINSIGHTS_SDKSTATS_DISABLED", value: "true");

        var builder = Host.CreateApplicationBuilder();

        builder.Services
            .AddOpenTelemetry()
            .UseAzureMonitorExporterWithBoundedShutdown(configure: options => {
                options.ConnectionString = "InstrumentationKey=00000000-0000-0000-0000-000000000000;IngestionEndpoint=https://ingestion.invalid/";
                options.DisableOfflineStorage = true;
                options.EnableLiveMetrics = false;
                options.TracesPerSecond = null;
                options.Transport = new HttpClientTransport(client: new HttpClient(handler: ingestion));
            })
            .WithTracing(configure: tracing => tracing.AddSource(names: SourceName));

        var host = builder.Build();

        await host.StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using (var source = new ActivitySource(name: SourceName)) {
            using var activity = source.StartActivity(name: FinalItem);

            Assert.NotNull(@object: activity);
        }

        var clock = Stopwatch.StartNew();

        await host.StopAsync(cancellationToken: TestContext.Current.CancellationToken);
        var delivered = ingestion.FinalItemDelivered;

        host.Dispose();

        return (delivered, clock.Elapsed);
    }

    [Fact]
    public async Task TheFinalItemIsDeliveredWhenTheHostsStopReturns() {
        var (delivered, _) = await RunAndShutDown(ingestion: new StallingIngestion(stall: TimeSpan.FromSeconds(seconds: 7)));

        Assert.True(
            condition: delivered,
            userMessage: "the host's stop returned before an ingestion endpoint stalling seven seconds received the final item"
        );
    }
    [Fact]
    public async Task AnEndpointThatNeverAnswersHoldsTheShutdownLessThanTheBudget() {
        var (delivered, shutdown) = await RunAndShutDown(ingestion: new StallingIngestion(stall: TimeSpan.FromMinutes(minutes: 2)));

        Assert.False(condition: delivered);
        Assert.True(
            condition: (shutdown < AzureMonitorTelemetryExtensions.ShutdownBudget),
            userMessage: $"an ingestion endpoint that never answers held the shutdown for {shutdown}, past the {AzureMonitorTelemetryExtensions.ShutdownBudget} budget"
        );
    }
}
