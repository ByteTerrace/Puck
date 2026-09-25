using Puck.Abstractions.Gpu;

namespace Puck.Shaders;

/// <summary>The barriers a package pass records around a render pass of its own. The planner orders a package pass in
/// the compute shape it reaches resources by: its inputs arrive shader-readable for the compute stage and its outputs
/// in the port's layout for compute writes (<see cref="RenderGraphPackageResource.Image"/>), and the next planned access
/// expects them left so. A package that draws bridges both sides: it makes its inputs visible to the fragment stage and
/// moves its target into <see cref="GpuImageLayout.RenderTarget"/> before its render pass, then moves the target back
/// into the port's layout, its color writes visible to the compute stage the next planned barrier waits on.</summary>
public static class RenderGraphPackageDraw {
    /// <summary>Records the barriers before a package's render pass: its sampled inputs made visible to the fragment
    /// stage, and its target moved from the port's layout into <see cref="GpuImageLayout.RenderTarget"/> after the
    /// compute-stage access the planned barrier ordered it behind.</summary>
    /// <param name="recorder">The recording's recorder.</param>
    /// <param name="commandBuffer">The recording's command buffer.</param>
    /// <param name="target">The output the render pass draws into, as the recording resolved it.</param>
    public static void Enter(IGpuRecorder recorder, nint commandBuffer, in RenderGraphPackageResource target) {
        ArgumentNullException.ThrowIfNull(argument: recorder);

        recorder.MemoryBarrier(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.FragmentShader,
            sourceAccessMask: GpuAccess.ShaderRead,
            sourceStageMask: GpuStage.ComputeShader
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ColorAttachmentWrite,
            destinationStageMask: GpuStage.ColorAttachmentOutput,
            imageHandle: target.Image.ImageHandle,
            newLayout: GpuImageLayout.RenderTarget,
            oldLayout: target.Image.Layout,
            sourceAccessMask: GpuAccess.ShaderWrite,
            sourceStageMask: GpuStage.ComputeShader
        );
    }
    /// <summary>Records the barrier after a package's render pass: its target moved from
    /// <see cref="GpuImageLayout.RenderTarget"/> back into the port's layout, its color writes made visible to the
    /// compute stage the next planned access is ordered behind.</summary>
    /// <param name="recorder">The recording's recorder.</param>
    /// <param name="commandBuffer">The recording's command buffer.</param>
    /// <param name="target">The output the render pass drew into, as the recording resolved it.</param>
    public static void Leave(IGpuRecorder recorder, nint commandBuffer, in RenderGraphPackageResource target) {
        ArgumentNullException.ThrowIfNull(argument: recorder);

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: target.Image.ImageHandle,
            newLayout: target.Image.Layout,
            oldLayout: GpuImageLayout.RenderTarget,
            sourceAccessMask: GpuAccess.ColorAttachmentWrite,
            sourceStageMask: GpuStage.ColorAttachmentOutput
        );
    }
}
