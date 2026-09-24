using System.Net;
using System.Text;
using Puck.Testing;
using Xunit;

namespace Puck.World.Azure.Tests;

public sealed class AzureScheduledEventsTests {
    [InlineData("{}")]
    [InlineData("{\"pollSeconds\":0}")]
    [InlineData("{\"pollSeconds\":1,\"unexpected\":true}")]
    [Theory]
    public void ExtensionOwnsSettingsValidation(string json) {
        using var settings = System.Text.Json.JsonDocument.Parse(json);

        Assert.Throws<ArgumentException>(testCode: () => AzureSiloExtensions.Retirement.Create(
            settings.RootElement,
            TimeProvider.System
        ));
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
    /// <summary>A metadata read that never answers ends when <see cref="AzureScheduledEvents.RequestTimeout"/> expires
    /// on the host clock, and the next poll waits its interval on that clock too: nothing in the observer runs on wall
    /// time, so the retirement arrives exactly when the law advances the clock past both.</summary>
    [Fact]
    public async Task MetadataDeadlineAndPollPacingRunOnTheHostClock() {
        var clock = new VirtualClock();
        var interval = TimeSpan.FromSeconds(seconds: 7);
        using var wire = new MetadataWire { StallFirstEvents = true };
        using var client = new HttpClient(handler: wire) { Timeout = Timeout.InfiniteTimeSpan };
        var retired = new TaskCompletionSource<DateTimeOffset>(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        var run = new AzureScheduledEvents(
            client: client,
            timeProvider: clock
        ).RunAsync(
            (deadline, _) => {
                retired.SetResult(result: deadline);
                return Task.CompletedTask;
            },
            interval,
            TestContext.Current.CancellationToken
        );

        await wire.EventsStalled.Task.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);
        await clock.ExpireAsync(
            ct: TestContext.Current.CancellationToken,
            dueTime: AzureScheduledEvents.RequestTimeout,
            pending: run
        );
        await clock.ExpireAsync(
            ct: TestContext.Current.CancellationToken,
            dueTime: interval,
            pending: run
        );
        await run.WaitAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(
            actual: await retired.Task,
            expected: DateTimeOffset.Parse(
                "2030-01-01T00:00:00Z",
                System.Globalization.CultureInfo.InvariantCulture
            )
        );
        Assert.Equal(
            actual: wire.Reads,
            expected: 3
        );
    }

    private sealed class MetadataWire : HttpMessageHandler {
        public TaskCompletionSource EventsStalled { get; } = new(creationOptions: TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StallFirstEvents { get; init; }

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
            if (
                StallFirstEvents &&
                (Reads == 2)
            ) {
                EventsStalled.TrySetResult();
                return StallAsync(cancellationToken: cancellationToken);
            }
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

            return Task.FromResult(result: new HttpResponseMessage(statusCode: HttpStatusCode.OK) {
                Content = new StringContent(
                content: body,
                encoding: Encoding.UTF8,
                mediaType: "application/json"
            ),
            });
        }

        private static async Task<HttpResponseMessage> StallAsync(CancellationToken cancellationToken) {
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: Timeout.InfiniteTimeSpan
            );
            throw new InvalidOperationException(message: "an infinite delay completed");
        }
    }
}
