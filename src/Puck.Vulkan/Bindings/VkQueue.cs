namespace Puck.Vulkan.Bindings;

/// <summary>
/// A device queue: its native <c>VkQueue</c> handle paired with the index of the queue family it belongs to.
/// </summary>
public readonly record struct VkQueue {
    /// <summary>Gets the index of the queue family this queue belongs to.</summary>
    public uint FamilyIndex { get; }
    /// <summary>Gets the native <c>VkQueue</c> handle.</summary>
    public nint Handle { get; }

    /// <summary>Initializes a new instance of the <see cref="VkQueue"/> struct.</summary>
    /// <param name="handle">The native <c>VkQueue</c> handle.</param>
    /// <param name="familyIndex">The index of the queue family this queue belongs to.</param>
    /// <exception cref="ArgumentException"><paramref name="handle"/> is zero.</exception>
    public VkQueue(nint handle, uint familyIndex) {
        if (0 == handle) {
            throw new ArgumentException(
                message: "Vulkan queue handle must be non-zero.",
                paramName: nameof(handle)
            );
        }

        Handle = handle;
        FamilyIndex = familyIndex;
    }
}
