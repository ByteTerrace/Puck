using Puck.Abstractions.Machines;
using Puck.Abstractions.Machines;
using Puck.World.Machines;
using Puck.World.Server;
using Xunit;

namespace Puck.World.Tests;

/// <summary>
/// CONTRACT UNDER TEST: <see cref="WorldExtensionLoader"/> and <see cref="PuckExtensionLoadContext"/>
/// discover and dynamically load extensions (gaming brick emulators, cloud storage, retirement observers,
/// authenticators, operation providers) without compile-time host coupling, delegating shared contracts
/// to the default load context and isolating private extension dependencies.
/// </summary>
public sealed class DynamicExtensionLoaderLawTests {
    private sealed class TestServerExtensionRegistry : IWorldExtensionRegistry {
        public List<WorldSiloStorageProvider> StorageProviders { get; } = [];
        public List<WorldSiloRetirementProvider> RetirementProviders { get; } = [];
        public List<WorldAuthenticationProvider> AuthenticationProviders { get; } = [];
        public List<WorldExtensionProviderType> OperationProviders { get; } = [];
        public Dictionary<string, Func<bool, (string ContentType, string Body)>> HealthChecks { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void RegisterStorage(WorldSiloStorageProvider provider) => StorageProviders.Add(provider);
        public void RegisterRetirement(WorldSiloRetirementProvider provider) => RetirementProviders.Add(provider);
        public void RegisterAuthentication(WorldAuthenticationProvider provider) => AuthenticationProviders.Add(provider);
        public void RegisterOperation(WorldExtensionProviderType provider) => OperationProviders.Add(provider);
        public void RegisterHealthCheck(string path, Func<bool, (string ContentType, string Body)> handler) => HealthChecks[path] = handler;
    }

    private static string FindRepositoryRoot() {
        var baseDir = AppContext.BaseDirectory;
        var directory = new DirectoryInfo(baseDir);

        while ((directory is not null) && !File.Exists(Path.Combine(directory.FullName, "Puck.slnx"))) {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }

    private static string FindAssembly(string projectName, string assemblyFileName) {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(baseDir, assemblyFileName);

        if (File.Exists(directPath)) {
            return directPath;
        }

        var repoRoot = FindRepositoryRoot();
        var releaseBuild = Path.Combine(repoRoot, "src", projectName, "bin", "Release", "net10.0", assemblyFileName);

        if (File.Exists(releaseBuild)) {
            return releaseBuild;
        }

        var debugBuild = Path.Combine(repoRoot, "src", projectName, "bin", "Debug", "net10.0", assemblyFileName);
        Assert.True(File.Exists(debugBuild), $"Could not find {assemblyFileName} at '{releaseBuild}' or '{debugBuild}'.");

        return debugBuild;
    }

    private static string FindHgbForgeAssembly() => FindAssembly("Puck.HumbleGamingBrick.Forge", "Puck.HumbleGamingBrick.Forge.dll");
    private static string FindAzureAssembly() => FindAssembly("Puck.World.Azure", "Puck.World.Azure.dll");
    private static string FindMcpAssembly() => FindAssembly("Puck.Mcp", "Puck.Mcp.dll");
    private static string FindAgentHarnessAssembly() => FindAssembly("Puck.World.AgentHarness", "Puck.World.AgentHarness.dll");

    private sealed class TestControlExtensionRegistry : Puck.Hosting.IControlExtensionRegistry {
        public Func<IServiceProvider, Puck.Hosting.IControlSessionHost, string, Puck.Abstractions.IPuckHostedService>? Factory { get; private set; }
        public void RegisterHostedControl(Func<IServiceProvider, Puck.Hosting.IControlSessionHost, string, Puck.Abstractions.IPuckHostedService> factory) {
            Factory = factory;
        }
    }

    private sealed class TestWorldAgentExtensionRegistry : Puck.World.Protocol.IWorldAgentExtensionRegistry {
        public Func<IServiceProvider, Puck.Abstractions.IPuckHostedService>? Factory { get; private set; }
        public void RegisterAgentRunner(Func<IServiceProvider, Puck.Abstractions.IPuckHostedService> factory) {
            Factory = factory;
        }
    }

    [Fact]
    public void LoadFromAssembly_LoadsAndRegistersMcpControlExtension() {
        var assemblyPath = FindMcpAssembly();
        Puck.Hosting.IControlExtension? controlExtension = null;
        var loaded = WorldExtensionLoader.LoadFromAssembly(
            assemblyPath: assemblyPath,
            onExtensionLoaded: ext => {
                if (ext is Puck.Hosting.IControlExtension ce) {
                    controlExtension = ce;
                }
            });

        Assert.NotEmpty(loaded);
        Assert.NotNull(controlExtension);
        Assert.Equal("Puck.Mcp", controlExtension!.Name);

        var registry = new TestControlExtensionRegistry();
        controlExtension.Register(registry);
        Assert.NotNull(registry.Factory);
    }

    [Fact]
    public void LoadFromAssembly_LoadsAndRegistersAgentHarnessExtension() {
        var assemblyPath = FindAgentHarnessAssembly();
        Puck.World.Protocol.IWorldAgentExtension? agentExtension = null;
        var loaded = WorldExtensionLoader.LoadFromAssembly(
            assemblyPath: assemblyPath,
            onExtensionLoaded: ext => {
                if (ext is Puck.World.Protocol.IWorldAgentExtension ae) {
                    agentExtension = ae;
                }
            });

        Assert.NotEmpty(loaded);
        Assert.NotNull(agentExtension);
        Assert.Equal("Puck.World.AgentHarness", agentExtension!.Name);

        var registry = new TestWorldAgentExtensionRegistry();
        agentExtension.Register(registry);
        Assert.NotNull(registry.Factory);
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
    public void LoadFromAssembly_LoadsAndRegistersAzureExtension() {
        var assemblyPath = FindAzureAssembly();
        var registry = new TestServerExtensionRegistry();
        var loaded = WorldExtensionLoader.LoadFromAssembly(assemblyPath: assemblyPath, serverRegistry: registry);

        Assert.NotEmpty(loaded);
        Assert.Contains(loaded, extension => string.Equals((extension as IWorldExtension)?.Name, "Azure", StringComparison.Ordinal));
        Assert.Contains(registry.StorageProviders, p => p.Type == "azure.blob");
        Assert.Contains(registry.RetirementProviders, p => p.Type == "azure.scheduled-events");
        Assert.Contains(registry.AuthenticationProviders, p => p.Type == "azure.api-users");
        Assert.Contains(registry.OperationProviders, p => p.Type == "azure.resource");
        Assert.True(registry.HealthChecks.ContainsKey("/livez/azure"));

        var (contentType, body) = registry.HealthChecks["/livez/azure"](true);
        Assert.Equal("application/json", contentType);
        Assert.Contains("Healthy", body);
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
    public void LoadFromDirectory_DiscoversBothAzureAndGamingBrickExtensions() {
        var hgbAssemblyPath = FindHgbForgeAssembly();
        var azureAssemblyPath = FindAzureAssembly();
        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-unified-");

        try {
            // Setup HGB directory
            var hgbDir = Path.Combine(tempRoot.FullName, "hgb");
            Directory.CreateDirectory(hgbDir);
            File.Copy(hgbAssemblyPath, Path.Combine(hgbDir, "Puck.HumbleGamingBrick.Forge.dll"), overwrite: true);

            var hgbDeps = Path.ChangeExtension(hgbAssemblyPath, ".deps.json");
            if (File.Exists(hgbDeps)) {
                File.Copy(hgbDeps, Path.Combine(hgbDir, "Puck.HumbleGamingBrick.Forge.deps.json"), overwrite: true);
            }

            // Setup Azure directory
            var azureDir = Path.Combine(tempRoot.FullName, "azure");
            Directory.CreateDirectory(azureDir);
            File.Copy(azureAssemblyPath, Path.Combine(azureDir, "Puck.World.Azure.dll"), overwrite: true);

            var azureDeps = Path.ChangeExtension(azureAssemblyPath, ".deps.json");
            if (File.Exists(azureDeps)) {
                File.Copy(azureDeps, Path.Combine(azureDir, "Puck.World.Azure.deps.json"), overwrite: true);
            }

            var serverRegistry = new TestServerExtensionRegistry();
            var machineExtensions = new List<IMachineExtension>();
            var machineRegistry = new WorldMachineExtensionRegistry();

            var loaded = WorldExtensionLoader.LoadFromDirectory(
                directoryPath: tempRoot.FullName,
                serverRegistry: serverRegistry,
                onExtensionLoaded: ext => {
                    if (ext is IMachineExtension brickExt) {
                        brickExt.Initialize(registry: machineRegistry);
                        machineExtensions.Add(brickExt);
                    }
                });

            // Proves both Azure and HGB were loaded from the same unified extensions directory
            Assert.NotEmpty(loaded);
            Assert.NotEmpty(machineExtensions);
            Assert.Contains(machineExtensions, ext => ext.Name == "HumbleGamingBrick");
            Assert.Contains(serverRegistry.StorageProviders, p => p.Type == "azure.blob");
            Assert.Contains(serverRegistry.RetirementProviders, p => p.Type == "azure.scheduled-events");
        } finally {
            try {
                tempRoot.Delete(recursive: true);
            } catch (Exception) {
                // Ignore locked memory-mapped files on Windows
            }
        }
    }

    [Fact]
    public void LoadFromDirectory_DiscoversAllFourExtensionFamilies() {
        var hgbAssembly = FindHgbForgeAssembly();
        var azureAssembly = FindAzureAssembly();
        var mcpAssembly = FindMcpAssembly();
        var agentAssembly = FindAgentHarnessAssembly();

        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-all-");

        try {
            var hgbDir = Path.Combine(tempRoot.FullName, "hgb");
            var azureDir = Path.Combine(tempRoot.FullName, "azure");
            var mcpDir = Path.Combine(tempRoot.FullName, "mcp");
            var agentDir = Path.Combine(tempRoot.FullName, "agent");

            Directory.CreateDirectory(hgbDir);
            Directory.CreateDirectory(azureDir);
            Directory.CreateDirectory(mcpDir);
            Directory.CreateDirectory(agentDir);

            File.Copy(hgbAssembly, Path.Combine(hgbDir, "Puck.HumbleGamingBrick.Forge.dll"), overwrite: true);
            File.Copy(azureAssembly, Path.Combine(azureDir, "Puck.World.Azure.dll"), overwrite: true);
            File.Copy(mcpAssembly, Path.Combine(mcpDir, "Puck.Mcp.dll"), overwrite: true);
            File.Copy(agentAssembly, Path.Combine(agentDir, "Puck.World.AgentHarness.dll"), overwrite: true);

            var serverRegistry = new TestServerExtensionRegistry();
            var discoveredControlExtensions = new List<Puck.Hosting.IControlExtension>();
            var discoveredAgentExtensions = new List<Puck.World.Protocol.IWorldAgentExtension>();
            var discoveredBrickExtensions = new List<IMachineExtension>();

            var loaded = WorldExtensionLoader.LoadFromDirectory(
                directoryPath: tempRoot.FullName,
                serverRegistry: serverRegistry,
                onExtensionLoaded: ext => {
                    if (ext is IMachineExtension be) {
                        discoveredBrickExtensions.Add(be);
                    }
                    if (ext is Puck.Hosting.IControlExtension ce) {
                        discoveredControlExtensions.Add(ce);
                    }
                    if (ext is Puck.World.Protocol.IWorldAgentExtension ae) {
                        discoveredAgentExtensions.Add(ae);
                    }
                });

            Assert.Equal(4, loaded.Count);
            Assert.Single(discoveredBrickExtensions);
            Assert.Single(discoveredControlExtensions);
            Assert.Single(discoveredAgentExtensions);
            Assert.Contains(serverRegistry.StorageProviders, p => p.Type == "azure.blob");
            Assert.Equal("HumbleGamingBrick", discoveredBrickExtensions[0].Name);
            Assert.Equal("Puck.Mcp", discoveredControlExtensions[0].Name);
            Assert.Equal("Puck.World.AgentHarness", discoveredAgentExtensions[0].Name);
        } finally {
            try {
                tempRoot.Delete(recursive: true);
            } catch (Exception) {
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

            var hostServerAssembly = typeof(WorldServer).Assembly;
            var loadedServerAssembly = context.LoadFromAssemblyName(assemblyName: hostServerAssembly.GetName());

            Assert.Same(expected: hostServerAssembly, actual: loadedServerAssembly);
        } finally {
            context.Unload();
        }
    }
}
