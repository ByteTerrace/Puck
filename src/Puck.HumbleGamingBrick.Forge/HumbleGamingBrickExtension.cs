using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;

[assembly: PuckExtension(extensionType: typeof(HumbleGamingBrickExtension))]

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Publishes the Humble Gaming Brick engines and authored-content provider.
/// </summary>
public sealed class HumbleGamingBrickExtension : IMachineExtension {
    /// <inheritdoc/>
    public string Name => "HumbleGamingBrick";

    /// <inheritdoc/>
    public void Initialize(IMachineExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.RegisterEngine(engine: new GamingBrickEngine(), contentProvider: new HgbCartridgeCompiler());
        registry.RegisterEngine(engine: new TuneInstrumentEngine());
    }
}
