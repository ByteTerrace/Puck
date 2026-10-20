using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingDeck.Forge;
using Puck.World.Machines;

namespace Puck.World.Testing;

/// <summary>The machine catalog a suite that carries the gaming cores boots and validates worlds against: both Gaming
/// Brick cores and their cartridge content, and the Humble Gaming Deck, composed the way a host composes its
/// extensions. A suite that carries no core links <c>TestMachines.Empty.cs</c> instead, so a world declaring a screen
/// machine is refused there by name.</summary>
internal static class TestMachines {
    /// <summary>Creates the catalog.</summary>
    /// <returns>A catalog of the Advanced and Humble Gaming Brick and Humble Gaming Deck extensions.</returns>
    internal static WorldMachineCatalog Catalog() => WorldMachineCatalog.From(extensions: Puck.Abstractions.PuckExtensionSet.Compose(extensions: [
        new AdvancedGamingBrickExtension(),
        new HumbleGamingBrickExtension(),
        new HumbleGamingDeckExtension(),
    ]));
}
