namespace Puck.DirectX.Messages;

/// <summary>
/// The result of creating a vertex buffer: the resource and its GPU virtual address.
/// </summary>
/// <param name="BufferHandle">The created native <c>ID3D12Resource</c> handle.</param>
/// <param name="GpuVirtualAddress">The buffer's GPU virtual address, for the vertex-buffer view.</param>
/// <param name="SizeBytes">The size, in bytes, of the buffer.</param>
public readonly record struct DirectXVertexBufferCreateResult(
    nint BufferHandle,
    ulong GpuVirtualAddress,
    uint SizeBytes
);
