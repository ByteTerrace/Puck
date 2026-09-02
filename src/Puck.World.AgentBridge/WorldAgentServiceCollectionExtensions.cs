using Microsoft.Extensions.DependencyInjection;
using Puck.Commands;
using Puck.World.Protocol;

namespace Puck.World.Agents;

/// <summary>Opt-in host composition for the provider-neutral Puck world agent bridge.</summary>
public static class WorldAgentServiceCollectionExtensions {
    /// <summary>
    /// Adds the bounded agent-to-host-thread mailbox and maps the host's existing loopback transport to the
    /// explicit-principal link used by <see cref="WorldAgentBridge"/>.
    /// </summary>
    /// <param name="services">The host service collection.</param>
    /// <param name="mailboxCapacity">The maximum number of agent operations waiting for the host thread.</param>
    /// <param name="maximumOperationsPerFrame">The maximum number of queued operations drained during one host frame.</param>
    /// <returns><paramref name="services"/> for chaining.</returns>
    /// <remarks>
    /// This is deliberately never called by the base <c>Puck.World</c> composition root. An agent-capable host opts in
    /// after registering its <see cref="LoopbackTransport"/> and the launcher's snapshot-input capture pipeline.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A capacity or per-frame limit is not positive.</exception>
    public static IServiceCollection AddPuckWorldAgentBridge(
        this IServiceCollection services,
        int mailboxCapacity = 256,
        int maximumOperationsPerFrame = 32
    ) {
        ArgumentNullException.ThrowIfNull(argument: services);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: mailboxCapacity);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value: maximumOperationsPerFrame);

        services.AddSingleton<IPrincipalServerLink>(
            implementationFactory: static provider => provider.GetRequiredService<LoopbackTransport>()
        );
        services.AddSingleton(
            implementationFactory: _ => new WorldAgentMailbox(
                capacity: mailboxCapacity,
                maximumOperationsPerFrame: maximumOperationsPerFrame
            )
        );
        services.AddSingleton<IWorldAgentDispatcher>(
            implementationFactory: static provider => provider.GetRequiredService<WorldAgentMailbox>()
        );
        services.AddSingleton<ISnapshotInputCapture>(
            implementationFactory: static provider => provider.GetRequiredService<WorldAgentMailbox>()
        );

        return services;
    }
}
