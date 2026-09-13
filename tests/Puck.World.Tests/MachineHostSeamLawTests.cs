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
    public void AddonsAssemblyReferencesNoEmulatorCoreOrMachineHostProject() {
        var referenced = typeof(Puck.World.Addons.WorldAddonRuntime).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var denied in DeniedAssemblyNames) {
            Assert.False(
                condition: referenced.Contains(item: denied),
                userMessage: $"Puck.World.Addons' compiled assembly references '{denied}' — addons must remain dedicated to scripting guests."
            );
        }
        Assert.False(
            condition: referenced.Contains(item: "Puck.World.Machines"),
            userMessage: "Puck.World.Addons must not reference Puck.World.Machines."
        );
    }
    [Fact]
    public void DesktopWorldAssemblyReferencesNoCloudProviderProjects() {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(
            path1: baseDir,
            path2: "Puck.World.dll"
        );
        string assemblyPath;

        if (File.Exists(path: directPath)) {
            assemblyPath = directPath;
        } else {
            var directory = new DirectoryInfo(path: baseDir);

            while (
                (directory is not null) &&
                !File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
            ) {
                directory = directory.Parent;
            }

            Assert.NotNull(@object: directory);

            var releasePath = Path.Combine(
                directory!.FullName,
                "src",
                "Puck.World",
                "bin",
                "Release",
                "net10.0",
                "Puck.World.dll"
            );

            assemblyPath = (File.Exists(path: releasePath)
                ? releasePath
                : Path.Combine(
                    directory.FullName,
                    "src",
                    "Puck.World",
                    "bin",
                    "Debug",
                    "net10.0",
                    "Puck.World.dll"
                )
            );
        }

        Assert.True(
            condition: File.Exists(path: assemblyPath),
            userMessage: $"Could not find Puck.World.dll at '{assemblyPath}'."
        );

        var worldAssembly = Assembly.LoadFrom(assemblyFile: assemblyPath);
        var referenced = worldAssembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.World.Azure"),
            userMessage: "Puck.World must not reference concrete Puck.World.Azure — cloud extensions must be dynamic plugins."
        );
    }
    [Fact]
    public void DesktopWorldAssemblyReferencesNoOptionalExtensions() {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(
            path1: baseDir,
            path2: "Puck.World.dll"
        );
        string assemblyPath;

        if (File.Exists(path: directPath)) {
            assemblyPath = directPath;
        } else {
            var directory = new DirectoryInfo(path: baseDir);

            while (
                (directory is not null) &&
                !File.Exists(path: Path.Combine(
                path1: directory.FullName,
                path2: "Puck.slnx"
            ))
            ) {
                directory = directory.Parent;
            }

            Assert.NotNull(@object: directory);

            var releasePath = Path.Combine(
                directory!.FullName,
                "src",
                "Puck.World",
                "bin",
                "Release",
                "net10.0",
                "Puck.World.dll"
            );

            assemblyPath = (File.Exists(path: releasePath)
                ? releasePath
                : Path.Combine(
                    directory.FullName,
                    "src",
                    "Puck.World",
                    "bin",
                    "Debug",
                    "net10.0",
                    "Puck.World.dll"
                )
            );
        }

        Assert.True(
            condition: File.Exists(path: assemblyPath),
            userMessage: $"Could not find Puck.World.dll at '{assemblyPath}'."
        );

        var worldAssembly = Assembly.LoadFrom(assemblyFile: assemblyPath);
        var referenced = worldAssembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.Mcp"),
            userMessage: "Puck.World must not reference Puck.Mcp — control extensions must be dynamic plugins."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.World.AgentBridge"),
            userMessage: "Puck.World must not reference Puck.World.AgentBridge."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.World.AgentHarness"),
            userMessage: "Puck.World must not reference Puck.World.AgentHarness — agent extensions must be dynamic plugins."
        );
    }
    [Fact]
    public void MachineHostConstructorParameterIsTypedThroughTheSeamInterface() {
        var constructor = typeof(WorldServer).GetConstructors().Single();
        var parameter = constructor.GetParameters().Single(predicate: candidate => (candidate.Name == "machines"));

        Assert.Equal(
            expected: typeof(IWorldMachineHost),
            actual: parameter.ParameterType
        );
    }
    [Fact]
    public void MachinesAssemblyReferencesNoConcreteEmulatorProjects() {
        var referenced = typeof(WorldMachineHost).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.HumbleGamingBrick"),
            userMessage: "Puck.World.Machines must not reference concrete Puck.HumbleGamingBrick."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.AdvancedGamingBrick"),
            userMessage: "Puck.World.Machines must not reference concrete Puck.AdvancedGamingBrick."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.HumbleGamingBrick.Forge"),
            userMessage: "Puck.World.Machines must not reference concrete Puck.HumbleGamingBrick.Forge."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.AdvancedGamingBrick.Forge"),
            userMessage: "Puck.World.Machines must not reference concrete Puck.AdvancedGamingBrick.Forge."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.GamingBricks.Forge"),
            userMessage: "Machine extension and content contracts belong in Puck.Abstractions."
        );
    }
    [Fact]
    public void MachinesPropertyIsTypedThroughTheSeamInterfaceNotTheConcreteHost() {
        var property = typeof(WorldServer).GetProperty(
            name: nameof(WorldServer.Machines),
            bindingAttr: BindingFlags.Public | BindingFlags.Instance
        );

        Assert.NotNull(@object: property);
        Assert.Equal(
            expected: typeof(IWorldMachineHost),
            actual: property!.PropertyType
        );
    }
    [Fact]
    public void ServerAssemblyReferencesNoEmulatorCoreOrRendererProject() {
        var referenced = typeof(WorldServer).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        foreach (var denied in DeniedAssemblyNames) {
            Assert.False(
                condition: referenced.Contains(item: denied),
                userMessage: $"Puck.World.Server's compiled assembly references '{denied}' — the screen-machine host belongs in Puck.World.Machines, so a browser or silo build of Server must not carry this edge."
            );
        }
    }
    [Fact]
    public void SiloAssemblyReferencesNoCloudProviderProjects() {
        var referenced = typeof(Puck.World.Silo.WorldSiloApplication).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.World.Azure"),
            userMessage: "Puck.World.Silo must not reference concrete Puck.World.Azure — cloud extensions must be dynamic plugins."
        );
    }
    [Fact]
    public void SiloAssemblyReferencesNoConcreteEmulatorProjects() {
        var referenced = typeof(Puck.World.Silo.WorldSiloApplication).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.HumbleGamingBrick"),
            userMessage: "Puck.World.Silo must not reference concrete Puck.HumbleGamingBrick."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.AdvancedGamingBrick"),
            userMessage: "Puck.World.Silo must not reference concrete Puck.AdvancedGamingBrick."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.HumbleGamingBrick.Forge"),
            userMessage: "Puck.World.Silo must not reference concrete Puck.HumbleGamingBrick.Forge."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.AdvancedGamingBrick.Forge"),
            userMessage: "Puck.World.Silo must not reference concrete Puck.AdvancedGamingBrick.Forge."
        );
    }
    [Fact]
    public void SiloAssemblyReferencesNoOptionalExtensions() {
        var referenced = typeof(Puck.World.Silo.WorldSiloApplication).Assembly.GetReferencedAssemblies().Select(selector: name => name.Name).ToHashSet(comparer: StringComparer.Ordinal);

        Assert.False(
            condition: referenced.Contains(item: "Puck.Mcp"),
            userMessage: "Puck.World.Silo must not reference Puck.Mcp — control extensions must be dynamic plugins."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.World.AgentBridge"),
            userMessage: "Puck.World.Silo must not reference Puck.World.AgentBridge."
        );
        Assert.False(
            condition: referenced.Contains(item: "Puck.World.AgentHarness"),
            userMessage: "Puck.World.Silo must not reference Puck.World.AgentHarness — agent extensions must be dynamic plugins."
        );
    }
    [Fact]
    public void TheConcreteHostReachedThroughTheSeamAnswersOrdinaryReads() {
        using var fixture = Fixtures.FreshServer();

        Assert.IsAssignableFrom<IWorldMachineHost>(@object: fixture.Server.Machines);
        Assert.False(
            condition: fixture.Server.Machines.HasMachine(index: 0),
            userMessage: "the fixture world declares no machine screens"
        );

        var (ok, message, contentHash) = fixture.Server.Machines.TryInsert(
            index: 999,
            contentPath: "no-such-file.rom",
            engineId: null,
            options: null
        );

        Assert.False(
            condition: ok,
            userMessage: message
        );
        Assert.Equal(
            actual: message,
            expected: "no screen 999 declared"
        );
        Assert.Null(@object: contentHash);
    }
}
