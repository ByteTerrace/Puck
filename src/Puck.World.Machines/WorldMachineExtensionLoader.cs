using Puck.Abstractions.Machines;
using Puck.World.Server;

namespace Puck.World.Machines;

/// <summary>
/// Collects one host's extension registrations before producing an immutable catalog.
/// </summary>
public sealed class WorldMachineExtensionRegistry : IMachineExtensionRegistry {
    private readonly Dictionary<string, IMachineEngine> m_engines = new(comparer: StringComparer.Ordinal);
    private readonly Dictionary<string, IMachineContentProvider> m_contentProviders = new(comparer: StringComparer.Ordinal);

    /// <summary>Captures and validates this composition's registrations. Subsequent registrations cannot change it.</summary>
    /// <returns>The immutable catalog for validation and runtime construction.</returns>
    public WorldMachineCatalog Build() => new(
        m_engines.Values,
        m_contentProviders.Values
    );
    /// <inheritdoc/>
    public void RegisterContentProvider(IMachineContentProvider contentProvider) {
        ArgumentNullException.ThrowIfNull(argument: contentProvider);

        ArgumentException.ThrowIfNullOrWhiteSpace(contentProvider.EngineId);
        if (!m_contentProviders.TryAdd(
            key: contentProvider.EngineId,
            value: contentProvider
        )) {
            throw new ArgumentException(
                message: $"Duplicate content provider for machine engine '{contentProvider.EngineId}'.",
                paramName: nameof(contentProvider)
            );
        }
    }
    /// <inheritdoc/>
    public void RegisterEngine(IMachineEngine engine, IMachineContentProvider? contentProvider = null) {
        ArgumentNullException.ThrowIfNull(argument: engine);

        ArgumentException.ThrowIfNullOrWhiteSpace(engine.Id);
        if (m_engines.ContainsKey(key: engine.Id)) {
            throw new ArgumentException(
                message: $"Duplicate machine engine '{engine.Id}'.",
                paramName: nameof(engine)
            );
        }
        if (contentProvider is not null) {
            if (!string.Equals(
                a: engine.Id,
                b: contentProvider.EngineId,
                comparisonType: StringComparison.Ordinal
            )) {
                throw new ArgumentException(
                    message: $"Content provider '{contentProvider.EngineId}' cannot register with engine '{engine.Id}'.",
                    paramName: nameof(contentProvider)
                );
            }
            RegisterContentProvider(contentProvider: contentProvider);
        }
        m_engines.Add(
            key: engine.Id,
            value: engine
        );
    }
}
/// <summary>
/// Discovers and loads dynamic machine extensions and companion cartridge compilers at runtime,
/// delegating to the unified <see cref="WorldExtensionLoader"/>.
/// </summary>
public static class WorldMachineExtensionLoader {
    /// <summary>
    /// Loads an extension directly from its assembly path.
    /// </summary>
    /// <param name="assemblyPath">The full path to the extension assembly.</param>
    /// <param name="registry">This host's registration destination.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of machine extensions discovered in the assembly.</returns>
    public static IReadOnlyList<IMachineExtension> LoadFromAssembly(string assemblyPath, IMachineExtensionRegistry registry, Action<string>? log = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: assemblyPath);
        ArgumentNullException.ThrowIfNull(registry);

        if (!File.Exists(path: assemblyPath)) {
            log?.Invoke($"Extension assembly not found at '{assemblyPath}'.");
            return [];
        }

        var loaded = new List<IMachineExtension>();

        try {
            WorldExtensionLoader.LoadFromAssembly(
                assemblyPath: assemblyPath,
                serverRegistry: null,
                onExtensionLoaded: ext => {
                    if (ext is IMachineExtension brickExtension) {
                        brickExtension.Initialize(registry: registry);

                        loaded.Add(item: brickExtension);
                        log?.Invoke($"[extensions] Loaded machine extension '{brickExtension.Name}'.");
                    }
                }
            );
        } catch (Exception ex) {
            log?.Invoke($"[extensions] Failed to load candidate assembly at '{assemblyPath}': {ex.Message}");
        }

        return loaded;
    }
    /// <summary>
    /// Scans a directory for extension assemblies, loading and registering any discovered machine extensions.
    /// </summary>
    /// <param name="directoryPath">The root extensions directory path.</param>
    /// <param name="registry">This host's registration destination.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of machine extensions loaded during this call.</returns>
    public static IReadOnlyList<IMachineExtension> LoadFromDirectory(string directoryPath, IMachineExtensionRegistry registry, Action<string>? log = null) {
        ArgumentNullException.ThrowIfNull(registry);
        if (
            string.IsNullOrWhiteSpace(value: directoryPath) ||
            !Directory.Exists(path: directoryPath)
        ) {
            return [];
        }

        var loaded = new List<IMachineExtension>();

        WorldExtensionLoader.LoadFromDirectory(
            directoryPath: directoryPath,
            serverRegistry: null,
            onExtensionLoaded: ext => {
                if (ext is IMachineExtension brickExtension) {
                    brickExtension.Initialize(registry: registry);

                    loaded.Add(item: brickExtension);
                    log?.Invoke($"[extensions] Loaded machine extension '{brickExtension.Name}'.");
                }
            },
            log: log
        );

        return loaded;
    }
}
