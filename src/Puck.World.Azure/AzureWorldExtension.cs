using Puck.Abstractions;
using Puck.World.Server;

[assembly: PuckExtension(typeof(Puck.World.Azure.AzureWorldExtension))]

namespace Puck.World.Azure;

/// <summary>
/// Contributes the Azure world-hosting providers: <c>azure.blob</c> persistence, the <c>azure.scheduled-events</c>
/// retirement observer, <c>azure.api-users</c> connection authentication, the <c>azure.resource</c> operation provider,
/// and the <c>/livez/azure</c> health endpoint. Registration performs no cloud access.
/// </summary>
public sealed class AzureWorldExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.World.Azure";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddStorage(provider: AzureSiloExtensions.Storage);
        registry.AddRetirement(provider: AzureSiloExtensions.Retirement);
        registry.AddAuthentication(provider: AzureSiloExtensions.Authentication);
        registry.AddOperation(provider: AzureConfiguredProvider.Registration);
        registry.AddHealthCheck(check: new(
            Path: "/livez/azure",
            Respond: static live => ("application/json", AzureApplicationHealth.Response(live: live))
        ));
    }
}
