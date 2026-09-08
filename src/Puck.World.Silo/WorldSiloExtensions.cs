using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Puck.Storage;
using Puck.World.Azure;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>The silo distribution's explicit extension catalog. Provider names never enter simulation code.</summary>
internal static class WorldSiloExtensions {
    private sealed record ClusteringProvider(string Kind, Action<ISiloBuilder> Configure);

    private static readonly WorldExtensionRegistry<ClusteringProvider> Clustering = new(extensions: [
        new("Localhost", builder => builder.UseLocalhostClustering(serviceId: "puck-world-silo")),
    ], keyOf: provider => provider.Kind);

    internal static IReadOnlyCollection<string> ClusteringKinds => Clustering.Keys;
    private static readonly WorldExtensionRegistry<WorldAuthenticationProvider> Authentication = new([AzureSiloExtensions.Authentication], provider => provider.Type);

    internal static Puck.Networking.IAuthenticator Authenticate(WorldSiloExtension selection, Puck.Networking.IAuthenticator federation) {
        if (!Authentication.TryGet(selection.Type, out var provider)) { throw new ArgumentException($"Uninstalled authentication extension '{selection.Type}'."); }
        return provider.Server(selection.Settings, federation);
    }

    internal static void ConfigureClustering(ISiloBuilder builder, WorldSiloDefinition definition) {
        if (!Clustering.TryGet(definition.Clustering.Kind, out var provider)) {
            throw new ArgumentException(message: $"Uninstalled clustering extension '{definition.Clustering.Kind}'; name one of: {string.Join(separator: ", ", values: Clustering.Keys)}.");
        }
        provider.Configure(builder);
    }

    private static readonly WorldExtensionRegistry<WorldSiloStorageProvider> Storage = new([
        new("directory", settings => {
            var path = WorldExtensionSettings.OnlySetting(settings, "path");

            if ((path.ValueKind != JsonValueKind.String) || string.IsNullOrWhiteSpace(path.GetString())) {
                throw new ArgumentException(message: "directory storage requires exactly one nonempty path setting.");
            }
            return new DirectoryObjectStorageTarget(path.GetString()!);
        }),
        AzureSiloExtensions.Storage,
    ], provider => provider.Type);
    private static readonly WorldExtensionRegistry<WorldSiloRetirementProvider> Retirement = new([
        AzureSiloExtensions.Retirement,
    ], provider => provider.Type);

    internal static void Add(IServiceCollection services, WorldSiloDefinition definition) {
        if (!Storage.TryGet(definition.Store.Type, out var storage)) {
            throw new ArgumentException(message: $"Uninstalled silo storage extension '{definition.Store.Type}'.");
        }
        services.AddSingleton(storage.Create(definition.Store.Settings));
        if (definition.Lifecycle?.Observer is { } selection) {
            if (!Retirement.TryGet(selection.Type, out var observer)) {
                throw new ArgumentException(message: $"Uninstalled silo retirement extension '{selection.Type}'.");
            }
            // The factory validates settings when lifecycle services are resolved, before they start; DI owns disposal.
            services.AddSingleton<IWorldHostRetirementObserver>(implementationFactory: _ => observer.Create(selection.Settings));
        }
    }
}
