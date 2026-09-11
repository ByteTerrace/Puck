using Puck.Abstractions;

namespace Puck.World.Protocol;

/// <summary>
/// Entry-point contract for an optional autonomous participant or agent extension (such as Microsoft Agent Framework Harness).
/// </summary>
public interface IWorldAgentExtension {
    /// <summary>Gets the user-friendly name of the extension.</summary>
    string Name { get; }

    /// <summary>Registers agent services and runners with the host registry.</summary>
    /// <param name="registry">The registry to register agent services into.</param>
    void Register(IWorldAgentExtensionRegistry registry);
}

/// <summary>
/// Registry supplied to an agent extension to register autonomous participant services.
/// </summary>
public interface IWorldAgentExtensionRegistry {
    /// <summary>Registers an autonomous participant runner service factory.</summary>
    /// <param name="factory">Factory taking the host service provider and returning an <see cref="IPuckHostedService"/>.</param>
    void RegisterAgentRunner(Func<IServiceProvider, IPuckHostedService> factory);
}
