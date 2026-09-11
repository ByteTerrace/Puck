using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The silo distribution's dynamic extension catalog. Provider names never enter simulation code.</summary>
internal static class WorldSiloExtensions {
    private sealed record ClusteringProvider(string Kind, Action<ISiloBuilder> Configure);

    private static readonly WorldExtensionRegistry<ClusteringProvider> Clustering = new(extensions: [
        new("Localhost", builder => builder.UseLocalhostClustering(serviceId: "puck-world-silo")),
    ], keyOf: provider => provider.Kind);

    internal static IReadOnlyCollection<string> ClusteringKinds => Clustering.Keys;

    private static readonly Dictionary<string, WorldAuthenticationProvider> s_authentication = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WorldSiloStorageProvider> s_storage = new(StringComparer.Ordinal) {
        ["directory"] = new("directory", settings => {
            var path = WorldExtensionSettings.OnlySetting(settings, "path");

            if ((path.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(path.GetString())) {
                throw new ArgumentException(message: "directory storage requires exactly one nonempty path setting.");
            }
            return new DirectoryObjectStorageTarget(path.GetString()!);
        }),
    };
    private static readonly Dictionary<string, WorldSiloRetirementProvider> s_retirement = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, WorldExtensionProviderType> s_operations = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Func<bool, (string ContentType, string Body)>> s_healthChecks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock s_gate = new();

    internal static void RegisterStorage(WorldSiloStorageProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (s_gate) {
            s_storage[provider.Type] = provider;
        }
    }

    internal static void RegisterRetirement(WorldSiloRetirementProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (s_gate) {
            s_retirement[provider.Type] = provider;
        }
    }

    internal static void RegisterAuthentication(WorldAuthenticationProvider provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (s_gate) {
            s_authentication[provider.Type] = provider;
        }
    }

    internal static void RegisterOperation(WorldExtensionProviderType provider) {
        ArgumentNullException.ThrowIfNull(argument: provider);
        lock (s_gate) {
            s_operations[provider.Type] = provider;
        }
    }

    internal static void RegisterHealthCheck(string path, Func<bool, (string ContentType, string Body)> handler) {
        ArgumentNullException.ThrowIfNull(argument: path);
        ArgumentNullException.ThrowIfNull(argument: handler);
        lock (s_gate) {
            s_healthChecks[path] = handler;
        }
    }

    internal static bool TryGetHealthCheck(string path, out Func<bool, (string ContentType, string Body)>? handler) {
        lock (s_gate) {
            return s_healthChecks.TryGetValue(key: path, value: out handler);
        }
    }

    internal static Puck.Networking.IAuthenticator Authenticate(WorldSiloExtension selection, Puck.Networking.IAuthenticator federation) {
        WorldAuthenticationProvider? provider;

        lock (s_gate) {
            s_authentication.TryGetValue(key: selection.Type, value: out provider);
        }

        if (provider is null) {
            throw new ArgumentException($"Uninstalled authentication extension '{selection.Type}'.");
        }

        return provider.Server(selection.Settings, federation);
    }

    internal static void ConfigureClustering(ISiloBuilder builder, WorldSiloDefinition definition) {
        if (!Clustering.TryGet(definition.Clustering.Kind, out var provider)) {
            throw new ArgumentException(message: $"Uninstalled clustering extension '{definition.Clustering.Kind}'; name one of: {string.Join(separator: ", ", values: Clustering.Keys)}.");
        }
        provider.Configure(builder);
    }

    internal static void Add(IServiceCollection services, WorldSiloDefinition definition) {
        WorldSiloStorageProvider? storage;

        lock (s_gate) {
            s_storage.TryGetValue(key: definition.Store.Type, value: out storage);
        }

        if (storage is null) {
            throw new ArgumentException(message: $"Uninstalled silo storage extension '{definition.Store.Type}'.");
        }

        services.AddSingleton(storage.Create(definition.Store.Settings));

        if (definition.Lifecycle?.Observer is { } selection) {
            WorldSiloRetirementProvider? observer;

            lock (s_gate) {
                s_retirement.TryGetValue(key: selection.Type, value: out observer);
            }

            if (observer is null) {
                throw new ArgumentException(message: $"Uninstalled silo retirement extension '{selection.Type}'.");
            }

            // The factory validates settings when lifecycle services are resolved, before they start; DI owns disposal.
            services.AddSingleton<IWorldHostRetirementObserver>(implementationFactory: _ => observer.Create(selection.Settings));
        }
    }

    internal sealed class Registry : IWorldExtensionRegistry {
        public void RegisterStorage(WorldSiloStorageProvider provider) => WorldSiloExtensions.RegisterStorage(provider: provider);
        public void RegisterRetirement(WorldSiloRetirementProvider provider) => WorldSiloExtensions.RegisterRetirement(provider: provider);
        public void RegisterAuthentication(WorldAuthenticationProvider provider) => WorldSiloExtensions.RegisterAuthentication(provider: provider);
        public void RegisterOperation(WorldExtensionProviderType provider) => WorldSiloExtensions.RegisterOperation(provider: provider);
        public void RegisterHealthCheck(string path, Func<bool, (string ContentType, string Body)> handler) => WorldSiloExtensions.RegisterHealthCheck(path: path, handler: handler);
    }
}
