using System.Text.Json;
using Puck.Networking;

namespace Puck.World.Azure;

/// <summary>Observes Azure host maintenance independently of world simulation and replay. Every metadata read's
/// deadline, the poll interval, and the fallback retirement instant run on the host clock.</summary>
/// <param name="client">The metadata client; each read is bounded by <see cref="RequestTimeout"/> here, so the client
/// needs no timeout of its own.</param>
/// <param name="timeProvider">The host clock; <see langword="null"/> is <see cref="TimeProvider.System"/>.</param>
public sealed class AzureScheduledEvents(HttpClient client, TimeProvider? timeProvider = null) {
    private const string Metadata = "http://169.254.169.254/metadata/";

    private readonly TimeProvider m_clock = (timeProvider ?? TimeProvider.System);

    /// <summary>Gets how long, on the host clock, one metadata read may take.</summary>
    public static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(seconds: 5);

    private async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken) {
        using var deadline = new OperationDeadline(
            caller: cancellationToken,
            timeout: RequestTimeout,
            timeProvider: m_clock
        );

        cancellationToken = deadline.Token;
        using var request = new HttpRequestMessage(
            method: HttpMethod.Get,
            requestUri: (Metadata + path)
        );

        request.Headers.Add(
            name: "Metadata",
            value: "true"
        );
        using var response = await client.SendAsync(
            cancellationToken: cancellationToken,
            request: request
        );

        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(cancellationToken: cancellationToken),
            cancellationToken: cancellationToken
        );
    }

    /// <summary>Polls events for this VM and invokes the host's deadline-aware retirement operation.</summary>
    /// <param name="retire">Retires every world in this process; throws if durable retirement fails.</param>
    /// <param name="pollInterval">Time between metadata reads.</param>
    /// <param name="cancellationToken">Ends observation when the hosting process stops.</param>
    /// <returns>The observer lifetime. It never acknowledges a VM-wide event on behalf of other processes.</returns>
    public async Task RunAsync(Func<DateTimeOffset, CancellationToken, Task> retire, TimeSpan pollInterval, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(retire);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(
            pollInterval,
            TimeSpan.Zero
        );
        string? resourceName = null;

        while (!cancellationToken.IsCancellationRequested) {
            DateTimeOffset? retirementDeadline = null;

            try {
                if (resourceName is null) {
                    using var instance = await ReadAsync(
                        cancellationToken: cancellationToken,
                        path: "instance/compute?api-version=2021-02-01"
                    );

                    resourceName = instance.RootElement.GetProperty(propertyName: "name").GetString();
                }
                using var document = await ReadAsync(
                    cancellationToken: cancellationToken,
                    path: "scheduledevents?api-version=2020-07-01"
                );

                foreach (var item in document.RootElement.GetProperty(propertyName: "Events").EnumerateArray()) {
                    var kind = item.GetProperty(propertyName: "EventType").GetString();

                    if (kind is not ("Preempt" or "Terminate" or "Reboot" or "Redeploy")) { continue; }
                    if (!item.GetProperty(propertyName: "Resources").EnumerateArray().Any(predicate: x => string.Equals(
                        a: x.GetString(),
                        b: resourceName,
                        comparisonType: StringComparison.OrdinalIgnoreCase
                    ))) { continue; }
                    retirementDeadline = (DateTimeOffset.TryParse(
                        item.GetProperty(propertyName: "NotBefore").GetString(),
                        System.Globalization.CultureInfo.InvariantCulture,
                        out var parsed
                    )
                        ? parsed
                        : m_clock.GetUtcNow()
                    );
                    break;
                }
            } catch (Exception ex) when (((ex is HttpRequestException or JsonException) || ((ex is OperationCanceledException) && !cancellationToken.IsCancellationRequested))) {
                Console.Error.WriteLine(value: $"[azure.events: metadata unavailable ({ex.GetType().Name})]");
            }
            // Retirement errors, including HTTP and timeout errors from persistence, must escape the metadata retry.
            // Do not acknowledge a VM-wide event on behalf of other processes that may still be saving.
            if (retirementDeadline is { } deadline) {
                await retire(
                    deadline,
                    cancellationToken
                );
                return;
            }
            await Task.Delay(
                cancellationToken: cancellationToken,
                delay: pollInterval,
                timeProvider: m_clock
            );
        }
    }
}
