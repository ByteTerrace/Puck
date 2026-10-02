using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>
/// Reads the descriptor bindings of compiled bytecode of either backend, whichever it is, in the shape
/// <see cref="ShaderInterfaceLayout.Mismatch"/> holds a module to: a SPIR-V module through
/// <see cref="SpirvInterfaceReader"/>, and a DXIL container through a <see cref="DxilInterfaceReader"/> over the
/// <c>dxcompiler.dll</c> beside the <c>dxc</c> a <see cref="ShaderToolchain"/> resolves, loaded on the first container it
/// reads and kept until the reflector is disposed. Only Windows loads that library, so a DXIL container is refused
/// elsewhere.
/// </summary>
public sealed class ShaderBytecodeReflector : IDisposable {
    private readonly ShaderToolchain m_toolchain;

    private IDisposable? m_dxil;
    private bool m_disposed;

    /// <summary>Initializes a new instance of the <see cref="ShaderBytecodeReflector"/> class.</summary>
    /// <param name="toolchain">The toolchain whose <c>dxc</c> locates the <c>dxcompiler.dll</c> a DXIL container is
    /// reflected through.</param>
    /// <exception cref="ArgumentNullException"><paramref name="toolchain"/> is <see langword="null"/>.</exception>
    public ShaderBytecodeReflector(ShaderToolchain toolchain) {
        ArgumentNullException.ThrowIfNull(argument: toolchain);

        m_toolchain = toolchain;
    }

    /// <summary>Reads every binding the bytecode declares.</summary>
    /// <param name="bytecode">A SPIR-V module or a DXIL container, as DXC wrote it.</param>
    /// <returns>The bindings, ordered as <see cref="ShaderInterfaceLayout.Ordered"/> orders them.</returns>
    /// <exception cref="ObjectDisposedException">The reflector is disposed.</exception>
    /// <exception cref="InvalidDataException">The bytes are neither a SPIR-V module nor a DXIL container, or the reader
    /// refuses them.</exception>
    /// <exception cref="InvalidOperationException">The bytes are a DXIL container and no <c>dxcompiler.dll</c> can be
    /// loaded to reflect it: the host is not Windows, or the toolchain resolves no <c>dxc</c> with the library beside
    /// it.</exception>
    public IReadOnlyList<ShaderInterfaceBinding> Read(ReadOnlySpan<byte> bytecode) {
        ObjectDisposedException.ThrowIf(condition: m_disposed, instance: this);

        if (ShaderBytecode.IsSpirV(bytecode: bytecode)) {
            return SpirvInterfaceReader.Read(module: bytecode);
        }
        if (!ShaderBytecode.IsDxbcContainer(bytecode: bytecode)) {
            throw new InvalidDataException(message: "The bytecode is neither a SPIR-V module nor a DXIL container.");
        }
        if (!OperatingSystem.IsWindows()) {
            throw new InvalidOperationException(message: "Reflecting a DXIL container needs dxcompiler.dll, which only Windows loads.");
        }

        return Dxil().Read(container: bytecode);
    }
    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_dxil?.Dispose();
        m_dxil = null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private DxilInterfaceReader Dxil() {
        if (m_dxil is DxilInterfaceReader reader) {
            return reader;
        }

        try {
            reader = DxilInterfaceReader.Load(toolchain: m_toolchain);
        } catch (Exception exception) when ((exception is ShaderToolMissingException or DllNotFoundException or EntryPointNotFoundException or InvalidDataException)) {
            throw new InvalidOperationException(
                innerException: exception,
                message: $"Reflecting a DXIL container needs dxcompiler.dll beside dxc: {exception.Message}"
            );
        }

        m_dxil = reader;

        return reader;
    }
}
