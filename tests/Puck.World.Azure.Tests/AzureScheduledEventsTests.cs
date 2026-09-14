using System.Net;
using System.Text;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureScheduledEventsTests {
    [InlineData("{}")]
    [InlineData("{\"pollSeconds\":0}")]
    [InlineData("{\"pollSeconds\":1,\"unexpected\":true}")]
    [Theory]
    public void ExtensionOwnsSettingsValidation(string json) {
        using var settings = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(testCode: () => AzureSiloExtensions.Retirement.Create(settings.RootElement));
    }
    [Fact]
    public async Task RetirementStorageFailureEscapesMetadataRetry() {
        using var client = new HttpClient(handler: new MetadataWire());
        var failure = new HttpRequestException(message: "checkpoint upload failed");
        var caught = await Assert.ThrowsAsync<HttpRequestException>(testCode: () => new AzureScheduledEvents(client: client).RunAsync(
            (_, _) => Task.FromException(exception: failure),
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken
        ));

        Assert.Same(
            actual: caught,
            expected: failure
        );
    }
    [Fact]
    public async Task RetiresOnlyForThisVmAndNeverAcknowledgesOtherProcesses() {
        using var wire = new MetadataWire();
        using var client = new HttpClient(handler: wire);
        var calls = 0;

        await new AzureScheduledEvents(client: client).RunAsync(
            (deadline, _) => {
            calls++;
            Assert.Equal(
                DateTimeOffset.Parse(
                    "2030-01-01T00:00:00Z",
                    System.Globalization.CultureInfo.InvariantCulture
                ),
                deadline
            );
            return Task.CompletedTask;
        },
            TimeSpan.FromSeconds(seconds: 1),
            TestContext.Current.CancellationToken
        );
        Assert.Equal(
            actual: calls,
            expected: 1
        );
        Assert.Equal(
            actual: wire.Reads,
            expected: 2
        );
    }

    private sealed class MetadataWire : HttpMessageHandler {
        public int Reads;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            Assert.Equal(
                HttpMethod.Get,
                request.Method
            );
            Assert.Equal(
                "true",
                Assert.Single(collection: request.Headers.GetValues(name: "Metadata"))
            );
            Reads++;
            var body = ((Reads == 1)
                ? """{"name":"worker-a"}"""
                : """
                {"Events":[
                  {"EventType":"Preempt","Resources":["worker-b"],"NotBefore":"2029-01-01T00:00:00Z"},
                  {"EventType":"Freeze","Resources":["worker-a"],"NotBefore":"2029-01-01T00:00:00Z"},
                  {"EventType":"Preempt","Resources":["worker-a"],"NotBefore":"2030-01-01T00:00:00Z"}
                ]}
                """
            );

            return Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) { Content = new StringContent(
                content: body,
                encoding: Encoding.UTF8,
                mediaType: "application/json"
            ) });
        }
    }
}
