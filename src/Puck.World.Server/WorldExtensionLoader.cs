using System.Reflection;
using System.Runtime.Loader;
using Puck.Abstractions;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>
/// Custom <see cref="AssemblyLoadContext"/> that isolates extension dependencies via
/// <see cref="AssemblyDependencyResolver"/> while delegating shared host contract assemblies
/// to <see cref="AssemblyLoadContext.Default"/> to guarantee type identity. Optional dependencies
/// absent from that host resolve from the extension's published dependency graph.
/// </summary>
public sealed class PuckExtensionLoadContext : AssemblyLoadContext {
    private static readonly HashSet<string> SharedHostPrefixes = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "Puck.Abstractions",
        "Puck.World.Server",
        "Puck.World.Machines",
        "Puck.World.Protocol",
        "Puck.World.Schema",
        "Puck.Storage",
        "Puck.Networking",
        "Puck.Attestation",
        "Puck.Commands",
        "Puck.Hosting",
        "Puck.Maths",
        "Puck.Assets",
        "Puck.State",
        "Puck.World.AgentBridge",
        "ModelContextProtocol",
        "Microsoft.Agents",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.Hosting",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Options",
        "System.",
    };

    private readonly AssemblyDependencyResolver m_resolver;

    /// <summary>Initializes a new isolated extension load context.</summary>
    /// <param name="pluginPath">The file path to the extension assembly.</param>
    public PuckExtensionLoadContext(string pluginPath) : this(
        pluginPath,
        isCollectible: true
    ) { }

    internal PuckExtensionLoadContext(string pluginPath, bool isCollectible) : base(
        name: Path.GetFileNameWithoutExtension(path: pluginPath),
        isCollectible: isCollectible
    ) {
        m_resolver = new AssemblyDependencyResolver(componentAssemblyPath: pluginPath);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName) {
        var name = assemblyName.Name;

        if (name is not null) {
            foreach (var prefix in SharedHostPrefixes) {
                if (name.StartsWith(
                    comparisonType: StringComparison.OrdinalIgnoreCase,
                    value: prefix
                )) {
                    // Share contracts available to this host. An optional dependency can match a broad prefix
                    // without being installed in the host (for example ModelContextProtocol in a bare silo).
                    // In that case the extension's published dependency graph must supply it.
                    try { return Default.LoadFromAssemblyName(assemblyName: assemblyName); } catch (FileNotFoundException) { break; }
                }
            }
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
}
/// <summary>
/// Unified dynamic extension loader that discovers, isolates via <see cref="AssemblyLoadContext"/>,
/// and activates extensions (both cloud/silo service extensions and machine extensions) from assemblies
/// or directories.
/// </summary>
public static class WorldExtensionLoader {
    private static readonly HashSet<string> HostAssemblyNames = new(comparer: StringComparer.OrdinalIgnoreCase) {
        "Puck.Abstractions.dll",
        "Puck.World.Server.dll",
        "Puck.World.Protocol.dll",
        "Puck.World.Schema.dll",
        "Puck.World.Silo.dll",
        "Puck.World.dll",
        "Puck.Storage.dll",
        "Puck.Networking.dll",
        "Puck.Attestation.dll",
    };

    private static void DispatchExtension(
        object extension,
        IWorldExtensionRegistry? serverRegistry,
        Action<object>? onExtensionLoaded) {
        if (
            (serverRegistry is not null) &&
            (extension is IWorldExtension worldExtension)
        ) {
            worldExtension.Register(registry: serverRegistry);
        }

        onExtensionLoaded?.Invoke(extension);
    }

    /// <summary>
    /// Loads an extension assembly, discovers extension types, activates them, and registers them.
    /// </summary>
    /// <param name="assemblyPath">The full path to the extension assembly.</param>
    /// <param name="serverRegistry">Optional server extension registry for <see cref="IWorldExtension"/>.</param>
    /// <param name="onExtensionLoaded">Optional callback invoked for each activated extension instance.</param>
    /// <returns>A collection of activated extension instances.</returns>
    public static IReadOnlyList<object> LoadFromAssembly(
        string assemblyPath,
        IWorldExtensionRegistry? serverRegistry = null,
        Action<object>? onExtensionLoaded = null) {
        var fullPath = Path.GetFullPath(path: assemblyPath);

        if (!File.Exists(path: fullPath)) {
            throw new FileNotFoundException(
                fileName: fullPath,
                message: $"Extension assembly not found at '{fullPath}'."
            );
        }

        // This API installs process-lifetime providers and returns no unload owner. A collectible context can
        // finalize before a later hosted-service callback resolves one of its dependencies.
        var loadContext = new PuckExtensionLoadContext(
            isCollectible: false,
            pluginPath: fullPath
        );
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath: fullPath);
        var activatedExtensions = new List<object>();

        // Fast metadata-driven discovery: check [assembly: PuckExtension] attributes
        var extensionAttributes = assembly.GetCustomAttributes<PuckExtensionAttribute>().ToArray();

        if (extensionAttributes.Length > 0) {
            foreach (var attribute in extensionAttributes) {
                if (Activator.CreateInstance(type: attribute.ExtensionType) is { } extension) {
                    activatedExtensions.Add(item: extension);
                    DispatchExtension(
                        extension: extension,
                        onExtensionLoaded: onExtensionLoaded,
                        serverRegistry: serverRegistry
                    );
                }
            }
            return activatedExtensions;
        }

        // Fallback: scan exported types
        foreach (var type in assembly.GetExportedTypes()) {
            if (
                type.IsAbstract ||
                type.IsInterface
            ) {
                continue;
            }

            if (
                typeof(IWorldExtension).IsAssignableFrom(c: type) ||
                typeof(IMachineExtension).IsAssignableFrom(c: type)
            ) {
                if (Activator.CreateInstance(type: type) is { } extension) {
                    activatedExtensions.Add(item: extension);
                    DispatchExtension(
                        extension: extension,
                        onExtensionLoaded: onExtensionLoaded,
                        serverRegistry: serverRegistry
                    );
                }
            }
        }

        return activatedExtensions;
    }
    /// <summary>
    /// Scans a directory for extension assemblies (both flat layout and directory-per-extension),
    /// loading and activating each discovered extension.
    /// </summary>
    /// <param name="directoryPath">The directory to scan.</param>
    /// <param name="serverRegistry">Optional server extension registry.</param>
    /// <param name="onExtensionLoaded">Optional callback for each activated extension.</param>
    /// <param name="log">Optional logger for warnings and diagnostics.</param>
    /// <returns>All activated extension instances.</returns>
    public static IReadOnlyList<object> LoadFromDirectory(
        string directoryPath,
        IWorldExtensionRegistry? serverRegistry = null,
        Action<object>? onExtensionLoaded = null,
        Action<string>? log = null) {
        if (!Directory.Exists(path: directoryPath)) {
            log?.Invoke($"Extension directory '{directoryPath}' does not exist.");
            return [];
        }

        var candidateDlls = new List<string>();

        // Top-level DLLs
        foreach (var file in Directory.EnumerateFiles(
            path: directoryPath,
            searchOption: SearchOption.TopDirectoryOnly,
            searchPattern: "*.dll"
        )) {
            if (!HostAssemblyNames.Contains(item: Path.GetFileName(path: file))) {
                candidateDlls.Add(item: file);
            }
        }

        // Immediate subdirectories (e.g. extensions/azure/Puck.World.Azure.dll, extensions/hgb/Puck.HumbleGamingBrick.Forge.dll)
        foreach (var subDir in Directory.EnumerateDirectories(path: directoryPath)) {
            var subDirName = Path.GetFileName(path: subDir);
            var matchingDll = Path.Combine(
                path1: subDir,
                path2: $"{subDirName}.dll"
            );

            if (File.Exists(path: matchingDll)) {
                candidateDlls.Add(item: matchingDll);
            } else {
                foreach (var file in Directory.EnumerateFiles(
                    path: subDir,
                    searchOption: SearchOption.TopDirectoryOnly,
                    searchPattern: "*.dll"
                )) {
                    if (!HostAssemblyNames.Contains(item: Path.GetFileName(path: file))) {
                        candidateDlls.Add(item: file);
                    }
                }
            }
        }

        var results = new List<object>();

        foreach (var dllPath in candidateDlls) {
            try {
                var loaded = LoadFromAssembly(
                    assemblyPath: dllPath,
                    onExtensionLoaded: onExtensionLoaded,
                    serverRegistry: serverRegistry
                );

                results.AddRange(collection: loaded);
            } catch (Exception ex) {
                log?.Invoke($"Failed to load candidate assembly at '{dllPath}': {ex.Message}");
            }
        }

        return results;
    }
}
