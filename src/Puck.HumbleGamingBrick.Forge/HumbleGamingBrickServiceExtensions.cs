using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Humble Gaming Brick (HGB) screen-machine engines and cartridge forge compiler.
/// </summary>
public static class HumbleGamingBrickServiceExtensions {
    /// <summary>
    /// Adds the Humble Gaming Brick screen machine engines and CGB cartridge compiler to the service collection.
    /// </summary>
    /// <param name="services">The target service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddHumbleGamingBrick(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddSingleton<IScreenMachineEngine, GamingBrickEngine>();
        services.AddSingleton<IScreenMachineEngine, TuneInstrumentEngine>();
        services.AddSingleton<ICartridgeCompiler, HgbCartridgeCompiler>();

        return services;
    }
}
