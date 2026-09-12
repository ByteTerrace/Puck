using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

internal static class DirectXGpuFormats {
    internal static DXGI_FORMAT ToDxgiFormat(GpuPixelFormat gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        GpuPixelFormat.R16G16B16A16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        GpuPixelFormat.R32G32B32A32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: gpuPixelFormat,
            message: null,
            paramName: nameof(gpuPixelFormat)
        ),
    };
    // The three GpuImageLayout cases every caller agrees map to a fixed D3D12_RESOURCE_STATES value. A caller
    // supplies its own behavior for anything else (Undefined included) — a fallback state or a thrown exception.
    internal static bool TryToResourceState(GpuImageLayout layout, out D3D12_RESOURCE_STATES resourceState) {
        switch (layout) {
            case GpuImageLayout.ShaderReadOnly:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_PIXEL_SHADER_RESOURCE;
                return true;
            case GpuImageLayout.External:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COMMON;
                return true;
            case GpuImageLayout.General:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_UNORDERED_ACCESS;
                return true;
            case GpuImageLayout.RenderTarget:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_RENDER_TARGET;
                return true;
            default:
                resourceState = default;
                return false;
        }
    }
}
