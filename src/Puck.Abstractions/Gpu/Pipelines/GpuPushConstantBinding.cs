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
        if (stageFlags == GpuShaderStage.None) {
            throw new ArgumentOutOfRangeException(actualValue: stageFlags, message: "The push constant range must name at least one shader stage.", paramName: nameof(stageFlags));
        }
        ArgumentOutOfRangeException.ThrowIfZero(value: data.Length, paramName: nameof(data));

        Offset = offset;
        StageFlags = stageFlags;
        Data = data;
    }
}
