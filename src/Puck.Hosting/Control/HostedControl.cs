using Puck.Abstractions;

namespace Puck.Hosting;

/// <summary>
/// The one hosted control plane an extension contributes, such as remote MCP. A host that exposes an
/// <see cref="IControlSessionHost"/> starts it when a deployment names a control configuration. A host has at most one:
/// every registration shares <see cref="Key"/>, so a second is refused by name at composition.
/// </summary>
/// <param name="Create">Creates the service from the host's service provider, its control session host, and the full
/// path of the deployment's control configuration file.</param>
public sealed record HostedControl(Func<IServiceProvider, IControlSessionHost, string, IPuckHostedService> Create) {
    /// <summary>The contribution key every hosted control registers under.</summary>
    public const string Key = "control";
}
/// <summary>Registers <see cref="HostedControl"/> contributions.</summary>
public static class HostedControlRegistration {
    /// <summary>Adds the extension's hosted control plane.</summary>
    /// <param name="registry">The registering extension's registry.</param>
    /// <param name="control">The hosted control.</param>
    /// <exception cref="PuckExtensionException">Another extension already registers a hosted control.</exception>
    public static void AddHostedControl(this IPuckExtensionRegistry registry, HostedControl control) {
        ArgumentNullException.ThrowIfNull(argument: registry);
        registry.Add(
            contribution: control,
            key: HostedControl.Key
        );
    }
}
