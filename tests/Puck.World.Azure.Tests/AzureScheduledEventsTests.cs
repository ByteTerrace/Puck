using System.Net;
using System.Text;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureScheduledEventsTests {
    [Fact]
    public async Task RetirementStorageFailureEscapesMetadataRetry() {
        using var client = new HttpClient(new MetadataWire());
        var failure = new HttpRequestException("checkpoint upload failed");
        var caught = await Assert.ThrowsAsync<HttpRequestException>(() => new AzureScheduledEvents(client).RunAsync(
            (_, _) => Task.FromException(failure), TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        Assert.Same(failure, caught);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"pollSeconds\":0}")]
    [InlineData("{\"pollSeconds\":1,\"unexpected\":true}")]
    public void ExtensionOwnsSettingsValidation(string json) {
        using var settings = System.Text.Json.JsonDocument.Parse(json);
        Assert.Throws<ArgumentException>(() => AzureSiloExtensions.Retirement.Create(settings.RootElement));
    }

    [Fact]
    public async Task RetiresOnlyForThisVmAndNeverAcknowledgesOtherProcesses() {
        using var wire = new MetadataWire();
        using var client = new HttpClient(wire);
        var calls = 0;
        await new AzureScheduledEvents(client).RunAsync((deadline, _) => {
            calls++;
            Assert.Equal(DateTimeOffset.Parse("2030-01-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture), deadline);
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Equal(2, wire.Reads);
    }
    private sealed class MetadataWire : HttpMessageHandler {
        public int Reads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("true", Assert.Single(request.Headers.GetValues("Metadata")));
            Reads++;
            var body = Reads == 1 ? """{"name":"worker-a"}""" : """
                {"Events":[
                  {"EventType":"Preempt","Resources":["worker-b"],"NotBefore":"2029-01-01T00:00:00Z"},
                  {"EventType":"Freeze","Resources":["worker-a"],"NotBefore":"2029-01-01T00:00:00Z"},
                  {"EventType":"Preempt","Resources":["worker-a"],"NotBefore":"2030-01-01T00:00:00Z"}
                ]}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
