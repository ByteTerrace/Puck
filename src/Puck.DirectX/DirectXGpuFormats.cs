using Puck.Abstractions.Presentation;
using Windows.Win32.Graphics.Direct3D12;
using Windows.Win32.Graphics.Dxgi.Common;

namespace Puck.DirectX;

/// <summary>Maps backend-neutral <see cref="GpuPixelFormat"/> values to their <c>DXGI_FORMAT</c> equivalents, a
/// <see cref="DisplayColorSpace"/> to its <c>DXGI_COLOR_SPACE_TYPE</c>, and image layouts to resource states.</summary>
public static class DirectXGpuFormats {
    /// <summary>Converts a <see cref="DisplayColorSpace"/> to the <c>DXGI_COLOR_SPACE_TYPE</c> a swap chain presents
    /// it in.</summary>
    /// <param name="colorSpace">The backend-neutral color space.</param>
    /// <returns>The corresponding <c>DXGI_COLOR_SPACE_TYPE</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="colorSpace"/> is not defined.</exception>
    public static DXGI_COLOR_SPACE_TYPE ToDxgiColorSpace(DisplayColorSpace colorSpace) => colorSpace switch {
        DisplayColorSpace.Srgb => DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G22_NONE_P709,
        DisplayColorSpace.Hdr10 => DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G2084_NONE_P2020,
        DisplayColorSpace.ScRgb => DXGI_COLOR_SPACE_TYPE.DXGI_COLOR_SPACE_RGB_FULL_G10_NONE_P709,
        _ => throw new ArgumentOutOfRangeException(
            actualValue: colorSpace,
            message: "The display color space is not defined.",
            paramName: nameof(colorSpace)
        ),
    };
    /// <summary>Converts a <see cref="GpuPixelFormat"/> to its <c>DXGI_FORMAT</c> value.</summary>
    /// <param name="gpuPixelFormat">The backend-neutral pixel format.</param>
    /// <returns>The corresponding <c>DXGI_FORMAT</c>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="gpuPixelFormat"/> is not defined.</exception>
    public static DXGI_FORMAT ToDxgiFormat(GpuPixelFormat gpuPixelFormat) => gpuPixelFormat switch {
        GpuPixelFormat.R8G8B8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM,
        GpuPixelFormat.B8G8R8A8Unorm => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
        GpuPixelFormat.R16G16B16A16Float => DXGI_FORMAT.DXGI_FORMAT_R16G16B16A16_FLOAT,
        GpuPixelFormat.R32G32B32A32Float => DXGI_FORMAT.DXGI_FORMAT_R32G32B32A32_FLOAT,
        GpuPixelFormat.D32Float => DXGI_FORMAT.DXGI_FORMAT_D32_FLOAT,
        GpuPixelFormat.Bc4Unorm => DXGI_FORMAT.DXGI_FORMAT_BC4_UNORM,
        GpuPixelFormat.Bc5Unorm => DXGI_FORMAT.DXGI_FORMAT_BC5_UNORM,
        GpuPixelFormat.Bc6hUfloat => DXGI_FORMAT.DXGI_FORMAT_BC6H_UF16,
        GpuPixelFormat.Bc7Unorm => DXGI_FORMAT.DXGI_FORMAT_BC7_UNORM,
        GpuPixelFormat.R8G8B8A8Srgb => DXGI_FORMAT.DXGI_FORMAT_R8G8B8A8_UNORM_SRGB,
        GpuPixelFormat.B8G8R8A8Srgb => DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM_SRGB,
        GpuPixelFormat.R10G10B10A2Unorm => DXGI_FORMAT.DXGI_FORMAT_R10G10B10A2_UNORM,
        GpuPixelFormat.R8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8_UNORM,
        GpuPixelFormat.R8G8Unorm => DXGI_FORMAT.DXGI_FORMAT_R8G8_UNORM,
        _ => throw new ArgumentOutOfRangeException(
        actualValue: gpuPixelFormat,
        message: null,
        paramName: nameof(gpuPixelFormat)
    ),
    };

    // The concrete GpuImageLayout cases share a fixed D3D12_RESOURCE_STATES value. A caller
    // supplies its own behavior for anything else (Undefined included) — a fallback state or a thrown exception.
    internal static bool TryToResourceState(GpuImageLayout layout, out D3D12_RESOURCE_STATES resourceState) {
        switch (layout) {
            case GpuImageLayout.ShaderReadOnly:
                resourceState = DirectXResourceStates.ShaderRead;
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
            case GpuImageLayout.DepthAttachment:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_DEPTH_WRITE;
                return true;
            case GpuImageLayout.TransferSource:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_SOURCE;
                return true;
            case GpuImageLayout.TransferDestination:
                resourceState = D3D12_RESOURCE_STATES.D3D12_RESOURCE_STATE_COPY_DEST;
                return true;
            default:
                resourceState = default;
                return false;
        }
    }
}
