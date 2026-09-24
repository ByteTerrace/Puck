using Puck.Abstractions;
using Puck.Hosting;

namespace Puck.Mcp;

/// <summary>
/// A services host an extension contributes to the remote MCP server: the <see cref="RemoteMcpHost"/> that interprets a
/// deployment's <c>services</c> settings, adds its own service tools, and owns their delegated authorization. It is
/// registered keyed by the provider's name. Remote MCP selects exactly one when a deployment configures
/// <c>services</c>, and none otherwise.
/// </summary>
/// <param name="Create">Creates the services host over the host's control sessions from validated options whose
/// <see cref="RemoteMcpOptions.Services"/> is set; the MCP server owns and disposes the result.</param>
public sealed record McpServicesProvider(Func<IControlSessionHost, RemoteMcpOptions, RemoteMcpHost> Create) {
    /// <summary>Selects the remote MCP host a deployment's options call for. Without <c>services</c> every caller
    /// attaches to the options' Console target and any installed provider is unused; with <c>services</c> the one
    /// installed provider creates the host.</summary>
    /// <param name="extensions">The host's composed extensions.</param>
    /// <param name="host">The host's control sessions.</param>
    /// <param name="options">The validated deployment options.</param>
    /// <returns>The selected host; the caller owns and disposes it.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="extensions"/>, <paramref name="host"/>, or
    /// <paramref name="options"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><c>services</c> is configured and no installed extension provides a
    /// services host, or more than one does; the refusal names each installed provider.</exception>
    public static RemoteMcpHost Select(PuckExtensionSet extensions, IControlSessionHost host, RemoteMcpOptions options) {
        ArgumentNullException.ThrowIfNull(argument: extensions);
        ArgumentNullException.ThrowIfNull(argument: host);
        ArgumentNullException.ThrowIfNull(argument: options);
        if (options.Services is null) {
            return new ConsoleTargetHost(
                host: host,
                target: (options.Target ?? throw new ArgumentException(message: "An MCP deployment without services requires a target."))
            );
        }

        return extensions.Select<McpServicesProvider>(
            key: null,
            purpose: "The MCP configuration names services"
        ).Create(
            arg1: host,
            arg2: options
        );
    }
}
