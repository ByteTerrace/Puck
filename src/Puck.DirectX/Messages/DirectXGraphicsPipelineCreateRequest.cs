namespace Puck.DirectX.Messages;

/// <summary>
/// Describes a graphics pipeline to create: an empty root signature plus a pipeline state over the given
/// vertex and pixel shader bytecode, with the fixed <c>POSITION</c> + <c>COLOR</c> input layout and an
/// <c>R8G8B8A8_UNORM</c> render target.
/// </summary>
/// <param name="DeviceHandle">The native <c>ID3D12Device</c> handle.</param>
/// <param name="VertexShaderBytecode">A pointer to the vertex shader bytecode.</param>
/// <param name="VertexShaderLength">The length, in bytes, of the vertex shader bytecode.</param>
/// <param name="PixelShaderBytecode">A pointer to the pixel shader bytecode.</param>
/// <param name="PixelShaderLength">The length, in bytes, of the pixel shader bytecode.</param>
public readonly record struct DirectXGraphicsPipelineCreateRequest(
    nint DeviceHandle,
    nint VertexShaderBytecode,
    nuint VertexShaderLength,
    nint PixelShaderBytecode,
    nuint PixelShaderLength
);
