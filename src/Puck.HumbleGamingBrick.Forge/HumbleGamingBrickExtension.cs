using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;

[assembly: PuckExtension(extensionType: typeof(HumbleGamingBrickExtension))]

namespace Puck.HumbleGamingBrick.Forge;

/// <summary>
/// Publishes the Humble Gaming Brick engines and authored-content provider.
/// </summary>
public sealed class HumbleGamingBrickExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.HumbleGamingBrick.Forge";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddMachineEngine(
            engine: new GamingBrickEngine(),
            contentProvider: new HgbCartridgeCompiler()
        );
        registry.AddMachineEngine(engine: new TuneInstrumentEngine());
    }
}
