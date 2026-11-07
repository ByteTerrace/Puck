using System.Text.Json;
using Azure.Identity;
using Puck.Storage.Azure;
using Puck.World.Server;

namespace Puck.World.Azure;

/// <summary>Azure implementations available for explicit silo composition; registration performs no cloud access.</summary>
public static class AzureSiloExtensions {
    /// <summary>Gets the API user admission extension; only clients acquire delegated credentials.</summary>
    public static WorldAuthenticationProvider Authentication { get; } = new(
        "azure.api-users",
        (settings, federation, clock) => new EntraWorldAuthenticator(
            settings,
            client: false,
            federation: federation,
            timeProvider: clock
        ),
        (settings, clock) => {
            var client = new EntraWorldAuthenticator(
                settings,
                client: true,
                timeProvider: clock
            );

            // The host's contract is provider-neutral: a credential the provider cannot obtain is an invalid
            // configuration, reported as one, never as this provider's own exception type.
            try {
                return (client, client.UserIdentity());
            } catch (AuthenticationFailedException error) {
                throw new InvalidOperationException(
                    innerException: error,
                    message: $"azure.api-users could not obtain a user token: {error.Message}"
                );
            }
        }
    );
    /// <summary>Gets the Blob persistence provider. Settings name exactly one of <c>accountUrl</c> (an HTTPS service URI
    /// the ambient identity authenticates to) or <c>connectionString</c> (a development or emulator account whose key
    /// the operator holds). An <c>accountUrl</c> may add <c>edgeNamespace</c>, the platform edge namespace the store is
    /// reached through, and <c>listEndpoint</c>, the direct account connection listing uses because the edge cannot
    /// serve one.</summary>
    public static WorldStorageProvider Storage { get; } = new(
        "azure.blob",
        static settings => ReadBlobTarget(settings: settings)
    );

    private static AzureBlobObjectStorageTarget ReadBlobTarget(JsonElement settings) {
        const string Usage = "azure.blob settings name exactly one of accountUrl (HTTPS, without credentials, query or fragment) or connectionString; an accountUrl may add a nonempty edgeNamespace and a listEndpoint.";

        if (settings.ValueKind != JsonValueKind.Object) { throw new ArgumentException(message: Usage); }
        string? accountUrl = null;
        string? connectionString = null;
        string? edgeNamespace = null;
        string? listEndpoint = null;

        foreach (var property in settings.EnumerateObject()) {
            if (
                (property.Value.ValueKind != JsonValueKind.String) ||
                string.IsNullOrWhiteSpace(value: property.Value.GetString())
            ) { throw new ArgumentException(message: Usage); }
            var value = property.Value.GetString()!;

            switch (property.Name) {
                case "accountUrl" when (accountUrl is null): accountUrl = value; break;
                case "connectionString" when (connectionString is null): connectionString = value; break;
                case "edgeNamespace" when (edgeNamespace is null): edgeNamespace = value; break;
                case "listEndpoint" when (listEndpoint is null): listEndpoint = value; break;
                default: throw new ArgumentException(message: Usage);
            }
        }
        if (connectionString is not null) {
            if ((accountUrl is not null) || (edgeNamespace is not null) || (listEndpoint is not null)) { throw new ArgumentException(message: Usage); }
            return new AzureBlobObjectStorageTarget(connectionString: connectionString);
        }
        if (
            !Uri.TryCreate(
            accountUrl,
            UriKind.Absolute,
            out var endpoint
        ) ||
            (endpoint.Scheme != Uri.UriSchemeHttps) ||
            (endpoint.UserInfo.Length != 0) ||
            (endpoint.Query.Length != 0) ||
            (endpoint.Fragment.Length != 0) ||
            ((listEndpoint is not null) && (edgeNamespace is null))
        ) { throw new ArgumentException(message: Usage); }
        return new AzureBlobObjectStorageTarget(serviceUri: endpoint) {
            DirectEndpoint = listEndpoint,
            EdgeNamespace = edgeNamespace,
        };
    }
    /// <summary>Gets the VM maintenance observer. Settings contain exactly one positive integer pollSeconds.</summary>
    public static WorldSiloRetirementProvider Retirement { get; } = new(
        "azure.scheduled-events",
        (settings, clock) => {
            var value = WorldExtensionSettings.OnlySetting(
                name: "pollSeconds",
                settings: settings
            );

            if (
                (value.ValueKind != JsonValueKind.Number) ||
                !value.TryGetInt32(value: out var seconds) ||
                (seconds < 1)
            ) {
                throw new ArgumentException(message: "azure.scheduled-events requires positive integer pollSeconds.");
            }
            return new Observer(
                clock: clock,
                interval: TimeSpan.FromSeconds(seconds: seconds)
            );
        }
    );

    private sealed class Observer(TimeSpan interval, TimeProvider clock) : IWorldHostRetirementObserver {
        private readonly HttpClient m_client = new(handler: new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

        public void Dispose() => m_client.Dispose();
        public Task RunAsync(Func<DateTimeOffset, CancellationToken, Task> retire, CancellationToken cancellationToken) =>
            new AzureScheduledEvents(
                client: m_client,
                timeProvider: clock
            ).RunAsync(
                cancellationToken: cancellationToken,
                pollInterval: interval,
                retire: retire
            );
    }
}
