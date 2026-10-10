using Puck.World.Machines;

namespace Puck.World.Testing;

/// <summary>The machine catalog a suite that carries no machine core boots and validates worlds against: no engine
/// and no content provider, so a world declaring a screen machine is refused by name. A suite that carries the Gaming
/// Brick cores links <c>TestMachines.Bricks.cs</c> instead.</summary>
internal static class TestMachines {
    /// <summary>Creates the catalog.</summary>
    /// <returns>An empty catalog.</returns>
    internal static WorldMachineCatalog Catalog() => WorldMachineCatalog.From(extensions: Puck.Abstractions.PuckExtensionSet.Compose(extensions: []));
}
