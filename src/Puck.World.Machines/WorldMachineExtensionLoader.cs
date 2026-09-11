using System.Reflection;
using System.Runtime.Loader;
using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;

namespace Puck.World.Machines;

/// <summary>
/// An isolated <see cref="AssemblyLoadContext"/> that resolves private extension dependencies
/// while delegating shared host contracts to <see cref="AssemblyLoadContext.Default"/>.
/// </summary>
public sealed class PuckExtensionLoadContext : AssemblyLoadContext {
    private readonly AssemblyDependencyResolver m_resolver;

    /// <summary>
    /// Initializes a new instance of the <see cref="PuckExtensionLoadContext"/> class.
    /// </summary>
    /// <param name="pluginPath">The path to the extension's main assembly.</param>
    public PuckExtensionLoadContext(string pluginPath) : base(name: Path.GetFileNameWithoutExtension(path: pluginPath), isCollectible: true) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: pluginPath);

        m_resolver = new AssemblyDependencyResolver(componentAssemblyPath: pluginPath);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName) {
        if (IsSharedHostAssembly(name: assemblyName.Name)) {
            return null;
        }

        var assemblyPath = m_resolver.ResolveAssemblyToPath(assemblyName: assemblyName);

        if (assemblyPath is not null) {
            return LoadFromAssemblyPath(assemblyPath: assemblyPath);
        }

        return null;
    }

    /// <inheritdoc/>
    protected override nint LoadUnmanagedDll(string unmanagedDllName) {
        var libraryPath = m_resolver.ResolveUnmanagedDllToPath(unmanagedDllName: unmanagedDllName);

        if (libraryPath is not null) {
            return LoadUnmanagedDllFromPath(unmanagedDllPath: libraryPath);
        }

        return nint.Zero;
    }

    private static bool IsSharedHostAssembly(string? name) {
        if (string.IsNullOrEmpty(value: name)) {
            return false;
        }

        return name.StartsWith(value: "System.", comparisonType: StringComparison.OrdinalIgnoreCase)
            || name.StartsWith(value: "Microsoft.", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.Abstractions", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.GamingBricks.Forge", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.World.Server", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.World.Machines", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.Maths", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.Assets", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.Audio", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.Commands", comparisonType: StringComparison.OrdinalIgnoreCase)
            || string.Equals(a: name, b: "Puck.State", comparisonType: StringComparison.OrdinalIgnoreCase);
    }
}

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
/// Discovers and loads dynamic machine extensions and companion cartridge compilers at runtime.
/// </summary>
public static class WorldMachineExtensionLoader {
    private static readonly List<PuckExtensionLoadContext> s_contexts = [];
    private static readonly List<IGamingBrickExtension> s_extensions = [];
    private static readonly Lock s_gate = new();

    /// <summary>
    /// Gets all dynamically loaded extensions.
    /// </summary>
    public static IReadOnlyList<IGamingBrickExtension> LoadedExtensions {
        get {
            lock (s_gate) {
                return s_extensions.ToArray();
            }
        }
    }

    /// <summary>
    /// Scans a directory for extension assemblies (both flat and subdirectory-isolated),
    /// loading and registering any discovered extensions.
    /// </summary>
    /// <param name="directoryPath">The root extensions directory path.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of extensions loaded during this call.</returns>
    public static IReadOnlyList<IGamingBrickExtension> LoadFromDirectory(string directoryPath, Action<string>? log = null) {
        if (string.IsNullOrWhiteSpace(value: directoryPath) || !Directory.Exists(path: directoryPath)) {
            return [];
        }

        var loaded = new List<IGamingBrickExtension>();

        // 1. Scan subdirectories (directory-per-extension pattern)
        foreach (var subDir in Directory.GetDirectories(path: directoryPath)) {
            var dirName = Path.GetFileName(path: subDir);
            var expectedDll = Path.Combine(subDir, $"{dirName}.dll");

            if (File.Exists(path: expectedDll)) {
                LoadCandidate(assemblyPath: expectedDll, loaded: loaded, log: log);
            } else {
                foreach (var dll in Directory.GetFiles(path: subDir, searchPattern: "*.dll")) {
                    LoadCandidate(assemblyPath: dll, loaded: loaded, log: log);
                }
            }
        }

        // 2. Scan flat files directly in the directory
        foreach (var dll in Directory.GetFiles(path: directoryPath, searchPattern: "*.dll")) {
            LoadCandidate(assemblyPath: dll, loaded: loaded, log: log);
        }

        return loaded;
    }

    /// <summary>
    /// Loads an extension directly from its assembly path.
    /// </summary>
    /// <param name="assemblyPath">The full path to the extension assembly.</param>
    /// <param name="log">An optional diagnostic logging callback.</param>
    /// <returns>The list of extensions discovered in the assembly.</returns>
    public static IReadOnlyList<IGamingBrickExtension> LoadFromAssembly(string assemblyPath, Action<string>? log = null) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: assemblyPath);

        if (!File.Exists(path: assemblyPath)) {
            log?.Invoke($"Extension assembly not found at '{assemblyPath}'.");

            return [];
        }

        var loaded = new List<IGamingBrickExtension>();
        LoadCandidate(assemblyPath: assemblyPath, loaded: loaded, log: log);

        return loaded;
    }

    private static void LoadCandidate(string assemblyPath, List<IGamingBrickExtension> loaded, Action<string>? log) {
        try {
            var context = new PuckExtensionLoadContext(pluginPath: assemblyPath);
            var assembly = context.LoadFromAssemblyPath(assemblyPath: assemblyPath);
            var registry = new WorldMachineExtensionRegistry();
            var foundAny = false;

            // Attribute discovery (fast path)
            var attributes = assembly.GetCustomAttributes<PuckExtensionAttribute>();

            foreach (var attribute in attributes) {
                if (typeof(IGamingBrickExtension).IsAssignableFrom(c: attribute.ExtensionType)
                    && Activator.CreateInstance(type: attribute.ExtensionType) is IGamingBrickExtension extension) {
                    extension.Initialize(registry: registry);

                    lock (s_gate) {
                        s_contexts.Add(item: context);
                        s_extensions.Add(item: extension);
                    }

                    loaded.Add(item: extension);
                    foundAny = true;
                    log?.Invoke($"[extensions] Loaded '{extension.Name}' from '{assemblyPath}' via PuckExtensionAttribute.");
                }
            }

            // Fallback type scanning if no attribute matched
            if (!foundAny) {
                foreach (var type in assembly.GetExportedTypes()) {
                    if (type.IsAbstract || type.IsInterface) {
                        continue;
                    }

                    if (typeof(IGamingBrickExtension).IsAssignableFrom(c: type)
                        && (type.GetConstructor(types: Type.EmptyTypes) is not null)
                        && Activator.CreateInstance(type: type) is IGamingBrickExtension extension) {
                        extension.Initialize(registry: registry);

                        lock (s_gate) {
                            s_contexts.Add(item: context);
                            s_extensions.Add(item: extension);
                        }

                        loaded.Add(item: extension);
                        foundAny = true;
                        log?.Invoke($"[extensions] Loaded '{extension.Name}' from '{assemblyPath}' via exported type scanning.");
                    }
                }
            }

            if (!foundAny) {
                // If the assembly carries neither attributes nor extension types, unload context if collectible
                context.Unload();
            }
        } catch (Exception ex) {
            log?.Invoke($"[extensions] Failed to load candidate assembly at '{assemblyPath}': {ex.Message}");
        }
    }
}
