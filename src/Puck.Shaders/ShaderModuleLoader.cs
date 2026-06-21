using Puck.Assets;

namespace Puck.Shaders;

public sealed class ShaderModuleLoader : IShaderModuleLoader {
    private const int DefaultMaxCachedShaders = 256;
    private const uint SpirVMagic = 0x07230203;
    private const int SpirVMinimumByteLength = 20;
    private const uint DxbcContainerMagic = 0x43425844; // 'DXBC' — the container wrapping DXBC (SM5) or DXIL (SM6) bytecode.
    private const int DxbcHeaderByteLength = 32;         // magic(4) + checksum(16) + version(4) + totalSize(4) + chunkCount(4).
    private const int DxbcTotalSizeByteOffset = 24;      // uint32: the container's declared total size, in bytes.
    private const int ShaderMagicByteLength = 4;

    private readonly IAssetSource m_assetSource;
    private readonly ContentAddressedLruCache<ReadOnlyMemory<byte>> m_shaderContentCache;

    internal ShaderModuleLoader(IAssetSource assetSource, int maxCachedShaders) {
        ArgumentNullException.ThrowIfNull(argument: assetSource);

        if (maxCachedShaders <= 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: maxCachedShaders,
                message: "Max cached shaders must be positive.",
                paramName: nameof(maxCachedShaders)
            );
        }

        m_assetSource = assetSource;
        m_shaderContentCache = new ContentAddressedLruCache<ReadOnlyMemory<byte>>(maxCachedShaders);
    }

    public ShaderModuleLoader(IAssetSource assetSource)
        : this(
            assetSource: assetSource,
            maxCachedShaders: DefaultMaxCachedShaders
        ) {
    }

    public ShaderStageInfo ValidateShader(ShaderStage stage, string path) {
        if (string.IsNullOrWhiteSpace(value: path)) {
            throw new ArgumentException(
                message: "Shader path must be provided.",
                paramName: nameof(path)
            );
        }

        var fullPath = Path.GetFullPath(path: path);

        if (!m_assetSource.Exists(path: fullPath)) {
            throw new FileNotFoundException(
                fileName: fullPath,
                message: $"The {stage.ToString().ToLowerInvariant()} shader file was not found: {fullPath}"
            );
        }

        var content = m_assetSource.Read(path: fullPath);
        var byteLength = content.Length;

        if (byteLength == 0) {
            throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is empty: {fullPath}");
        }

        if (byteLength < ShaderMagicByteLength) {
            throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is too small to identify (minimum {ShaderMagicByteLength} bytes): {fullPath}");
        }

        // Identify the bytecode format from its leading magic, then validate per-format. SPIR-V (Vulkan) is
        // word-based; a DXBC/DXIL container (Direct3D 12) is byte-granular and declares its own total size, so
        // the SPIR-V word-alignment rule must not be applied to it.
        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: content.Span[..ShaderMagicByteLength]);

        switch (magic) {
            case SpirVMagic:
                if ((byteLength % sizeof(uint)) != 0) {
                    throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} SPIR-V shader byte length must be a multiple of 4: {fullPath}");
                }

                if (byteLength < SpirVMinimumByteLength) {
                    throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is too small to be valid SPIR-V (minimum {SpirVMinimumByteLength} bytes): {fullPath}");
                }

                break;
            case DxbcContainerMagic:
                if (byteLength < DxbcHeaderByteLength) {
                    throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is too small to be a valid DXBC/DXIL container (minimum {DxbcHeaderByteLength} bytes): {fullPath}");
                }

                var declaredByteLength = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: content.Span.Slice(start: DxbcTotalSizeByteOffset, length: sizeof(uint)));

                if (declaredByteLength != (uint)byteLength) {
                    throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} DXBC/DXIL container declares {declaredByteLength} bytes but the file is {byteLength} bytes: {fullPath}");
                }

                break;
            default:
                throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is not recognized as SPIR-V or DXBC/DXIL bytecode: {fullPath}");
        }

        var contentHash = AssetContentHash.Compute(content: content.Span);
        var cachedContent = m_shaderContentCache.GetOrAdd(
            hash: contentHash,
            valueFactory: () => content
        );

        return new ShaderStageInfo(
            ByteLength: byteLength,
            Content: cachedContent,
            ContentHash: contentHash,
            Path: fullPath,
            Stage: stage
        );
    }
    public ValidatedShaderSet ValidateShaderSet(ShaderSet shaderSet) {
        return new ValidatedShaderSet(
            Fragment: ValidateShader(
                path: shaderSet.FragmentShaderPath,
                stage: ShaderStage.Fragment
            ),
            Vertex: ValidateShader(
                path: shaderSet.VertexShaderPath,
                stage: ShaderStage.Vertex
            )
        );
    }
}
