namespace Puck.Abstractions.Gpu;

/// <summary>
/// A push constant range together with the data to push: the byte offset, the shader stages that read it,
/// and the payload.
/// </summary>
public sealed class GpuPushConstantBinding {
    /// <summary>Gets the push constant data to upload.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>Gets the byte offset of the range within the push constant block.</summary>
    public uint Offset { get; }
    /// <summary>Gets the size, in bytes, of the data (the length of <see cref="Data"/>).</summary>
    public uint Size => checked((uint)Data.Length);
    /// <summary>Gets the shader stages that read the range.</summary>
    public GpuShaderStage StageFlags { get; }

    /// <summary>Initializes a new instance of the <see cref="GpuPushConstantBinding"/> class.</summary>
    /// <param name="offset">The byte offset of the range within the push constant block.</param>
    /// <param name="stageFlags">The shader stages that read the range; must name at least one stage.</param>
    /// <param name="data">The push constant data to upload.</param>
    public GpuPushConstantBinding(uint offset, GpuShaderStage stageFlags, ReadOnlyMemory<byte> data) {
        ValidateRange(
            stageFlags: stageFlags,
            offset: offset,
            dataLength: data.Length
        );

        Offset = offset;
        StageFlags = stageFlags;
        Data = data;
    }

    /// <summary>Validates the backend-neutral alignment and stage-mask requirements for a push-constant update.</summary>
    public static void ValidateRange(GpuShaderStage stageFlags, uint offset, int dataLength) {
        const GpuShaderStage AllStages = GpuShaderStage.Vertex | GpuShaderStage.Fragment | GpuShaderStage.Compute;

        if (
            (GpuShaderStage.None == stageFlags) ||
            (GpuShaderStage.None != (stageFlags & ~AllStages))
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(stageFlags),
                stageFlags,
                "The push constant range must contain only defined shader-stage flags and name at least one stage."
            );
        }
        if (0 != (offset & 3u)) {
            throw new ArgumentOutOfRangeException(
                nameof(offset),
                offset,
                "The push constant offset must be four-byte aligned."
            );
        }
        if (
            (dataLength <= 0) ||
            (0 != (dataLength & 3))
        ) {
            throw new ArgumentOutOfRangeException(
                nameof(dataLength),
                dataLength,
                "Push constant data must be non-empty and a multiple of four bytes."
            );
        }

        _ = checked((offset + ((uint)dataLength)));
    }
}
