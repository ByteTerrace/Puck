using Puck.Abstractions;
using Puck.Mcp.Azure;

[assembly: PuckExtension(typeof(AzureMcpExtension))]

namespace Puck.Mcp.Azure;

/// <summary>
/// Contributes the Azure delegated services to remote MCP as the <c>azure</c> <see cref="McpServicesProvider"/>: platform
/// onboarding (<c>puck_onboard</c>) and granted inventory and metrics observations (<c>puck_service_observe</c>), each
/// reached through a request-confined on-behalf-of exchange. Remote MCP selects it when a deployment configures
/// <c>services</c>; registration performs no cloud access.
/// </summary>
public sealed class AzureMcpExtension : IPuckExtension {
    /// <summary>The key the provider registers under.</summary>
    public const string ProviderKey = "azure";

    /// <inheritdoc/>
    public string Name => "Puck.Mcp.Azure";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        registry.Add(
            contribution: new McpServicesProvider(Create: static (host, options) => new AzureMcpHost(
                host: host,
                options: options
            )),
            key: ProviderKey
        );
    }
}
