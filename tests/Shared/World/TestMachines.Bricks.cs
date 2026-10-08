using Puck.AdvancedGamingBrick.Forge;
using Puck.HumbleGamingBrick.Forge;
using Puck.World.Machines;

namespace Puck.World.Testing;

/// <summary>The machine catalog a suite that carries the Gaming Brick cores boots and validates worlds against: both
/// cores and their cartridge content, composed the way a host composes its extensions. A suite that carries no core
/// links <c>TestMachines.Empty.cs</c> instead, so a world declaring a screen machine is refused there by name.</summary>
internal static class TestMachines {
    /// <summary>Creates the catalog.</summary>
    /// <returns>A catalog of the Advanced and Humble Gaming Brick extensions.</returns>
    internal static WorldMachineCatalog Catalog() => WorldMachineCatalog.From(extensions: Puck.Abstractions.PuckExtensionSet.Compose(extensions: [
        new AdvancedGamingBrickExtension(),
        new HumbleGamingBrickExtension(),
    ]));
}
