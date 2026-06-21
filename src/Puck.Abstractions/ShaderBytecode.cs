using System.Buffers.Binary;

namespace Puck.Abstractions;

/// <summary>
/// Neutral validation of compiled shader bytecode handed to <see cref="IGpuShaderModuleFactory.Create"/> — the
/// in-memory counterpart of the file-based shader loader's format check, so the per-backend create path rejects
/// malformed bytecode instead of forwarding it to the driver. Recognizes SPIR-V (Vulkan) and the DXBC container
/// (Direct3D 12, which wraps both DXBC (SM5) and DXIL (SM6)) by their leading magic. File loads are additionally
/// content-hashed and cached by the shader loader; this validates the bytecode the create path is actually given.
/// </summary>
public static class ShaderBytecode {
    private const uint SpirVMagic = 0x07230203;
    private const uint DxbcContainerMagic = 0x43425844; // 'DXBC' — wraps DXBC (SM5) or DXIL (SM6) bytecode.
    private const int SpirVMinimumByteLength = 20;
    private const int DxbcHeaderByteLength = 32; // magic(4) + checksum(16) + version(4) + totalSize(4) + chunkCount(4).
    private const int MagicByteLength = 4;

    /// <summary>Validates that <paramref name="bytecode"/> is recognizable, well-formed SPIR-V or DXBC/DXIL bytecode.</summary>
    /// <param name="bytecode">The compiled shader bytecode.</param>
    /// <exception cref="ArgumentException"><paramref name="bytecode"/> is too small, mis-aligned (SPIR-V), or an unrecognized format.</exception>
    public static void ValidateFormat(ReadOnlySpan<byte> bytecode) {
        if (bytecode.Length < MagicByteLength) {
            throw new ArgumentException(message: $"Shader bytecode is too small to identify (minimum {MagicByteLength} bytes); got {bytecode.Length}.", paramName: nameof(bytecode));
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source: bytecode[..MagicByteLength]);

        switch (magic) {
            case SpirVMagic:
                if ((bytecode.Length % sizeof(uint)) != 0) {
                    throw new ArgumentException(message: $"SPIR-V shader byte length must be a multiple of 4; got {bytecode.Length}.", paramName: nameof(bytecode));
                }

                if (bytecode.Length < SpirVMinimumByteLength) {
                    throw new ArgumentException(message: $"SPIR-V shader is too small to be valid (minimum {SpirVMinimumByteLength} bytes); got {bytecode.Length}.", paramName: nameof(bytecode));
                }

                break;
            case DxbcContainerMagic:
                if (bytecode.Length < DxbcHeaderByteLength) {
                    throw new ArgumentException(message: $"DXBC/DXIL container is too small to be valid (minimum {DxbcHeaderByteLength} bytes); got {bytecode.Length}.", paramName: nameof(bytecode));
                }

                break;
            default:
                throw new ArgumentException(message: "Shader bytecode is not recognized as SPIR-V or DXBC/DXIL bytecode.", paramName: nameof(bytecode));
        }
    }
}
