using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

/// <summary>
/// A presenter's read of a surface back to CPU pixels, which both backends' presenters share: a surface that already
/// holds CPU pixels is handed back, a working image no capture sink reads is encoded in SDR (<see cref="SurfaceEncoder"/>)
/// first, and any other GPU surface is imported when it carries a shared handle and read back
/// (<see cref="SurfaceReadbackCapture"/>). The encoder, importer and readback are armed by the first read that needs each,
/// and <see cref="Release"/> returns them before the device they live on goes away or is recreated.
/// </summary>
public sealed class PresentationCapture {
    private readonly IGpuDeviceContext m_device;
    private readonly bool m_directX;
    private readonly GpuPassPipelineCache m_pipelines;

    private SurfaceEncoder? m_encoder;
    private IGpuSurfaceImport? m_import;
    private IGpuSurfaceReadback? m_readback;

    /// <summary>Initializes a new instance of the <see cref="PresentationCapture"/> class.</summary>
    /// <param name="device">The presenter's device, whose services create the lazily armed readback, encoder and
    /// shared-surface importer.</param>
    /// <param name="pipelines">The composition's pass pipelines, which a capture's display encode is an entry of.</param>
    /// <param name="directX">Whether the device is a Direct3D 12 device, which reads the encode's DXIL rather than its
    /// SPIR-V.</param>
    /// <exception cref="ArgumentNullException"><paramref name="device"/> or <paramref name="pipelines"/> is
    /// <see langword="null"/>.</exception>
    public PresentationCapture(IGpuDeviceContext device, GpuPassPipelineCache pipelines, bool directX) {
        ArgumentNullException.ThrowIfNull(argument: device);
        ArgumentNullException.ThrowIfNull(argument: pipelines);

        m_device = device;
        m_directX = directX;
        m_pipelines = pipelines;
    }

    /// <summary>Reads a surface back to CPU pixels.</summary>
    /// <param name="surface">The surface to read.</param>
    /// <returns>The surface itself when it is empty or already holds CPU pixels of a surface format, otherwise a
    /// CPU-pixel surface of the same extent.</returns>
    /// <exception cref="IOException">The display encode's deployed bytecode is missing or cannot be read.</exception>
    public Surface ReadSurface(Surface surface) {
        if (
            surface.IsEmpty ||
            (surface.IsCpuPixels && Surface.IsSurfaceFormat(format: surface.Format))
        ) {
            return surface;
        }
        // A working image no capture sink reads is captured through the display encode's SDR.
        if (
            (surface.IsSameDeviceImage || surface.IsCpuPixels) &&
            !Surface.IsSurfaceFormat(format: surface.Format)
        ) {
            return (m_encoder ??= new SurfaceEncoder(
                device: m_device,
                directX: m_directX,
                owner: "presentation-capture",
                pipelines: m_pipelines
            )).ReadSurface(surface: surface);
        }

        return SurfaceReadbackCapture.ReadSurface(
            captureImport: ref m_import,
            captureReadback: ref m_readback,
            deviceContext: m_device,
            surface: surface
        );
    }
    /// <summary>Disposes the encoder, readback and importer a read armed; a later read arms them again.</summary>
    public void Release() {
        m_encoder?.Dispose();
        m_encoder = null;
        m_readback?.Dispose();
        m_readback = null;
        m_import?.Dispose();
        m_import = null;
    }
}
