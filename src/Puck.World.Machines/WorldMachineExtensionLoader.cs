using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;
using Puck.World.Server;

namespace Puck.World.Machines;

/// <summary>
/// Concrete registry that forwards extension registrations directly into <see cref="WorldScreenMachineEngines"/>.
/// </summary>
public sealed class WorldMachineExtensionRegistry : IGamingBrickExtensionRegistry {
    /// <inheritdoc/>
    public void RegisterEngine(IScreenMachineEngine engine, ICartridgeCompiler? compiler = null) {
        ArgumentNullException.ThrowIfNull(argument: engine);

        WorldScreenMachineEngines.Register(engine: engine, compiler: compiler);
    }

    /// <inheritdoc/>
    public void RegisterCompiler(ICartridgeCompiler compiler) {
        ArgumentNullException.ThrowIfNull(argument: compiler);

        WorldScreenMachineEngines.RegisterCompiler(compiler: compiler);
    }
}

/// <summary>
/// Discovers and loads dynamic machine extensions and companion cartridge compilers at runtime,
/// delegating to the unified <see cref="WorldExtensionLoader"/>.
/// </summary>
public static class WorldMachineExtensionLoader {
    private static readonly List<IGamingBrickExtension> s_extensions = [];
    private static readonly Lock s_gate = new();

    /// <summary>
    /// Gets all dynamically loaded machine extensions.
    /// </summary>
    public static IReadOnlyList<IGamingBrickExtension> LoadedExtensions {
        get {
            lock (s_gate) {
                return s_extensions.ToArray();
            }
        }
    }

    /// <summary>
    /// Scans a directory for extension assemblies, loading and registering any discovered machine extensions.
    /// </summary>
    /// <param name="directoryPath">The root extensions directory path.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of machine extensions loaded during this call.</returns>
    public static IReadOnlyList<IGamingBrickExtension> LoadFromDirectory(string directoryPath, Action<string>? log = null) {
        if (string.IsNullOrWhiteSpace(value: directoryPath) || !Directory.Exists(path: directoryPath)) {
            return [];
        }

        var loaded = new List<IGamingBrickExtension>();
        var registry = new WorldMachineExtensionRegistry();

        WorldExtensionLoader.LoadFromDirectory(
            directoryPath: directoryPath,
            serverRegistry: null,
            onExtensionLoaded: ext => {
                if (ext is IGamingBrickExtension brickExtension) {
                    brickExtension.Initialize(registry: registry);

                    lock (s_gate) {
                        s_extensions.Add(brickExtension);
                    }

                    loaded.Add(brickExtension);
                    log?.Invoke($"[extensions] Loaded machine extension '{brickExtension.Name}'.");
                }
            },
            log: log);

        return loaded;
    }

    /// <summary>
    /// Loads an extension directly from its assembly path.
    /// </summary>
    /// <param name="assemblyPath">The full path to the extension assembly.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of machine extensions discovered in the assembly.</returns>
    public static IReadOnlyList<IGamingBrickExtension> LoadFromAssembly(string assemblyPath, Action<string>? log = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: assemblyPath);

        if (!File.Exists(path: assemblyPath)) {
            log?.Invoke($"Extension assembly not found at '{assemblyPath}'.");
            return [];
        }

        var loaded = new List<IGamingBrickExtension>();
        var registry = new WorldMachineExtensionRegistry();

        try {
            WorldExtensionLoader.LoadFromAssembly(
                assemblyPath: assemblyPath,
                serverRegistry: null,
                onExtensionLoaded: ext => {
                    if (ext is IGamingBrickExtension brickExtension) {
                        brickExtension.Initialize(registry: registry);

                        lock (s_gate) {
                            s_extensions.Add(brickExtension);
                        }

                        loaded.Add(brickExtension);
                        log?.Invoke($"[extensions] Loaded machine extension '{brickExtension.Name}'.");
                    }
                });
        } catch (Exception ex) {
            log?.Invoke($"[extensions] Failed to load candidate assembly at '{assemblyPath}': {ex.Message}");
        }

        return loaded;
    }
}
