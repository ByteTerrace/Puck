using System.Reflection;

using Xunit;

using Puck.World.Server;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: the screen-machine host fold — <see cref="WorldServer"/> reaches a booted machine only
/// through <see cref="IWorldMachineHost"/> (the concrete <c>WorldMachineHost</c> now lives in
/// <c>Puck.World.Machines</c>), and <c>Puck.World.Server</c>'s own compiled assembly carries neither the
/// emulator cores nor <c>Puck.SdfVm</c> — the fold this proves.
/// </summary>
public sealed class MachineHostSeamLawTests {
    // The emulator cores and the renderer project the machine host used to pull into Puck.World.Server — every
    // name here must NOT appear among Server's own referenced assemblies once the fold holds.
    private static readonly string[] DeniedAssemblyNames = [
        "Puck.AdvancedGamingBrick",
        "Puck.GamingBricks",
        "Puck.GamingBricks.Forge",
        "Puck.HumbleGamingBrick",
        "Puck.HumbleGamingBrick.Forge",
        "Puck.SdfVm",
    ];

    [Fact]
    public void ServerAssemblyReferencesNoEmulatorCoreOrRendererProject() {
        var referenced = typeof(WorldServer).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var denied in DeniedAssemblyNames) {
            Assert.False(condition: referenced.Contains(denied), userMessage: $"Puck.World.Server's compiled assembly references '{denied}' — the screen-machine host belongs in Puck.World.Machines, so a browser or silo build of Server must not carry this edge.");
        }
    }
    [Fact]
    public void AddonsAssemblyReferencesNoEmulatorCoreOrMachineHostProject() {
        var referenced = typeof(Puck.World.Addons.WorldAddonRuntime).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var denied in DeniedAssemblyNames) {
            Assert.False(condition: referenced.Contains(denied), userMessage: $"Puck.World.Addons' compiled assembly references '{denied}' — addons must remain dedicated to scripting guests.");
        }
        Assert.False(condition: referenced.Contains("Puck.World.Machines"), userMessage: "Puck.World.Addons must not reference Puck.World.Machines.");
    }
    [Fact]
    public void MachinesAssemblyReferencesNoConcreteEmulatorProjects() {
        var referenced = typeof(WorldMachineHost).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(condition: referenced.Contains("Puck.HumbleGamingBrick"), userMessage: "Puck.World.Machines must not reference concrete Puck.HumbleGamingBrick.");
        Assert.False(condition: referenced.Contains("Puck.AdvancedGamingBrick"), userMessage: "Puck.World.Machines must not reference concrete Puck.AdvancedGamingBrick.");
        Assert.False(condition: referenced.Contains("Puck.HumbleGamingBrick.Forge"), userMessage: "Puck.World.Machines must not reference concrete Puck.HumbleGamingBrick.Forge.");
        Assert.False(condition: referenced.Contains("Puck.AdvancedGamingBrick.Forge"), userMessage: "Puck.World.Machines must not reference concrete Puck.AdvancedGamingBrick.Forge.");
    }
    [Fact]
    public void MachinesPropertyIsTypedThroughTheSeamInterfaceNotTheConcreteHost() {
        var property = typeof(WorldServer).GetProperty(name: nameof(WorldServer.Machines), bindingAttr: (BindingFlags.Public | BindingFlags.Instance));

        Assert.NotNull(@object: property);
        Assert.Equal(expected: typeof(IWorldMachineHost), actual: property!.PropertyType);
    }
    [Fact]
    public void MachineHostConstructorParameterIsTypedThroughTheSeamInterface() {
        var constructor = typeof(WorldServer).GetConstructors().Single();
        var parameter = constructor.GetParameters().Single(predicate: candidate => (candidate.Name == "machines"));

        Assert.Equal(expected: typeof(IWorldMachineHost), actual: parameter.ParameterType);
    }
    [Fact]
    public void TheConcreteHostReachedThroughTheSeamAnswersOrdinaryReads() {
        using var fixture = Fixtures.FreshServer();

        Assert.IsAssignableFrom<IWorldMachineHost>(@object: fixture.Server.Machines);
        Assert.False(condition: fixture.Server.Machines.HasMachine(index: 0), userMessage: "the fixture world declares no machine screens");

        var (ok, message, contentHash) = fixture.Server.Machines.TryInsert(index: 999, contentPath: "no-such-file.rom", engineId: null, options: null);

        Assert.False(condition: ok, userMessage: message);
        Assert.Equal(expected: "no screen 999 declared", actual: message);
        Assert.Null(@object: contentHash);
    }
}
