using Puck.Abstractions;
using Puck.Hosting;
using Puck.Mcp;

[assembly: PuckExtension(typeof(McpControlExtension))]

namespace Puck.Mcp;

/// <summary>
/// First-class dynamic extension providing MCP control session hosting over streamable HTTP / OAuth.
/// </summary>
public sealed class McpControlExtension : IControlExtension {
    /// <inheritdoc/>
    public string Name => "Puck.Mcp";

    /// <inheritdoc/>
    public void Register(IControlExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterHostedControl(factory: static (services, host, configurationPath) => {
            var fullPath = Path.GetFullPath(path: configurationPath);
            var options = RemoteMcpServer.ReadOptionsAsync(configurationPath: fullPath).GetAwaiter().GetResult();
            var target = (options.Target ?? "silo");
            var controlHost = new McpHostingExtensions.HostedControlHost(host: host, target: target);

            return new McpHostingExtensions.McpHostedService(
                configurationPath: fullPath,
                host: controlHost,
                options: options
            );
        });
    }
}
