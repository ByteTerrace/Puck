using Puck.Abstractions.Gpu;
using Puck.Assets;

namespace Puck.Compositing;

/// <summary>
/// Records a content-addressed <see cref="GpuDrawCommand"/> list into an offscreen <see cref="IGpuRenderTarget"/>
/// by driving the shared <see cref="IGpuCommandRecorder"/>. Because the recorder, pipelines, and render targets
/// are all backend-neutral, the same compositor runs unchanged on every backend (Vulkan, DirectX, …); each
/// command resolves its pipeline from the supplied content-addressed map, so a pipeline's identity is the hash
/// of its defining shader asset regardless of backend.
/// <para>
/// The composed target is presented through the existing <c>ISurfacePresenter.Present(Surface)</c> path (using
/// <see cref="IGpuRenderTarget.ImageViewHandle"/>), so multi-draw compositing adds no present-time cost beyond
/// the single fullscreen blit the single-surface path already performs.
/// </para>
/// </summary>
public sealed class GpuCompositor {
    private readonly IGpuCommandRecorder m_commandRecorder;

    /// <summary>Initializes a new instance of the <see cref="GpuCompositor"/> class.</summary>
    /// <param name="commandRecorder">The backend-neutral command recorder the active backend provides.</param>
    /// <exception cref="ArgumentNullException"><paramref name="commandRecorder"/> is <see langword="null"/>.</exception>
    public GpuCompositor(IGpuCommandRecorder commandRecorder) {
        ArgumentNullException.ThrowIfNull(commandRecorder);

        m_commandRecorder = commandRecorder;
    }

    /// <summary>
    /// Records <paramref name="drawCommands"/> (in list order) into <paramref name="target"/>'s command buffer
    /// and returns that command buffer handle, ready to submit through an <see cref="IGpuQueueSubmitter"/>.
    /// </summary>
    /// <param name="deviceContext">The shared GPU device context.</param>
    /// <param name="target">The offscreen render target to compose into.</param>
    /// <param name="drawCommands">The draw list to replay. Must contain at least one command.</param>
    /// <param name="pipelines">The content-addressed pipeline map every command's <see cref="GpuDrawCommand.PipelineId"/> resolves against.</param>
    /// <returns>The render target's command buffer handle, recorded and ready to submit.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException"><paramref name="drawCommands"/> is empty.</exception>
    /// <exception cref="KeyNotFoundException">A command references a pipeline id absent from <paramref name="pipelines"/>.</exception>
    public nint Record(
        IGpuDeviceContext deviceContext,
        IGpuRenderTarget target,
        IReadOnlyList<GpuDrawCommand> drawCommands,
        IReadOnlyDictionary<AssetContentHash, IGpuPipeline> pipelines
    ) {
        ArgumentNullException.ThrowIfNull(deviceContext);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(drawCommands);
        ArgumentNullException.ThrowIfNull(pipelines);

        if (drawCommands.Count == 0) {
            throw new ArgumentException(
                message: "A compose pass requires at least one draw command.",
                paramName: nameof(drawCommands)
            );
        }

        var deviceHandle = deviceContext.DeviceHandle;
        var commandBufferHandle = target.CommandBufferHandle;

        m_commandRecorder.BeginCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        m_commandRecorder.BeginRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            framebufferHandle: target.FramebufferHandle,
            height: target.Height,
            renderPassHandle: target.RenderPassHandle,
            width: target.Width
        );
        m_commandRecorder.SetScissor(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle,
            height: target.Height,
            width: target.Width,
            x: 0,
            y: 0
        );

        foreach (var command in drawCommands) {
            var pipeline = Resolve(pipelines: pipelines, pipelineId: command.PipelineId);

            m_commandRecorder.BindGraphicsPipeline(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                pipelineHandle: pipeline.Handle
            );

            if (command.VertexBufferHandle != 0) {
                m_commandRecorder.BindVertexBuffer(
                    commandBufferHandle: commandBufferHandle,
                    deviceHandle: deviceHandle,
                    vertexBufferHandle: command.VertexBufferHandle
                );
            }

            if (command.PushConstants is { } pushConstants) {
                m_commandRecorder.PushConstants(
                    commandBufferHandle: commandBufferHandle,
                    data: pushConstants.Data.Span,
                    deviceHandle: deviceHandle,
                    offset: pushConstants.Offset,
                    pipelineLayoutHandle: pipeline.LayoutHandle,
                    stageFlags: pushConstants.StageFlags
                );
            }

            if (command.DescriptorSetHandle != 0) {
                m_commandRecorder.BindDescriptorSet(
                    commandBufferHandle: commandBufferHandle,
                    descriptorSetHandle: command.DescriptorSetHandle,
                    deviceHandle: deviceHandle,
                    pipelineLayoutHandle: pipeline.LayoutHandle
                );
            }

            var parameters = command.DrawParameters;

            m_commandRecorder.Draw(
                commandBufferHandle: commandBufferHandle,
                deviceHandle: deviceHandle,
                firstInstance: parameters.FirstInstance,
                firstVertex: parameters.FirstVertex,
                instanceCount: parameters.InstanceCount,
                vertexCount: parameters.VertexCount
            );
        }

        m_commandRecorder.EndRenderPass(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );
        m_commandRecorder.EndCommandBuffer(
            commandBufferHandle: commandBufferHandle,
            deviceHandle: deviceHandle
        );

        return commandBufferHandle;
    }

    private static IGpuPipeline Resolve(IReadOnlyDictionary<AssetContentHash, IGpuPipeline> pipelines, AssetContentHash pipelineId) {
        if (!pipelines.TryGetValue(key: pipelineId, value: out var pipeline)) {
            throw new KeyNotFoundException(message: $"No pipeline is registered for content id '{pipelineId}'.");
        }

        return pipeline;
    }
}
