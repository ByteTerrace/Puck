using System.Runtime.Versioning;
using Puck.DirectX.Interfaces;
using Puck.DirectX.Interop;
using Puck.DirectX.Messages;

namespace Puck.DirectX.Factories;

/// <summary>
/// The default <see cref="IDirectXVertexBufferFactory"/>: it creates an upload-heap vertex buffer of the
/// requested data and returns an owning <see cref="DirectXVertexBuffer"/>.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXVertexBufferFactory : IDirectXVertexBufferFactory {
    private readonly IDirectXVertexBufferApi m_vertexBufferApi;

    /// <summary>Initializes a new instance of the <see cref="DirectXVertexBufferFactory"/> class.</summary>
    /// <param name="vertexBufferApi">The vertex-buffer API used to create and own the underlying resource.</param>
    /// <exception cref="ArgumentNullException"><paramref name="vertexBufferApi"/> is <see langword="null"/>.</exception>
    public DirectXVertexBufferFactory(IDirectXVertexBufferApi vertexBufferApi) {
        ArgumentNullException.ThrowIfNull(vertexBufferApi);

        m_vertexBufferApi = vertexBufferApi;
    }

    /// <inheritdoc/>
    public DirectXVertexBuffer Create(
        IDirectXDeviceContext deviceContext,
        ReadOnlyMemory<byte> vertexData,
        uint strideBytes
    ) {
        ArgumentNullException.ThrowIfNull(deviceContext);

        var result = m_vertexBufferApi.CreateVertexBuffer(request: new DirectXVertexBufferCreateRequest(
            DeviceHandle: deviceContext.Device.Handle,
            StrideBytes: strideBytes,
            VertexData: vertexData
        ));

        return new DirectXVertexBuffer(
            bufferHandle: result.BufferHandle,
            gpuVirtualAddress: result.GpuVirtualAddress,
            sizeBytes: result.SizeBytes,
            strideBytes: strideBytes,
            vertexBufferApi: m_vertexBufferApi
        );
    }
}
