namespace Puck.Abstractions.Gpu;

/// <summary>
/// A kind of device object one of <see cref="GpuDeviceServices"/>' creating members makes, which
/// <see cref="GpuCreationFaults"/> counts and can fail. <see cref="GpuCreationFaults.NameOf"/> gives each its one
/// spelling.
/// </summary>
public enum GpuCreationKind : byte {
    /// <summary>A compute or graphics pipeline (<see cref="IGpuPipelineFactory"/>), spelled <c>pipeline</c>.</summary>
    Pipeline = 0,
    /// <summary>A host-visible or device-local buffer (<see cref="IGpuBufferFactory"/>), spelled <c>buffer</c>.</summary>
    Buffer = 1,
    /// <summary>An image (<see cref="IGpuImageFactory"/>), spelled <c>image</c>.</summary>
    Image = 2,
    /// <summary>A render pass (<see cref="IGpuRenderPassFactory.Create"/>), spelled <c>render-pass</c>.</summary>
    RenderPass = 3,
    /// <summary>A framebuffer (<see cref="IGpuRenderPassFactory.CreateFramebuffer"/>), spelled
    /// <c>framebuffer</c>.</summary>
    Framebuffer = 4,
    /// <summary>A shader module (<see cref="IGpuShaderModuleFactory"/>), spelled <c>shader-module</c>.</summary>
    ShaderModule = 5,
    /// <summary>A command pool (<see cref="IGpuCommandPoolFactory"/>), spelled <c>command-pool</c>.</summary>
    CommandPool = 6,
    /// <summary>A descriptor pool (<see cref="IGpuBindings.CreatePool"/>), spelled <c>bindings-pool</c>.</summary>
    BindingsPool = 7,
}
