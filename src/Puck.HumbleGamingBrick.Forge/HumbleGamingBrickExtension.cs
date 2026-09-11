using Puck.Abstractions;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;

[assembly: PuckExtension(extensionType: typeof(HumbleGamingBrickExtension))]

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// First-class extension publishing the Humble Gaming Brick engines and compiler.
/// </summary>
public sealed class HumbleGamingBrickExtension : IGamingBrickExtension {
    /// <inheritdoc/>
    public string Name => "HumbleGamingBrick";

    /// <inheritdoc/>
    public void Initialize(IGamingBrickExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterEngine(engine: new GamingBrickEngine(), compiler: new HgbCartridgeCompiler());
        registry.RegisterEngine(engine: new TuneInstrumentEngine());
    }
}
