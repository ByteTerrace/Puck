using System.Runtime.InteropServices;

namespace Puck.Vulkan.Bindings;

/// <summary>
/// Reports the memory types and heaps available on a physical device. Use
/// <see cref="MemoryTypePropertyFlags(int)"/> to read the property flags of a given memory type.
/// </summary>
/// <remarks>
/// EXCEPTION (not 1:1): blittable form so the type is usable inside an unmanaged function-pointer signature. Memory
/// type i is the {propertyFlags, heapIndex} pair at MemoryTypePairs[i * 2]; the heap array rides as raw ulongs purely
/// to 8-align and size the tail correctly.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct VkPhysicalDeviceMemoryProperties {
    /// <summary>The number of valid entries (memory types) addressable through <see cref="MemoryTypePairs"/>.</summary>
    public uint MemoryTypeCount;
    /// <summary>The raw memory types as packed <c>{ propertyFlags, heapIndex }</c> pairs: memory type <c>i</c> occupies indices <c>2i</c> and <c>2i + 1</c>.</summary>
    public fixed uint MemoryTypePairs[64];
    /// <summary>The number of valid entries (memory heaps) carried in <see cref="MemoryHeapPairs"/>.</summary>
    public uint MemoryHeapCount;
    /// <summary>The raw memory heaps, carried as untyped <see langword="ulong"/> storage to preserve the native size and 8-byte alignment of the tail.</summary>
    public fixed ulong MemoryHeapPairs[32];

    /// <summary>Gets the property flags of the memory type at the given index.</summary>
    /// <param name="memoryTypeIndex">The zero-based index of the memory type, which must be less than <see cref="MemoryTypeCount"/>.</param>
    /// <returns>A bitmask of <c>VkMemoryPropertyFlagBits</c> describing the memory type.</returns>
    public uint MemoryTypePropertyFlags(int memoryTypeIndex) {
        return MemoryTypePairs[(memoryTypeIndex * 2)];
    }
}
