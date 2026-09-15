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
        public List<WorldExtensionEmbeddingProviderType> EmbeddingProviders { get; } = [];
        public Dictionary<string, Func<bool, (string ContentType, string Body)>> HealthChecks { get; } = new(comparer: StringComparer.OrdinalIgnoreCase);

        public void RegisterAuthentication(WorldAuthenticationProvider provider) => AuthenticationProviders.Add(item: provider);
        public void RegisterEmbedding(WorldExtensionEmbeddingProviderType provider) => EmbeddingProviders.Add(item: provider);
        public void RegisterHealthCheck(string path, Func<bool, (string ContentType, string Body)> handler) => HealthChecks[path] = handler;
        public void RegisterOperation(WorldExtensionProviderType provider) => OperationProviders.Add(item: provider);
        public void RegisterRetirement(WorldSiloRetirementProvider provider) => RetirementProviders.Add(item: provider);
        public void RegisterStorage(WorldSiloStorageProvider provider) => StorageProviders.Add(item: provider);
    }

    private static string FindAgentHarnessAssembly() => FindAssembly(
        assemblyFileName: "Puck.World.AgentHarness.dll",
        projectName: "Puck.World.AgentHarness"
    );
    private static string FindAssembly(string projectName, string assemblyFileName) {
        var baseDir = AppContext.BaseDirectory;
        var directPath = Path.Combine(
            path1: baseDir,
            path2: assemblyFileName
        );

        if (File.Exists(path: directPath)) {
            return directPath;
        }

        var repoRoot = FindRepositoryRoot();
        var releaseBuild = Path.Combine(
            repoRoot,
            "src",
            projectName,
            "bin",
            "Release",
            "net10.0",
            assemblyFileName
        );

        if (File.Exists(path: releaseBuild)) {
            return releaseBuild;
        }

        var debugBuild = Path.Combine(
            repoRoot,
            "src",
            projectName,
            "bin",
            "Debug",
            "net10.0",
            assemblyFileName
        );

        Assert.True(
            condition: File.Exists(path: debugBuild),
            userMessage: $"Could not find {assemblyFileName} at '{releaseBuild}' or '{debugBuild}'."
        );

        return debugBuild;
    }
    private static string FindAzureAssembly() => FindAssembly(
        assemblyFileName: "Puck.World.Azure.dll",
        projectName: "Puck.World.Azure"
    );
    private static string FindHgbForgeAssembly() => FindAssembly(
        assemblyFileName: "Puck.HumbleGamingBrick.Forge.dll",
        projectName: "Puck.HumbleGamingBrick.Forge"
    );
    private static string FindMcpAssembly() => FindAssembly(
        assemblyFileName: "Puck.Mcp.dll",
        projectName: "Puck.Mcp"
    );
    private static string FindRepositoryRoot() {
        var baseDir = AppContext.BaseDirectory;
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
        return directory!.FullName;
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
            }
        );

        Assert.NotEmpty(collection: loaded);
        Assert.NotNull(@object: agentExtension);
        Assert.Equal(
            "Puck.World.AgentHarness",
            agentExtension!.Name
        );

        var registry = new TestWorldAgentExtensionRegistry();

        agentExtension.Register(registry: registry);
        Assert.NotNull(@object: registry.Factory);
    }
    [Fact]
    public void LoadFromAssembly_LoadsAndRegistersAzureExtension() {
        var assemblyPath = FindAzureAssembly();
        var registry = new TestServerExtensionRegistry();
        var loaded = WorldExtensionLoader.LoadFromAssembly(
            assemblyPath: assemblyPath,
            serverRegistry: registry
        );

        Assert.NotEmpty(collection: loaded);
        Assert.Contains(
            collection: loaded,
            filter: extension => string.Equals(
                a: (extension as IWorldExtension)?.Name,
                b: "Azure",
                comparisonType: StringComparison.Ordinal
            )
        );
        Assert.Contains(
            collection: registry.StorageProviders,
            filter: p => (p.Type == "azure.blob")
        );
        Assert.Contains(
            collection: registry.RetirementProviders,
            filter: p => (p.Type == "azure.scheduled-events")
        );
        Assert.Contains(
            collection: registry.AuthenticationProviders,
            filter: p => (p.Type == "azure.api-users")
        );
        Assert.Contains(
            collection: registry.OperationProviders,
            filter: p => (p.Type == "azure.resource")
        );
        Assert.True(condition: registry.HealthChecks.ContainsKey(key: "/livez/azure"));

        var (contentType, body) = registry.HealthChecks["/livez/azure"](true);
        Assert.Equal(
            actual: contentType,
            expected: "application/json"
        );
        Assert.Contains(
            actualString: body,
            expectedSubstring: "Healthy"
        );
    }
    [Fact]
    public void LoadFromAssembly_LoadsAndRegistersExtension() {
        var assemblyPath = FindHgbForgeAssembly();
        var registry = new WorldMachineExtensionRegistry();
        var loaded = WorldMachineExtensionLoader.LoadFromAssembly(
            assemblyPath: assemblyPath,
            registry: registry
        );
        var catalog = registry.Build();

        Assert.NotEmpty(collection: loaded);
        Assert.Contains(
            collection: loaded,
            filter: extension => string.Equals(
                a: extension.Name,
                b: "HumbleGamingBrick",
                comparisonType: StringComparison.Ordinal
            )
        );
        Assert.True(condition: catalog.IsRegistered(engineId: "gaming-brick"));
        Assert.True(condition: catalog.ContentProviders.ContainsKey(key: "gaming-brick"));
        Assert.False(condition: new WorldMachineExtensionRegistry().Build().IsRegistered(engineId: "gaming-brick"));
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
            }
        );

        Assert.NotEmpty(collection: loaded);
        Assert.NotNull(@object: controlExtension);
        Assert.Equal(
            "Puck.Mcp",
            controlExtension!.Name
        );
        Assert.False(condition: System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly: controlExtension.GetType().Assembly)!.IsCollectible);

        var registry = new TestControlExtensionRegistry();

        controlExtension.Register(registry: registry);
        Assert.NotNull(@object: registry.Factory);
    }
    [Fact]
    public void LoadFromDirectory_DiscoversAllFourExtensionFamilies() {
        var hgbAssembly = FindHgbForgeAssembly();
        var azureAssembly = FindAzureAssembly();
        var mcpAssembly = FindMcpAssembly();
        var agentAssembly = FindAgentHarnessAssembly();

        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-all-");

        try {
            var hgbDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "hgb"
            );
            var azureDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "azure"
            );
            var mcpDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "mcp"
            );
            var agentDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "agent"
            );

            Directory.CreateDirectory(path: hgbDir);
            Directory.CreateDirectory(path: azureDir);
            Directory.CreateDirectory(path: mcpDir);
            Directory.CreateDirectory(path: agentDir);

            File.Copy(
                hgbAssembly,
                Path.Combine(
                    path1: hgbDir,
                    path2: "Puck.HumbleGamingBrick.Forge.dll"
                ),
                overwrite: true
            );
            File.Copy(
                azureAssembly,
                Path.Combine(
                    path1: azureDir,
                    path2: "Puck.World.Azure.dll"
                ),
                overwrite: true
            );
            File.Copy(
                mcpAssembly,
                Path.Combine(
                    path1: mcpDir,
                    path2: "Puck.Mcp.dll"
                ),
                overwrite: true
            );
            File.Copy(
                agentAssembly,
                Path.Combine(
                    path1: agentDir,
                    path2: "Puck.World.AgentHarness.dll"
                ),
                overwrite: true
            );

            var serverRegistry = new TestServerExtensionRegistry();
            var discoveredControlExtensions = new List<Puck.Hosting.IControlExtension>();
            var discoveredAgentExtensions = new List<Puck.World.Protocol.IWorldAgentExtension>();
            var discoveredBrickExtensions = new List<IMachineExtension>();

            var loaded = WorldExtensionLoader.LoadFromDirectory(
                directoryPath: tempRoot.FullName,
                serverRegistry: serverRegistry,
                onExtensionLoaded: ext => {
                    if (ext is IMachineExtension be) {
                        discoveredBrickExtensions.Add(item: be);
                    }
                    if (ext is Puck.Hosting.IControlExtension ce) {
                        discoveredControlExtensions.Add(item: ce);
                    }
                    if (ext is Puck.World.Protocol.IWorldAgentExtension ae) {
                        discoveredAgentExtensions.Add(item: ae);
                    }
                }
            );

            Assert.Equal(
                4,
                loaded.Count
            );
            Assert.Single(collection: discoveredBrickExtensions);
            Assert.Single(collection: discoveredControlExtensions);
            Assert.Single(collection: discoveredAgentExtensions);
            Assert.Contains(
                collection: serverRegistry.StorageProviders,
                filter: p => (p.Type == "azure.blob")
            );
            Assert.Equal(
                "HumbleGamingBrick",
                discoveredBrickExtensions[0].Name
            );
            Assert.Equal(
                "Puck.Mcp",
                discoveredControlExtensions[0].Name
            );
            Assert.Equal(
                "Puck.World.AgentHarness",
                discoveredAgentExtensions[0].Name
            );
        } finally {
            try {
                tempRoot.Delete(recursive: true);
            } catch (Exception) {
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
            var hgbDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "hgb"
            );

            Directory.CreateDirectory(path: hgbDir);
            File.Copy(
                hgbAssemblyPath,
                Path.Combine(
                    path1: hgbDir,
                    path2: "Puck.HumbleGamingBrick.Forge.dll"
                ),
                overwrite: true
            );

            var hgbDeps = Path.ChangeExtension(
                extension: ".deps.json",
                path: hgbAssemblyPath
            );

            if (File.Exists(path: hgbDeps)) {
                File.Copy(
                    hgbDeps,
                    Path.Combine(
                        path1: hgbDir,
                        path2: "Puck.HumbleGamingBrick.Forge.deps.json"
                    ),
                    overwrite: true
                );
            }

            // Setup Azure directory
            var azureDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "azure"
            );

            Directory.CreateDirectory(path: azureDir);
            File.Copy(
                azureAssemblyPath,
                Path.Combine(
                    path1: azureDir,
                    path2: "Puck.World.Azure.dll"
                ),
                overwrite: true
            );

            var azureDeps = Path.ChangeExtension(
                extension: ".deps.json",
                path: azureAssemblyPath
            );

            if (File.Exists(path: azureDeps)) {
                File.Copy(
                    azureDeps,
                    Path.Combine(
                        path1: azureDir,
                        path2: "Puck.World.Azure.deps.json"
                    ),
                    overwrite: true
                );
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
                        machineExtensions.Add(item: brickExt);
                    }
                }
            );

            // Proves both Azure and HGB were loaded from the same unified extensions directory
            Assert.NotEmpty(collection: loaded);
            Assert.NotEmpty(collection: machineExtensions);
            Assert.Contains(
                collection: machineExtensions,
                filter: ext => (ext.Name == "HumbleGamingBrick")
            );
            Assert.Contains(
                collection: serverRegistry.StorageProviders,
                filter: p => (p.Type == "azure.blob")
            );
            Assert.Contains(
                collection: serverRegistry.RetirementProviders,
                filter: p => (p.Type == "azure.scheduled-events")
            );
        } finally {
            try {
                tempRoot.Delete(recursive: true);
            } catch (Exception) {
                // Ignore locked memory-mapped files on Windows
            }
        }
    }
    [Fact]
    public void LoadFromDirectory_DiscoversSubdirectoryExtensions() {
        var assemblyPath = FindHgbForgeAssembly();
        var tempRoot = Directory.CreateTempSubdirectory(prefix: "puck-ext-test-");

        try {
            var subDir = Path.Combine(
                path1: tempRoot.FullName,
                path2: "HumbleGamingBrick"
            );

            Directory.CreateDirectory(path: subDir);

            var targetDll = Path.Combine(
                path1: subDir,
                path2: "Puck.HumbleGamingBrick.Forge.dll"
            );

            File.Copy(
                destFileName: targetDll,
                overwrite: true,
                sourceFileName: assemblyPath
            );

            var depsFile = Path.ChangeExtension(
                extension: ".deps.json",
                path: assemblyPath
            );

            if (File.Exists(path: depsFile)) {
                File.Copy(
                    sourceFileName: depsFile,
                    destFileName: Path.Combine(
                        path1: subDir,
                        path2: "Puck.HumbleGamingBrick.Forge.deps.json"
                    ),
                    overwrite: true
                );
            }

            var loaded = WorldMachineExtensionLoader.LoadFromDirectory(
                directoryPath: tempRoot.FullName,
                registry: new WorldMachineExtensionRegistry()
            );

            Assert.NotEmpty(collection: loaded);
            Assert.Contains(
                collection: loaded,
                filter: extension => string.Equals(
                    a: extension.Name,
                    b: "HumbleGamingBrick",
                    comparisonType: StringComparison.Ordinal
                )
            );
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
            var fakeDll = Path.Combine(
                path1: tempRoot.FullName,
                path2: "not-a-real-assembly.dll"
            );

            File.WriteAllText(
                contents: "corrupted assembly data",
                path: fakeDll
            );

            var logMessages = new List<string>();
            var loaded = WorldMachineExtensionLoader.LoadFromDirectory(
                directoryPath: tempRoot.FullName,
                registry: new WorldMachineExtensionRegistry(),
                log: msg => logMessages.Add(item: msg)
            );

            Assert.Empty(collection: loaded);
            Assert.NotEmpty(collection: logMessages);
            Assert.Contains(
                collection: logMessages,
                filter: msg => msg.Contains(
                    comparisonType: StringComparison.Ordinal,
                    value: "Failed to load candidate assembly"
                )
            );
        } finally {
            tempRoot.Delete(recursive: true);
        }
    }
    [Fact]
    public void PuckExtensionLoadContext_DefersSharedHostContractsToDefaultContext() {
        var assemblyPath = FindHgbForgeAssembly();
        var context = new PuckExtensionLoadContext(pluginPath: assemblyPath);

        try {
            var hostAbstractionsAssembly = typeof(IMachineEngine).Assembly;
            var loadedAbstractionsAssembly = context.LoadFromAssemblyName(assemblyName: hostAbstractionsAssembly.GetName());

            // Proves type identity matches the host's AssemblyLoadContext.Default
            Assert.Same(
                actual: loadedAbstractionsAssembly,
                expected: hostAbstractionsAssembly
            );

            Assert.Same(
                expected: hostAbstractionsAssembly,
                actual: typeof(IMachineContentProvider).Assembly
            );

            var hostServerAssembly = typeof(WorldServer).Assembly;
            var loadedServerAssembly = context.LoadFromAssemblyName(assemblyName: hostServerAssembly.GetName());

            Assert.Same(
                actual: loadedServerAssembly,
                expected: hostServerAssembly
            );
        } finally {
            context.Unload();
        }
    }

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
}
