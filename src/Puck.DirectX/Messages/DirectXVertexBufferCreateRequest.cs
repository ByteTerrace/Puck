namespace Puck.DirectX.Messages;

/// <summary>
/// Describes a vertex buffer to create in an upload heap and populate.
/// </summary>
/// <param name="DeviceHandle">The native <c>ID3D12Device</c> handle.</param>
/// <param name="VertexData">The tightly packed vertex bytes to upload.</param>
/// <param name="StrideBytes">The size, in bytes, of one vertex.</param>
public readonly record struct DirectXVertexBufferCreateRequest(
    nint DeviceHandle,
    ReadOnlyMemory<byte> VertexData,
    uint StrideBytes
);
