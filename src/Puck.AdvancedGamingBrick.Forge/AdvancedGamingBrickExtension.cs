using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick.Forge;

[assembly: PuckExtension(extensionType: typeof(AdvancedGamingBrickExtension))]

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// Publishes the Advanced Gaming Brick engine and authored-content provider.
/// </summary>
public sealed class AdvancedGamingBrickExtension : IMachineExtension {
    /// <inheritdoc/>
    public string Name => "AdvancedGamingBrick";

    /// <inheritdoc/>
    public void Initialize(IMachineExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterEngine(engine: new AdvancedGamingBrickEngine(), contentProvider: new AgbCartridgeCompiler());
    }
}
