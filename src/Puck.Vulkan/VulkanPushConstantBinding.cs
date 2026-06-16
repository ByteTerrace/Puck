namespace Puck.Vulkan;

/// <summary>
/// A push constant range together with the data to push into it: the byte offset, the shader stages that
/// read it, and the payload.
/// </summary>
public sealed class VulkanPushConstantBinding {
    /// <summary>Gets the push constant data to upload.</summary>
    public ReadOnlyMemory<byte> Data { get; }
    /// <summary>Gets the byte offset of the range within the push constant block.</summary>
    public uint Offset { get; }
    /// <summary>Gets the size, in bytes, of the data (the length of <see cref="Data"/>).</summary>
    public uint Size => checked((uint)Data.Length);
    /// <summary>Gets the bitmask of <c>VkShaderStageFlagBits</c> identifying the stages that read the range.</summary>
    public uint StageFlags { get; }

    /// <summary>Initializes a new instance of the <see cref="VulkanPushConstantBinding"/> class.</summary>
    /// <param name="offset">The byte offset of the range within the push constant block.</param>
    /// <param name="stageFlags">A bitmask of <c>VkShaderStageFlagBits</c> identifying the stages that read the range.</param>
    /// <param name="data">The push constant data to upload.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="stageFlags"/> is zero, or <paramref name="data"/> is empty.</exception>
    public VulkanPushConstantBinding(uint offset, uint stageFlags, ReadOnlyMemory<byte> data) {
        if (stageFlags == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: stageFlags,
                message: "Push-constant stage flags must be non-zero.",
                paramName: nameof(stageFlags)
            );
        }

        if (data.Length == 0) {
            throw new ArgumentOutOfRangeException(
                actualValue: data.Length,
                message: "Push-constant data must be non-empty.",
                paramName: nameof(data)
            );
        }

        Offset = offset;
        StageFlags = stageFlags;
        Data = data;
    }
}
