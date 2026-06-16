using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Factories;

/// <summary>
/// The default <see cref="IDirectXPipelineFactory"/>: it extracts the bytecode pointers from the shader blobs,
/// calls the pipeline API, and returns an owning <see cref="DirectXPipeline"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXPipelineFactory : IDirectXPipelineFactory {
    private readonly IDirectXPipelineApi m_pipelineApi;

    /// <summary>Initializes a new instance of the <see cref="DirectXPipelineFactory"/> class.</summary>
    /// <param name="pipelineApi">The pipeline API used to create the root signature and pipeline state.</param>
    /// <exception cref="ArgumentNullException"><paramref name="pipelineApi"/> is <see langword="null"/>.</exception>
    public DirectXPipelineFactory(IDirectXPipelineApi pipelineApi) {
        ArgumentNullException.ThrowIfNull(pipelineApi);

        m_pipelineApi = pipelineApi;
    }

    /// <inheritdoc/>
    public DirectXPipeline Create(
        IDirectXDeviceContext deviceContext,
        DirectXShaderBytecode vertexShader,
        DirectXShaderBytecode pixelShader
    ) {
        return Build(
            create: m_pipelineApi.CreateGraphicsPipeline,
            deviceContext: deviceContext,
            pixelShader: pixelShader,
            vertexShader: vertexShader
        );
    }
    /// <inheritdoc/>
    public DirectXPipeline CreateTextured(
        IDirectXDeviceContext deviceContext,
        DirectXShaderBytecode vertexShader,
        DirectXShaderBytecode pixelShader
    ) {
        return Build(
            create: m_pipelineApi.CreateTexturedGraphicsPipeline,
            deviceContext: deviceContext,
            pixelShader: pixelShader,
            vertexShader: vertexShader
        );
    }

    private static DirectXPipeline Build(
        Func<DirectXGraphicsPipelineCreateRequest, DirectXGraphicsPipelineCreateResult> create,
        IDirectXDeviceContext deviceContext,
        DirectXShaderBytecode vertexShader,
        DirectXShaderBytecode pixelShader
    ) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(pixelShader);
        ArgumentNullException.ThrowIfNull(vertexShader);

        var result = create(new DirectXGraphicsPipelineCreateRequest(
            DeviceHandle: deviceContext.Device.Handle,
            PixelShaderBytecode: pixelShader.BufferPointer,
            PixelShaderLength: pixelShader.BufferLength,
            VertexShaderBytecode: vertexShader.BufferPointer,
            VertexShaderLength: vertexShader.BufferLength
        ));

        return new DirectXPipeline(
            pipelineStateHandle: result.PipelineStateHandle,
            rootSignatureHandle: result.RootSignatureHandle
        );
    }
}
