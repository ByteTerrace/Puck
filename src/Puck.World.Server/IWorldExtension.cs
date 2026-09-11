namespace Puck.World.Server;

/// <summary>
/// Entry-point contract for a Puck world or silo extension (such as cloud storage, retirement,
/// authentication, or external operations).
/// </summary>
public interface IWorldExtension {
    /// <summary>Gets the user-friendly name of the extension.</summary>
    string Name { get; }
    /// <summary>Registers this extension's services, providers, and health handlers into the host registry.</summary>
    /// <param name="registry">The registry to register components into.</param>
    void Register(IWorldExtensionRegistry registry);
}

/// <summary>
/// Registry supplied to a world extension during activation to register storage, retirement,
/// authentication, operations, and health checks.
/// </summary>
public interface IWorldExtensionRegistry {
    /// <summary>Registers a silo object blob storage target provider.</summary>
    /// <param name="provider">The storage provider.</param>
    void RegisterStorage(WorldSiloStorageProvider provider);

    /// <summary>Registers a host retirement observer provider.</summary>
    /// <param name="provider">The retirement observer provider.</param>
    void RegisterRetirement(WorldSiloRetirementProvider provider);

    /// <summary>Registers a connection authentication provider.</summary>
    /// <param name="provider">The authentication provider.</param>
    void RegisterAuthentication(WorldAuthenticationProvider provider);

    /// <summary>Registers a configured external operation provider type.</summary>
    /// <param name="provider">The operation provider type.</param>
    void RegisterOperation(WorldExtensionProviderType provider);

    /// <summary>Registers a custom HTTP health endpoint handler.</summary>
    /// <param name="path">The exact HTTP request path (e.g. "/livez/azure").</param>
    /// <param name="handler">Callback taking silo liveness and returning content-type and body.</param>
    void RegisterHealthCheck(string path, Func<bool, (string ContentType, string Body)> handler);
}
