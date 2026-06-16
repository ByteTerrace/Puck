using System.Runtime.InteropServices;

namespace Puck.Vulkan.Interop;

/// <summary>
/// Resolves and lazily loads the platform Vulkan loader, and resolves exported entry points from it.
/// </summary>
public static class VulkanNativeLibrary {
    private static string? OverriddenLibraryPath;

    /// <summary>
    /// Gets or sets an explicit path or name for the Vulkan loader, overriding the per-platform default.
    /// Must be assigned before the first Vulkan call (the point at which the loader is loaded).
    /// </summary>
    /// <exception cref="InvalidOperationException">The loader has already been loaded.</exception>
    public static string? LibraryPathOverride {
        get => OverriddenLibraryPath;
        set {
            if (Handle.IsValueCreated) {
                throw new InvalidOperationException(message: "The Vulkan loader has already been loaded; assign VulkanNativeLibrary.LibraryPathOverride before the first Vulkan call.");
            }

            OverriddenLibraryPath = value;
        }
    }

    /// <summary>The lazily initialized handle to the loaded native Vulkan loader.</summary>
    public static readonly Lazy<VulkanNativeLibraryHandle> Handle = new(
        mode: LazyThreadSafetyMode.ExecutionAndPublication,
        valueFactory: () => VulkanNativeLibraryHandle.Load(libraryPath: ResolveLibraryPath())
    );

    /// <summary>Determines the path or name of the Vulkan loader to load: the override if set, otherwise the per-platform default.</summary>
    /// <returns>The loader path or name to pass to the native library loader.</returns>
    /// <exception cref="PlatformNotSupportedException">No default loader name is known for the current platform and no override is set.</exception>
    public static string ResolveLibraryPath() {
        if (!string.IsNullOrWhiteSpace(value: OverriddenLibraryPath)) {
            return OverriddenLibraryPath;
        }

        if (OperatingSystem.IsWindows()) {
            return "vulkan-1";
        }

        if (OperatingSystem.IsAndroid()) {
            return "libvulkan.so";
        }

        if (
            OperatingSystem.IsMacOS() ||
            OperatingSystem.IsMacCatalyst() ||
            OperatingSystem.IsIOS()
        ) {
            return "libvulkan.1.dylib";
        }

        if (
            OperatingSystem.IsLinux() ||
            OperatingSystem.IsFreeBSD()
        ) {
            return "libvulkan.so.1";
        }

        throw new PlatformNotSupportedException(message: $"No default Vulkan loader name is known for this platform ('{RuntimeInformation.OSDescription}'). Assign VulkanNativeLibrary.LibraryPathOverride from trusted bootstrap code. On Nintendo Switch the licensed Puck Switch SDK backend supplies the loader path.");
    }
    /// <summary>Resolves an exported function from the loaded Vulkan loader, loading the loader if necessary.</summary>
    /// <param name="functionName">The name of the exported function to resolve.</param>
    /// <returns>The address of the exported function.</returns>
    /// <exception cref="ArgumentException"><paramref name="functionName"/> is <see langword="null"/>, empty, or white space.</exception>
    public static nint GetExport(string functionName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: functionName);

        return Handle.Value.GetExport(functionName: functionName);
    }
}
