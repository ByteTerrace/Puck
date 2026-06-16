namespace Puck.DirectX.Messages;

/// <summary>
/// The result of creating a graphics pipeline: the root signature and the pipeline state object.
/// </summary>
/// <param name="RootSignatureHandle">The created native <c>ID3D12RootSignature</c> handle.</param>
/// <param name="PipelineStateHandle">The created native <c>ID3D12PipelineState</c> handle.</param>
public readonly record struct DirectXGraphicsPipelineCreateResult(
    nint RootSignatureHandle,
    nint PipelineStateHandle
);
