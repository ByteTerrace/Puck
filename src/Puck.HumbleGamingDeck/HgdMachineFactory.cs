using Microsoft.Extensions.DependencyInjection;

namespace Puck.HumbleGamingDeck;

/// <summary>Creates isolated machine instances using the shared machine/fork ownership lifecycle.</summary>
public static class HgdMachineFactory {
    /// <summary>Creates a machine and its owning service scope.</summary>
    /// <param name="configuration">The immutable machine inputs shared with future forks.</param>
    /// <param name="compose">The optional additional service registrations, repeated when a fork's scope is first created.</param>
    /// <returns>The instance that the caller owns and disposes.</returns>
    /// <exception cref="ArgumentNullException">The configuration is null.</exception>
    public static MachineInstance<HgdMachine, HgdMachineConfiguration> Create(HgdMachineConfiguration configuration,
        Action<IServiceCollection>? compose = null) {
        ArgumentNullException.ThrowIfNull(argument: configuration);
        var services = new ServiceCollection();

        _ = services.AddSingleton(implementationInstance: configuration);
        _ = services.AddScoped<HgdMachine>();
        _ = services.AddScoped(implementationFactory: static provider => provider.GetRequiredService<HgdMachine>().Clock);
        _ = services.AddScoped(implementationFactory: static provider => provider.GetRequiredService<HgdMachine>().Cpu);
        _ = services.AddScoped(implementationFactory: static provider => provider.GetRequiredService<HgdMachine>().Bus);
        _ = services.AddScoped(implementationFactory: static provider => provider.GetRequiredService<HgdMachine>().Bus.Mapper);
        compose?.Invoke(obj: services);
        var provider = services.BuildServiceProvider(validateScopes: true);
        var scope = provider.CreateScope();

        return new(provider: provider, scope: scope, machine: scope.ServiceProvider.GetRequiredService<HgdMachine>(),
            configuration: configuration, compose: compose, create: Create);
    }
}
