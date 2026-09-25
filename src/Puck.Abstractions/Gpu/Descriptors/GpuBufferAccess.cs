namespace Puck.Abstractions.Gpu;

/// <summary>
/// How a shader reaches a buffer through a binding: read-only or read-write. On Direct3D 12 it chooses a shader
/// resource view or an unordered access view, and must match the binding's range in the root signature
/// (<see cref="GpuComputeBindingKind.StorageBufferRead"/> or <see cref="GpuComputeBindingKind.StorageBufferReadWrite"/>);
/// a Vulkan storage buffer is the same descriptor either way.
/// </summary>
public enum GpuBufferAccess : uint {
    /// <summary>Read-only: a <c>StructuredBuffer</c> or <c>ByteAddressBuffer</c>.</summary>
    Read = 1,
    /// <summary>Read-write: a <c>RWStructuredBuffer</c> or <c>RWByteAddressBuffer</c>.</summary>
    ReadWrite = 2,
}
