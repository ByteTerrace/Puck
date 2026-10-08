using Xunit;

namespace Puck.World.Machines.Tests;

/// <summary>CONTRACT UNDER TEST: no assembly this suite links, its own fixtures included, resolves a per-user world state
/// root or device cache (<see cref="PerUserRootScan"/>); every consumer takes the roots its host or fixture hands it.</summary>
public sealed class WorldStateRootIsolationLawTests {
    [Fact]
    public void NoAssemblyThisSuiteLinksResolvesAPerUserRoot() => PerUserRootScan.AssertNoPerUserRootOutsideTheEntryPoint(suite: typeof(WorldStateRootIsolationLawTests).Assembly);
}
