using System.Reflection;
using System.Runtime.Loader;
using Puck.Abstractions;
using Puck.Abstractions.Machines;

namespace Puck.World.Server;

/// <summary>
/// Custom <see cref="AssemblyLoadContext"/> that isolates extension dependencies via
/// <see cref="AssemblyDependencyResolver"/> while delegating shared host contract assemblies
/// to <see cref="AssemblyLoadContext.Default"/> to guarantee type identity.
/// </summary>
public sealed class PuckExtensionLoadContext : AssemblyLoadContext {
    private static readonly HashSet<string> SharedHostPrefixes = new(StringComparer.OrdinalIgnoreCase) {
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
    public PuckExtensionLoadContext(string pluginPath) : base(name: Path.GetFileNameWithoutExtension(pluginPath), isCollectible: true) {
        m_resolver = new AssemblyDependencyResolver(pluginPath);
    }

    /// <inheritdoc/>
    protected override Assembly? Load(AssemblyName assemblyName) {
        var name = assemblyName.Name;

        if (name is not null) {
            foreach (var prefix in SharedHostPrefixes) {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) {
                    return null; // Defer to AssemblyLoadContext.Default to ensure shared type identity
                }
            }
        }

        var assemblyPath = m_resolver.ResolveAssemblyToPath(assemblyName);

        if (assemblyPath is not null) {
            return LoadFromAssemblyPath(assemblyPath);
        }

        return null;
    }

    /// <inheritdoc/>
    protected override nint LoadUnmanagedDll(string unmanagedDllName) {
        var libraryPath = m_resolver.ResolveUnmanagedDllToPath(unmanagedDllName);

        if (libraryPath is not null) {
            return LoadUnmanagedDllFromPath(libraryPath);
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
    private static readonly HashSet<string> HostAssemblyNames = new(StringComparer.OrdinalIgnoreCase) {
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
        var fullPath = Path.GetFullPath(assemblyPath);

        if (!File.Exists(fullPath)) {
            throw new FileNotFoundException($"Extension assembly not found at '{fullPath}'.", fullPath);
        }

        var loadContext = new PuckExtensionLoadContext(pluginPath: fullPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath: fullPath);
        var activatedExtensions = new List<object>();

        // Fast metadata-driven discovery: check [assembly: PuckExtension] attributes
        var extensionAttributes = assembly.GetCustomAttributes<PuckExtensionAttribute>().ToArray();

        if (extensionAttributes.Length > 0) {
            foreach (var attribute in extensionAttributes) {
                if (Activator.CreateInstance(attribute.ExtensionType) is { } extension) {
                    activatedExtensions.Add(extension);
                    DispatchExtension(extension, serverRegistry, onExtensionLoaded);
                }
            }
            return activatedExtensions;
        }

        // Fallback: scan exported types
        foreach (var type in assembly.GetExportedTypes()) {
            if (type.IsAbstract || type.IsInterface) {
                continue;
            }

            if (typeof(IWorldExtension).IsAssignableFrom(type) ||
                typeof(IMachineExtension).IsAssignableFrom(type)) {
                if (Activator.CreateInstance(type) is { } extension) {
                    activatedExtensions.Add(extension);
                    DispatchExtension(extension, serverRegistry, onExtensionLoaded);
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
        if (!Directory.Exists(directoryPath)) {
            log?.Invoke($"Extension directory '{directoryPath}' does not exist.");
            return [];
        }

        var candidateDlls = new List<string>();

        // Top-level DLLs
        foreach (var file in Directory.EnumerateFiles(directoryPath, "*.dll", SearchOption.TopDirectoryOnly)) {
            if (!HostAssemblyNames.Contains(Path.GetFileName(file))) {
                candidateDlls.Add(file);
            }
        }

        // Immediate subdirectories (e.g. extensions/azure/Puck.World.Azure.dll, extensions/hgb/Puck.HumbleGamingBrick.Forge.dll)
        foreach (var subDir in Directory.EnumerateDirectories(directoryPath)) {
            var subDirName = Path.GetFileName(subDir);
            var matchingDll = Path.Combine(subDir, $"{subDirName}.dll");

            if (File.Exists(matchingDll)) {
                candidateDlls.Add(matchingDll);
            } else {
                foreach (var file in Directory.EnumerateFiles(subDir, "*.dll", SearchOption.TopDirectoryOnly)) {
                    if (!HostAssemblyNames.Contains(Path.GetFileName(file))) {
                        candidateDlls.Add(file);
                    }
                }
            }
        }

        var results = new List<object>();

        foreach (var dllPath in candidateDlls) {
            try {
                var loaded = LoadFromAssembly(
                    assemblyPath: dllPath,
                    serverRegistry: serverRegistry,
                    onExtensionLoaded: onExtensionLoaded);

                results.AddRange(loaded);
            } catch (Exception ex) {
                log?.Invoke($"Failed to load candidate assembly at '{dllPath}': {ex.Message}");
            }
        }

        return results;
    }

    private static void DispatchExtension(
        object extension,
        IWorldExtensionRegistry? serverRegistry,
        Action<object>? onExtensionLoaded) {
        if (serverRegistry is not null && extension is IWorldExtension worldExtension) {
            worldExtension.Register(serverRegistry);
        }

        onExtensionLoaded?.Invoke(extension);
    }
}
