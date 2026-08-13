using System.Buffers.Binary;

namespace Puck.Abstractions.Gpu;

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
            throw new ArgumentException(
                message: $"Shader bytecode is too small to identify (minimum {MagicByteLength} bytes); got {bytecode.Length}.",
                paramName: nameof(bytecode)
            );
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(source: bytecode[..MagicByteLength]);

        switch (magic) {
            case SpirVMagic:
                if ((bytecode.Length % sizeof(uint)) != 0) {
                    throw new ArgumentException(
                        message: $"SPIR-V shader byte length must be a multiple of 4; got {bytecode.Length}.",
                        paramName: nameof(bytecode)
                    );
                }

                if (bytecode.Length < SpirVMinimumByteLength) {
                    throw new ArgumentException(
                        message: $"SPIR-V shader is too small to be valid (minimum {SpirVMinimumByteLength} bytes); got {bytecode.Length}.",
                        paramName: nameof(bytecode)
                    );
                }

                ValidateSpirV(bytecode: bytecode);
                break;
            case DxbcContainerMagic:
                if (bytecode.Length < DxbcHeaderByteLength) {
                    throw new ArgumentException(
                        message: $"DXBC/DXIL container is too small to be valid (minimum {DxbcHeaderByteLength} bytes); got {bytecode.Length}.",
                        paramName: nameof(bytecode)
                    );
                }

                ValidateDxbc(bytecode: bytecode);
                break;
            default:
                throw new ArgumentException(
                    message: "Shader bytecode is not recognized as SPIR-V or DXBC/DXIL bytecode.",
                    paramName: nameof(bytecode)
                );
        }
    }

    private static void ValidateSpirV(ReadOnlySpan<byte> bytecode) {
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(4, 4));
        var bound = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(12, 4));
        var schema = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(16, 4));

        if ((0 != (version & 0xff0000ffu)) || (0 == (version & 0x00ff0000u)) || (0 == bound) || (0 != schema)) {
            throw new ArgumentException(message: "SPIR-V has an invalid version, id bound, or reserved schema word.", paramName: nameof(bytecode));
        }

        var wordIndex = 5;
        var wordCount = (bytecode.Length / sizeof(uint));

        if (wordIndex == wordCount) {
            throw new ArgumentException(message: "SPIR-V contains no instructions.", paramName: nameof(bytecode));
        }

        while (wordIndex < wordCount) {
            var instruction = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(wordIndex * sizeof(uint), sizeof(uint)));
            var instructionWordCount = (int)(instruction >> 16);

            if ((instructionWordCount <= 0) || (instructionWordCount > (wordCount - wordIndex))) {
                throw new ArgumentException(message: "SPIR-V contains a zero-length or truncated instruction.", paramName: nameof(bytecode));
            }

            wordIndex += instructionWordCount;
        }
    }

    private static void ValidateDxbc(ReadOnlySpan<byte> bytecode) {
        var declaredSize = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(24, 4));
        var chunkCount = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(28, 4));
        var tableByteLength = checked(DxbcHeaderByteLength + checked((int)chunkCount * sizeof(uint)));

        if ((declaredSize != bytecode.Length) || (0 == chunkCount) || (tableByteLength > bytecode.Length)) {
            throw new ArgumentException(message: "DXBC/DXIL has an invalid declared size or chunk table.", paramName: nameof(bytecode));
        }

        for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++) {
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(DxbcHeaderByteLength + checked((int)chunkIndex * sizeof(uint)), sizeof(uint)));

            if ((0 != (offset & 3u)) || (offset < tableByteLength) || (offset > (uint)(bytecode.Length - 8))) {
                throw new ArgumentException(message: "DXBC/DXIL contains an invalid chunk offset.", paramName: nameof(bytecode));
            }

            var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytecode.Slice(checked((int)offset + 4), 4));

            if (payloadLength > (uint)(bytecode.Length - checked((int)offset + 8))) {
                throw new ArgumentException(message: "DXBC/DXIL contains a truncated chunk.", paramName: nameof(bytecode));
            }
        }
    }
}
