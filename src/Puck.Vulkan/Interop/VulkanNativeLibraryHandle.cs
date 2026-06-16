using System.Runtime.InteropServices;

namespace Puck.Vulkan.Interop;

/// <summary>
/// A <see cref="SafeHandle"/> wrapping the loaded native Vulkan loader, which frees the library when released.
/// </summary>
public sealed class VulkanNativeLibraryHandle : SafeHandle {
    /// <summary>Loads the native Vulkan loader from the given path or name.</summary>
    /// <param name="libraryPath">The path or name of the loader to load.</param>
    /// <returns>A handle owning the loaded library.</returns>
    /// <exception cref="ArgumentException"><paramref name="libraryPath"/> is <see langword="null"/>, empty, or white space.</exception>
    public static VulkanNativeLibraryHandle Load(string libraryPath) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: libraryPath);

        return new VulkanNativeLibraryHandle(handle: NativeLibrary.Load(libraryPath: libraryPath));
    }

    /// <inheritdoc/>
    public override bool IsInvalid => (nint.Zero == handle);

    private VulkanNativeLibraryHandle() : base(
        invalidHandleValue: nint.Zero,
        ownsHandle: true
    ) { }
    private VulkanNativeLibraryHandle(nint handle) : this() {
        SetHandle(handle: handle);
    }

    /// <inheritdoc/>
    protected override bool ReleaseHandle() {
        NativeLibrary.Free(handle: handle);

        return true;
    }

    /// <summary>Resolves an exported function from the loaded library.</summary>
    /// <param name="functionName">The name of the exported function to resolve.</param>
    /// <returns>The address of the exported function.</returns>
    /// <exception cref="ArgumentException"><paramref name="functionName"/> is <see langword="null"/>, empty, or white space.</exception>
    public nint GetExport(string functionName) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: functionName);

        return NativeLibrary.GetExport(
            handle: handle,
            name: functionName
        );
    }
}
