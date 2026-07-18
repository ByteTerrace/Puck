using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Puck.DirectX.Interop;

/// <summary>
/// Holds DXIL bytecode pinned in memory so a <c>D3D12_SHADER_BYTECODE</c> can point directly into it.
/// <see cref="Handle"/> is the pinned pointer; <see cref="BytecodeLength"/> is the byte count.
/// </summary>
[SupportedOSPlatform("windows10.0.10240")]
public sealed class DirectXGpuShaderModule : IGpuShaderModule, IDisposable {
    private readonly byte[] m_bytecode;
    private GCHandle m_pin;

    /// <summary>Initializes a new instance pinning a copy of the given bytecode.</summary>
    public DirectXGpuShaderModule(ReadOnlyMemory<byte> bytecode) {
        m_bytecode = bytecode.ToArray();
        m_pin = GCHandle.Alloc(type: GCHandleType.Pinned, value: m_bytecode);
    }

    /// <summary>Gets the size, in bytes, of the DXIL blob.</summary>
    public nuint BytecodeLength => (nuint)m_bytecode.Length;
    /// <inheritdoc/>
    public nint Handle => m_pin.AddrOfPinnedObject();

    /// <inheritdoc/>
    public void Dispose() => m_pin.Free();
}
