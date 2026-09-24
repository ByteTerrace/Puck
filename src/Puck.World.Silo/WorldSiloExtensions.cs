using System.Collections.Frozen;
using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.World.Server;

namespace Puck.World.Silo;

/// <summary>Selects the silo document's providers from the composed extensions. Provider names never enter simulation
/// code.</summary>
internal static class WorldSiloExtensions {
    // The silo's own clustering kinds: an Orleans concern of this host, not an extension contribution.
    private static readonly FrozenDictionary<string, Action<ISiloBuilder>> Clustering = new Dictionary<string, Action<ISiloBuilder>>(comparer: StringComparer.Ordinal) {
        ["Localhost"] = static builder => builder.UseLocalhostClustering(serviceId: "puck-world-silo"),
    }.ToFrozenDictionary(comparer: StringComparer.Ordinal);

    internal static IReadOnlyCollection<string> ClusteringKinds => Clustering.Keys;

    internal static void Add(IServiceCollection services, WorldSiloDefinition definition, PuckExtensionSet extensions) {
        var storage = extensions.Select<WorldSiloStorageProvider>(
            key: definition.Store.Type,
            purpose: "Silo storage"
        );

        services.AddSingleton(implementationInstance: storage.Create(definition.Store.Settings));

        if (definition.Lifecycle?.Observer is { } selection) {
            var observer = extensions.Select<WorldSiloRetirementProvider>(
                key: selection.Type,
                purpose: "Silo retirement observer"
            );

            // The factory validates settings when lifecycle services are resolved, before they start; DI owns disposal.
            // The observer runs on the silo's one clock.
            services.AddSingleton<IWorldHostRetirementObserver>(implementationFactory: sp => observer.Create(
                selection.Settings,
                sp.GetRequiredService<WorldSiloHost>().Clock
            ));
        }
    }
    internal static Puck.Networking.IAuthenticator Authenticate(PuckExtensionSet extensions, WorldSiloExtension selection, Puck.Networking.IAuthenticator federation, TimeProvider clock) =>
        extensions.Select<WorldAuthenticationProvider>(
            key: selection.Type,
            purpose: "Authentication"
        ).Server(
            selection.Settings,
            federation,
            clock
        );
    internal static void ConfigureClustering(ISiloBuilder builder, WorldSiloDefinition definition) {
        if (!Clustering.TryGetValue(
            key: definition.Clustering.Kind,
            value: out var configure
        )) {
            throw new ArgumentException(message: $"Uninstalled clustering extension '{definition.Clustering.Kind}'; name one of: {string.Join(
                separator: ", ",
                values: Clustering.Keys
            )}.");
        }
        configure(builder);
    }
}
