using System.Text.Json;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>Azure implementations available for explicit silo composition; registration performs no cloud access.</summary>
public static class AzureSiloExtensions {
    /// <summary>Gets the API user admission extension; only clients acquire delegated credentials.</summary>
    public static WorldAuthenticationProvider Authentication { get; } = new("azure.api-users",
        (settings, federation) => new EntraWorldAuthenticator(settings, client: false, federation: federation),
        settings => {
            var client = new EntraWorldAuthenticator(settings, client: true);

            return (client, client.UserIdentity());
        });
    /// <summary>Gets the Blob persistence provider. Settings contain exactly one HTTPS accountUrl.</summary>
    public static WorldSiloStorageProvider Storage { get; } = new("azure.blob", settings => {
        var value = WorldExtensionSettings.OnlySetting(settings, "accountUrl");

        if ((value.ValueKind != JsonValueKind.String) || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps) || (endpoint.UserInfo.Length != 0) || (endpoint.Query.Length != 0) || (endpoint.Fragment.Length != 0)) {
            throw new ArgumentException(message: "azure.blob requires an HTTPS accountUrl without credentials, query, or fragment.");
        }
        return new AzureBlobObjectStorageTarget(serviceUri: endpoint);
    });
    /// <summary>Gets the VM maintenance observer. Settings contain exactly one positive integer pollSeconds.</summary>
    public static WorldSiloRetirementProvider Retirement { get; } = new("azure.scheduled-events", settings => {
        var value = WorldExtensionSettings.OnlySetting(settings, "pollSeconds");

        if ((value.ValueKind != JsonValueKind.Number) || !value.TryGetInt32(out var seconds) || (seconds < 1)) {
            throw new ArgumentException(message: "azure.scheduled-events requires positive integer pollSeconds.");
        }
        return new Observer(interval: TimeSpan.FromSeconds(seconds));
    });

    private sealed class Observer(TimeSpan interval) : IWorldHostRetirementObserver {
        private readonly HttpClient m_client = new(handler: new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(seconds: 5) };

        public Task RunAsync(Func<DateTimeOffset, CancellationToken, Task> retire, CancellationToken cancellationToken) =>
            new AzureScheduledEvents(client: m_client).RunAsync(cancellationToken: cancellationToken, pollInterval: interval, retire: retire);
        public void Dispose() => m_client.Dispose();
    }
}
