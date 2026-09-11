using Puck.Abstractions;

namespace Puck.Hosting;

/// <summary>
/// Entry-point contract for an optional host control-plane extension (such as MCP).
/// </summary>
public interface IControlExtension {
    /// <summary>Gets the user-friendly name of the extension.</summary>
    string Name { get; }

    /// <summary>Registers hosted control services with the host registry.</summary>
    /// <param name="registry">The registry to register control services into.</param>
    void Register(IControlExtensionRegistry registry);
}

/// <summary>
/// Registry supplied to a control extension to register hosted control services.
/// </summary>
public interface IControlExtensionRegistry {
    /// <summary>Registers a hosted control service factory.</summary>
    /// <param name="factory">Factory taking the host service provider, the control session host, and the configuration path, returning an <see cref="IPuckHostedService"/>.</param>
    void RegisterHostedControl(Func<IServiceProvider, IControlSessionHost, string, IPuckHostedService> factory);
}
