using Puck.Assets;

namespace Puck.Shaders;

public readonly record struct ShaderStageInfo(
    ShaderStage Stage,
    string Path,
    long ByteLength,
    AssetContentHash ContentHash,
    ReadOnlyMemory<byte> Content
);
