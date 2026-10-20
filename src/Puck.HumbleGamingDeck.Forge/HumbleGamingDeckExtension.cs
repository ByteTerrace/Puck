using Puck.Abstractions;
using Puck.Abstractions.Machines;
using Puck.HumbleGamingDeck.Forge;

[assembly: PuckExtension(extensionType: typeof(HumbleGamingDeckExtension))]

namespace Puck.HumbleGamingDeck.Forge;

/// <summary>
/// Publishes the Humble Gaming Deck engine. The Deck mounts native iNES and NES 2.0 images as raw bytes, so no content
/// provider registers with it and a content folder offers every file it holds.
/// </summary>
public sealed class HumbleGamingDeckExtension : IPuckExtension {
    /// <inheritdoc/>
    public string Name => "Puck.HumbleGamingDeck.Forge";

    /// <inheritdoc/>
    public void Register(IPuckExtensionRegistry registry) {
        ArgumentNullException.ThrowIfNull(argument: registry);

        registry.AddMachineEngine(engine: new HumbleGamingDeckEngine());
    }
}
