using System.Runtime.Versioning;
using Puck.DirectX.Interop;
using Windows.Win32.Graphics.Direct3D12;

namespace Puck.DirectX;

/// <summary>
/// Names a Direct3D 12 device's objects with <c>ID3D12Object::SetName</c>, so the debug layer's messages
/// (<c>[d3d12-debug]</c> lines) and its live-object report at teardown print each object's name. It is on only when the
/// device is created with the debug layer. It names buffers, images, pipeline states, command allocators and command
/// lists; Direct3D 12 image views, descriptor pools, descriptor sets and render passes are not objects there, so their
/// names are dropped.
/// </summary>
/// <param name="deviceContext">The device context whose debug-layer choice turns naming on.</param>
[SupportedOSPlatform("windows10.0.10240")]
public sealed unsafe class DirectXGpuObjectNaming(DirectXDeviceContext deviceContext) : GpuObjectNaming {
    /// <inheritdoc/>
    public override bool IsEnabled => deviceContext.EnableDebugLayer;

    /// <inheritdoc/>
    protected override void Apply(GpuObjectKind kind, nint handle, string name) {
        switch (kind) {
            case GpuObjectKind.Buffer:
            case GpuObjectKind.CommandBuffer:
            case GpuObjectKind.CommandPool:
            case GpuObjectKind.Image:
            case GpuObjectKind.Pipeline:
                break;
            default:
                return;
        }

        fixed (char* text = name) {
            // A name is a diagnostic: a refusal leaves the object unnamed and the creation standing.
            ((ID3D12Object*)handle)->SetName(Name: text);
        }
    }
}
