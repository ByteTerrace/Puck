using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.World.Machines;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldMachineExtensionLoader"/> and <see cref="PuckExtensionLoadContext"/>
/// discover and dynamically load machine extensions without compile-time host coupling, delegating shared
/// contracts to the default load context and isolating private extension dependencies.
/// </summary>
public sealed class DynamicExtensionLoaderLawTests {
    private static string FindHgbForgeAssembly() {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(baseDir, "Puck.HumbleGamingBrick.Forge.dll");

        if (File.Exists(directPath)) {
            return directPath;
        }

        var directory = new DirectoryInfo(baseDir);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);

        var releaseBuild = Path.Combine(directory!.FullName, "src", "Puck.HumbleGamingBrick.Forge", "bin", "Release", "net10.0", "Puck.HumbleGamingBrick.Forge.dll");

        if (File.Exists(releaseBuild)) {
            return releaseBuild;
        }

        var debugBuild = Path.Combine(directory.FullName, "src", "Puck.HumbleGamingBrick.Forge", "bin", "Debug", "net10.0", "Puck.HumbleGamingBrick.Forge.dll");

        Assert.True(File.Exists(debugBuild), $"Could not find Puck.HumbleGamingBrick.Forge.dll at '{releaseBuild}' or '{debugBuild}'.");

        return debugBuild;
    }

    [Fact]
    public void LoadFromAssembly_LoadsAndRegistersExtension() {
        var assemblyPath = FindHgbForgeAssembly();
        var loaded = WorldMachineExtensionLoader.LoadFromAssembly(assemblyPath: assemblyPath);

        Assert.NotEmpty(loaded);
        Assert.Contains(loaded, extension => string.Equals(extension.Name, "HumbleGamingBrick", StringComparison.Ordinal));
        Assert.True(WorldScreenMachineEngines.IsRegistered("gaming-brick"));
        Assert.True(WorldScreenMachineEngines.CompilesCartridges("gaming-brick"));
        Assert.True(WorldScreenMachineEngines.CartridgeCompilers.ContainsKey("gaming-brick"));
    }

    [Fact]
    public void LoadFromDirectory_DiscoversSubdirectoryExtensions() {
        var assemblyPath = FindHgbForgeAssembly();
        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-test-");

        try {
            var subDir = Path.Combine(tempRoot.FullName, "HumbleGamingBrick");
            Directory.CreateDirectory(subDir);

            var targetDll = Path.Combine(subDir, "Puck.HumbleGamingBrick.Forge.dll");
            File.Copy(sourceFileName: assemblyPath, destFileName: targetDll, overwrite: true);

            var depsFile = Path.ChangeExtension(assemblyPath, ".deps.json");

            if (File.Exists(depsFile)) {
                File.Copy(sourceFileName: depsFile, destFileName: Path.Combine(subDir, "Puck.HumbleGamingBrick.Forge.deps.json"), overwrite: true);
            }

            var loaded = WorldMachineExtensionLoader.LoadFromDirectory(directoryPath: tempRoot.FullName);

            Assert.NotEmpty(loaded);
            Assert.Contains(loaded, extension => string.Equals(extension.Name, "HumbleGamingBrick", StringComparison.Ordinal));
        } finally {
            try {
                tempRoot.Delete(recursive: true);
            } catch (Exception) {
                // On Windows, loaded assembly files remain memory-mapped and locked until ALC unload or process exit.
            }
        }
    }

    [Fact]
    public void LoadFromDirectory_GracefullyIgnoresInvalidFiles() {
        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-invalid-");

        try {
            var fakeDll = Path.Combine(tempRoot.FullName, "not-a-real-assembly.dll");
            File.WriteAllText(path: fakeDll, contents: "corrupted assembly data");

            var logMessages = new List<string>();
            var loaded = WorldMachineExtensionLoader.LoadFromDirectory(directoryPath: tempRoot.FullName, log: msg => logMessages.Add(msg));

            Assert.Empty(loaded);
            Assert.NotEmpty(logMessages);
            Assert.Contains(logMessages, msg => msg.Contains("Failed to load candidate assembly", StringComparison.Ordinal));
        } finally {
            tempRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public void PuckExtensionLoadContext_DefersSharedHostContractsToDefaultContext() {
        var assemblyPath = FindHgbForgeAssembly();
        var context = new PuckExtensionLoadContext(pluginPath: assemblyPath);

        try {
            var hostAbstractionsAssembly = typeof(IScreenMachineEngine).Assembly;
            var loadedAbstractionsAssembly = context.LoadFromAssemblyName(assemblyName: hostAbstractionsAssembly.GetName());

            // Proves type identity matches the host's AssemblyLoadContext.Default
            Assert.Same(expected: hostAbstractionsAssembly, actual: loadedAbstractionsAssembly);

            var hostForgeAssembly = typeof(ICartridgeCompiler).Assembly;
            var loadedForgeAssembly = context.LoadFromAssemblyName(assemblyName: hostForgeAssembly.GetName());

            Assert.Same(expected: hostForgeAssembly, actual: loadedForgeAssembly);
        } finally {
            context.Unload();
        }
    }
}
