using Puck.Assets;

namespace Puck.Shaders;

public sealed class ShaderModuleLoader : IShaderModuleLoader {
    private const int DefaultMaxCachedShaders = 256;
    private const uint SpirVMagic = 0x07230203;
    private const int SpirVMinimumByteLength = 20;

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

        if ((byteLength % sizeof(uint)) != 0) {
            throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader byte length must be a multiple of 4: {fullPath}");
        }

        if (byteLength < SpirVMinimumByteLength) {
            throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file is too small to be valid SPIR-V (minimum {SpirVMinimumByteLength} bytes): {fullPath}");
        }

        var magic = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(source: content.Span[..4]);

        if (magic != SpirVMagic) {
            throw new InvalidDataException(message: $"The {stage.ToString().ToLowerInvariant()} shader file does not have a valid SPIR-V magic number: {fullPath}");
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
