using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.GamingBricks.Forge;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Advanced Gaming Brick (AGB) screen-machine engine and cartridge forge compiler.
/// </summary>
public static class AdvancedGamingBrickServiceExtensions {
    /// <summary>
    /// Adds the Advanced Gaming Brick screen machine engine and AGB cartridge compiler to the service collection.
    /// </summary>
    /// <param name="services">The target service collection.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddAdvancedGamingBrick(this IServiceCollection services) {
        ArgumentNullException.ThrowIfNull(argument: services);

        services.AddSingleton<IScreenMachineEngine, AdvancedGamingBrickEngine>();
        services.AddSingleton<ICartridgeCompiler, AgbCartridgeCompiler>();

        return services;
    }
}
