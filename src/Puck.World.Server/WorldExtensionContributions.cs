using System.Text.Json;
using Puck.Abstractions;
using Puck.Storage;

namespace Puck.World.Server;

/// <summary>A custom HTTP health endpoint an extension adds to a host that serves health.</summary>
/// <param name="Path">The exact request path, such as <c>/livez/azure</c>.</param>
/// <param name="Respond">Reads host liveness and returns the response's content type and body.</param>
public sealed record WorldHealthCheck(string Path, Func<bool, (string ContentType, string Body)> Respond);
/// <summary>Registers the world-hosting contribution kinds, each keyed by the selection key a deployment names.</summary>
public static class WorldExtensionContributions {
    /// <summary>Adds a connection authentication provider under its <see cref="WorldAuthenticationProvider.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="provider">The provider.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddAuthentication(this IPuckExtensionRegistry registry, WorldAuthenticationProvider provider) => registry.Add(
        contribution: provider,
        key: provider.Type
    );
    /// <summary>Adds an embedding provider type under its <see cref="WorldExtensionEmbeddingProviderType.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="provider">The provider type.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddEmbedding(this IPuckExtensionRegistry registry, WorldExtensionEmbeddingProviderType provider) => registry.Add(
        contribution: provider,
        key: provider.Type
    );
    /// <summary>Adds an HTTP health endpoint under its <see cref="WorldHealthCheck.Path"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="check">The endpoint.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the path.</exception>
    public static void AddHealthCheck(this IPuckExtensionRegistry registry, WorldHealthCheck check) => registry.Add(
        contribution: check,
        key: check.Path
    );
    /// <summary>Adds an external operation provider type under its <see cref="WorldExtensionProviderType.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="provider">The provider type.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddOperation(this IPuckExtensionRegistry registry, WorldExtensionProviderType provider) => registry.Add(
        contribution: provider,
        key: provider.Type
    );
    /// <summary>Adds a host retirement observer provider under its <see cref="WorldSiloRetirementProvider.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="provider">The provider.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddRetirement(this IPuckExtensionRegistry registry, WorldSiloRetirementProvider provider) => registry.Add(
        contribution: provider,
        key: provider.Type
    );
    /// <summary>Adds a persistence provider under its <see cref="WorldSiloStorageProvider.Type"/>.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="provider">The provider.</param>
    /// <exception cref="PuckExtensionException">Another registration holds the key.</exception>
    public static void AddStorage(this IPuckExtensionRegistry registry, WorldSiloStorageProvider provider) => registry.Add(
        contribution: provider,
        key: provider.Type
    );
}
/// <summary>The built-in world-hosting extension every world host composes: the <c>directory</c> persistence provider,
/// whose settings hold exactly one nonempty <c>path</c>.</summary>
public sealed class WorldServerExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.World.Server";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        registry.AddStorage(provider: new(
            "directory",
            static settings => {
                var path = WorldExtensionSettings.OnlySetting(
                    name: "path",
                    settings: settings
                );

                if (
                    (path.ValueKind != JsonValueKind.String) ||
                    string.IsNullOrWhiteSpace(value: path.GetString())
                ) {
                    throw new ArgumentException(message: "directory storage requires exactly one nonempty path setting.");
                }
                return new DirectoryObjectStorageTarget(path.GetString()!);
            }
        ));
    }
}
