using Microsoft.Extensions.DependencyInjection;
using Puck.Abstractions.Counting;
using Puck.Abstractions.Gpu;
using Puck.Commands;
using Puck.World.Server;

namespace Puck.World;

/// <summary>Registers <c>world.counters</c> over the container's own registrations, and the server's own counter
/// sources it reads.</summary>
public static class WorldCountersServiceRegistration {
    /// <summary>Registers <see cref="WorldCountersCommandModule"/> as an <see cref="ICommandModule"/> that reads every
    /// <see cref="IWorkCounterSource"/> registered in <paramref name="services"/> and, when one is registered, the
    /// <see cref="IGpuWorkRegistry"/>. A source becomes visible to the verb by being registered, in any order, before
    /// or after this call.</summary>
    /// <param name="services">The World's service collection.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWorldCounters(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddSingleton<ICommandModule>(implementationFactory: static sp => new WorldCountersCommandModule(
            gpu: sp.GetService<IGpuWorkRegistry>(),
            sources: sp.GetServices<IWorkCounterSource>()
        ));
    }
    /// <summary>Registers the registered <see cref="WorldServer"/>'s own counters as <see cref="IWorkCounterSource"/>s:
    /// <c>state.arena</c> and <c>state.search</c> through the server's forwarders
    /// (<see cref="WorldServer.ArenaWorkSource"/>, <see cref="WorldServer.SearchWorkSource"/>), which carry a retired
    /// instance's totals across a definition install so a reading never goes down, and <c>state.rules</c>, the rule
    /// evaluator that lives as long as the server.</summary>
    /// <param name="services">The World's service collection, which registers the <see cref="WorldServer"/>.</param>
    /// <returns><paramref name="services"/>.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is <see langword="null"/>.</exception>
    public static IServiceCollection AddWorldServerCounters(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(services);

        return services
            .AddSingleton<IWorkCounterSource>(implementationFactory: static sp => sp.GetRequiredService<WorldServer>().ArenaWorkSource)
            .AddSingleton<IWorkCounterSource>(implementationFactory: static sp => sp.GetRequiredService<WorldServer>().RuleHost.RuleWork)
            .AddSingleton<IWorkCounterSource>(implementationFactory: static sp => sp.GetRequiredService<WorldServer>().SearchWorkSource);
    }
}
