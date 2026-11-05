using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingDeck.Forge;

[assembly: PuckExtension(extensionType: typeof(HumbleGamingDeckExtension))]

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>
/// Publishes the Humble Gaming Deck engine and its diagnostic-program source provider. Native iNES and NES 2.0
/// images mount directly; authored .deck selections derive their executable images on demand.
/// </summary>
public sealed class HumbleGamingDeckExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.HumbleGamingDeck.Forge";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddMachineEngine(engine: new HumbleGamingDeckEngine(), contentProvider: new DeckContentProvider());
    }
}
