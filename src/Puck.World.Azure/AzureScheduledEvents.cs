using System.Text.Json;

namespace Puck.World.Azure;

/// <summary>Observes Azure host maintenance independently of world simulation and replay.</summary>
public sealed class AzureScheduledEvents(HttpClient client) {
    private const string Metadata = "http://169.254.169.254/metadata/";

    /// <summary>Polls events for this VM and invokes the host's deadline-aware retirement operation.</summary>
    /// <param name="retire">Retires every world in this process; throws if durable retirement fails.</param>
    /// <param name="pollInterval">Time between metadata reads.</param>
    /// <param name="cancellationToken">Ends observation when the hosting process stops.</param>
    /// <returns>The observer lifetime. It never acknowledges a VM-wide event on behalf of other processes.</returns>
    public async Task RunAsync(Func<DateTimeOffset, CancellationToken, Task> retire, TimeSpan pollInterval, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(retire);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(pollInterval, TimeSpan.Zero);
        string? resourceName = null;
        while (!cancellationToken.IsCancellationRequested) {
            DateTimeOffset? retirementDeadline = null;
            try {
                if (resourceName is null) {
                    using var instance = await ReadAsync("instance/compute?api-version=2021-02-01", cancellationToken);
                    resourceName = instance.RootElement.GetProperty("name").GetString();
                }
                using var document = await ReadAsync("scheduledevents?api-version=2020-07-01", cancellationToken);
                foreach (var item in document.RootElement.GetProperty("Events").EnumerateArray()) {
                    var kind = item.GetProperty("EventType").GetString();
                    if (kind is not ("Preempt" or "Terminate" or "Reboot" or "Redeploy")) { continue; }
                    if (!item.GetProperty("Resources").EnumerateArray().Any(x => string.Equals(x.GetString(), resourceName, StringComparison.OrdinalIgnoreCase))) { continue; }
                    retirementDeadline = DateTimeOffset.TryParse(item.GetProperty("NotBefore").GetString(), System.Globalization.CultureInfo.InvariantCulture, out var parsed)
                        ? parsed : DateTimeOffset.UtcNow;
                    break;
                }
            } catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested) {
                Console.Error.WriteLine($"[azure.events: metadata unavailable ({ex.GetType().Name})]");
            }
            // Retirement errors, including HTTP and timeout errors from persistence, must escape the metadata retry.
            // Do not acknowledge a VM-wide event on behalf of other processes that may still be saving.
            if (retirementDeadline is { } deadline) {
                await retire(deadline, cancellationToken);
                return;
            }
            await Task.Delay(pollInterval, cancellationToken);
        }
    }

    private async Task<JsonDocument> ReadAsync(string path, CancellationToken cancellationToken) {
        using var request = new HttpRequestMessage(HttpMethod.Get, Metadata + path);
        request.Headers.Add("Metadata", "true");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
    }
}
