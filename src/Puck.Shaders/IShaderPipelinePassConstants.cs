using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>Writes one pipeline pass push-constant block for a frame.</summary>
public interface IShaderPipelinePassConstants {
    /// <summary>Gets the exact push-constant block size in bytes.</summary>
    uint SizeBytes { get; }
    /// <summary>Gets the stages that consume the block.</summary>
    GpuShaderStage Stages { get; }
    /// <summary>Writes one complete block into the supplied destination.</summary>
    void Write(in FrameContext context, in ShaderFrameInput input, uint passWidth, uint passHeight, ulong frameCounter, Span<byte> destination);
}
