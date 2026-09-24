using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick.Forge;

[assembly: PuckExtension(extensionType: typeof(AdvancedGamingBrickExtension))]

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// Publishes the Advanced Gaming Brick engine and authored-content provider.
/// </summary>
public sealed class AdvancedGamingBrickExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.AdvancedGamingBrick.Forge";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddMachineEngine(
            engine: new AdvancedGamingBrickEngine(),
            contentProvider: new AgbCartridgeCompiler()
        );
    }
}
