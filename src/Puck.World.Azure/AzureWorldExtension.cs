using Puck.Abstractions;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.World.Azure.AzureWorldExtension))]

namespace Puck.World.Azure;

/// <summary>
/// First-class dynamic extension implementing <see cref="IWorldExtension"/> for Azure services:
/// blob persistence, scheduled-events retirement observer, Entra ID authentication,
/// ARM operations, and /livez/azure health check.
/// </summary>
public sealed class AzureWorldExtension : IWorldExtension {
    /// <inheritdoc/>
    public string Name => "Azure";

    /// <inheritdoc/>
    public void Register(IWorldExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterStorage(provider: AzureSiloExtensions.Storage);
        registry.RegisterRetirement(provider: AzureSiloExtensions.Retirement);
        registry.RegisterAuthentication(provider: AzureSiloExtensions.Authentication);
        registry.RegisterOperation(provider: AzureConfiguredProvider.Registration);
        registry.RegisterHealthCheck(path: "/livez/azure", handler: static live => ("application/json", AzureApplicationHealth.Response(live: live)));
    }
}
