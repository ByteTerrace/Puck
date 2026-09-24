using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions;
using Puck.Hosting;
using Puck.Mcp;

[assembly: PuckExtension(typeof(McpControlExtension))]

namespace Puck.Mcp;

/// <summary>
/// Contributes the remote MCP server as the host's <see cref="HostedControl"/>: one OAuth-protected Streamable HTTP
/// listener over the host's <see cref="IControlSessionHost"/>, configured by the deployment's remote MCP document. The
/// document's <c>services</c> settings select the one installed <see cref="McpServicesProvider"/>
/// (see <see cref="McpServicesProvider.Select"/>).
/// </summary>
public sealed class McpControlExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.Mcp";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddHostedControl(control: new(Create: static (services, host, configurationPath) => {
            var fullPath = Path.GetFullPath(path: configurationPath);
            var options = RemoteMcpServer.ReadOptionsAsync(configurationPath: fullPath).GetAwaiter().GetResult();

            return new McpHostedService(
                configurationPath: fullPath,
                host: McpServicesProvider.Select(
                    extensions: services.GetRequiredService<PuckExtensionSet>(),
                    host: host,
                    options: options
                ),
                options: options
            );
        }));
    }
}
