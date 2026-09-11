using Puck.AdvancedGamingBrick.Forge;
using Puck.GamingBricks.Forge;

[assembly: PuckExtension(extensionType: typeof(AdvancedGamingBrickExtension))]

namespace Puck.AdvancedGamingBrick.Forge;

/// <summary>
/// First-class extension publishing the Advanced Gaming Brick engine and compiler.
/// </summary>
public sealed class AdvancedGamingBrickExtension : IGamingBrickExtension {
    /// <inheritdoc/>
    public string Name => "AdvancedGamingBrick";

    /// <inheritdoc/>
    public void Initialize(IGamingBrickExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterEngine(engine: new AdvancedGamingBrickEngine(), compiler: new AgbCartridgeCompiler());
    }
}
