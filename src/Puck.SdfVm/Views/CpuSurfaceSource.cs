using Puck.Abstractions.Capture;
using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.SdfVm.Views;

/// <summary>
/// Wraps per-frame CPU pixels as a screen source: it uploads a producer's B8G8R8A8/R8G8B8A8 frame to an
/// <see cref="IGpuSurfaceUpload"/>-backed image (the upload object recreates the image on a dimension/format change)
/// and exposes the resulting stable view handle as a <see cref="Func{T}"/> a <see cref="GuestSurfaceView"/> samples.
/// The input seam takes either a whole <see cref="Surface"/> — the CPU-pixel variant an
/// <see cref="IFrameCaptureSource"/> produces — or a raw pixel buffer a fill-a-buffer producer hands over, so a
/// capture session and a procedural pattern connect through the same adapter.
/// <para>
/// This adapter never reads back or converts a GPU/shared-handle surface: it assumes CPU pixels are already in hand. A
/// non-CPU or empty frame keeps the last published handle rather than clearing the screen.
/// </para>
/// </summary>
public sealed class CpuSurfaceSource : IDisposable {
    private IGpuSurfaceUpload? m_upload;
    private Func<nint>? m_asSource;
    private nint m_handle;
    private bool m_disposed;

    /// <summary>Gets the current image-view handle (valid until the next publish or this object's disposal); 0 before
    /// the first successful publish.</summary>
    public nint CurrentHandle => m_handle;

    /// <summary>Gets a stable delegate that returns the <see cref="CurrentHandle"/> on each call — the
    /// <c>Func&lt;nint&gt;</c> a <see cref="GuestSurfaceView"/> resolves against. The same delegate instance is
    /// returned every time, so it may be captured once at registration.</summary>
    public Func<nint> AsSource => (m_asSource ??= (() => m_handle));

    /// <summary>Publishes a CPU-pixel <see cref="Surface"/> (an <see cref="IFrameCaptureSource"/> capture). A surface
    /// that is not the CPU-pixel variant, or has a zero extent, keeps the last handle.</summary>
    /// <param name="deviceContext">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    /// <param name="surface">The captured frame; its <see cref="Surface.Pixels"/> are uploaded.</param>
    /// <returns>The published image-view handle (the current handle when nothing was uploaded).</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public nint Publish(IGpuDeviceContext deviceContext, IGpuComputeServices gpu, in Surface surface) {
        if (!surface.IsCpuPixels || (0 == surface.Width) || (0 == surface.Height)) {
            return m_handle;
        }

        return Publish(
            deviceContext: deviceContext,
            gpu: gpu,
            pixels: surface.Pixels,
            width: surface.Width,
            height: surface.Height,
            format: surface.Format
        );
    }

    /// <summary>Publishes a raw tightly packed pixel buffer (a fill-a-buffer producer's frame). An empty buffer or a
    /// zero extent keeps the last handle.</summary>
    /// <param name="deviceContext">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    /// <param name="pixels">The tightly packed pixels, in <paramref name="format"/> order, rows without padding.</param>
    /// <param name="width">The frame width in pixels.</param>
    /// <param name="height">The frame height in pixels.</param>
    /// <param name="format">The presentable pixel format the buffer is laid out in.</param>
    /// <returns>The published image-view handle (the current handle when nothing was uploaded).</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public nint Publish(IGpuDeviceContext deviceContext, IGpuComputeServices gpu, ReadOnlyMemory<byte> pixels, uint width, uint height, SurfaceFormat format) {
        ArgumentNullException.ThrowIfNull(argument: deviceContext);
        ArgumentNullException.ThrowIfNull(argument: gpu);

        if (m_disposed || pixels.IsEmpty || (0 == width) || (0 == height)) {
            return m_handle;
        }

        m_upload ??= gpu.SurfaceTransferFactory.CreateUpload(deviceContext: deviceContext);
        // The upload object owns the returned handle and recreates its image on a dimension/format change, so a
        // varying capture extent needs no manual reallocation here.
        m_handle = m_upload.Upload(
            deviceContext: deviceContext,
            format: GpuPixelFormats.FromSurfaceFormat(format: format),
            height: height,
            pixels: pixels,
            width: width
        );

        return m_handle;
    }

    /// <summary>Pulls one frame from a capture source and publishes it — the convenience seam for driving a
    /// <see cref="IFrameCaptureSource"/> each tick. A source with no frame this call keeps the last handle.</summary>
    /// <param name="source">The capture source to pull.</param>
    /// <param name="deviceContext">The GPU device context to upload on.</param>
    /// <param name="gpu">The neutral GPU compute services (resolves the upload factory).</param>
    /// <returns>The published image-view handle (the current handle when no frame was captured).</returns>
    /// <exception cref="ArgumentNullException">A required argument is <see langword="null"/>.</exception>
    public nint Capture(IFrameCaptureSource source, IGpuDeviceContext deviceContext, IGpuComputeServices gpu) {
        ArgumentNullException.ThrowIfNull(argument: source);

        return (source.TryCapture(surface: out var surface)
            ? Publish(deviceContext: deviceContext, gpu: gpu, surface: in surface)
            : m_handle);
    }

    /// <summary>Drops the GPU upload after a device loss: the next publish rebuilds it on the fresh device.</summary>
    public void NotifyDeviceLost() {
        m_upload?.Dispose();
        m_upload = null;
        m_handle = 0;
    }

    /// <inheritdoc/>
    public void Dispose() {
        if (m_disposed) {
            return;
        }

        m_disposed = true;
        m_upload?.Dispose();
        m_upload = null;
    }
}
