using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// The capture of the published output: a surface format read back as it is, any other image through the display encode.
public sealed partial class ShaderPipelineRenderNode {
    // A capture armed after a selection reads that selection: while its preview builds, the published image is still the
    // previous selection's, so the capture waits for the frame that publishes the new one. A published image no surface
    // carries (a float working image) is captured through the display encode's SDR, which waits while its pipeline
    // builds and moves the image out of the layout it was published in to sample it, and back.
    private void CaptureIfPending() {
        if (m_previewRequest is not null) {
            return;
        }
        if (
            (m_capture.PendingPath is not null) &&
            m_lastSurface.IsSameDeviceImage &&
            !Surface.IsSurfaceFormat(format: m_lastSurface.Format) &&
            !(m_encoder ??= new SurfaceEncoder(
                device: m_device,
                directX: m_directX,
                owner: m_name,
                pipelines: m_pipelines
            )).IsReady
        ) {
            return;
        }

        m_capture.Serve(
            failureLabel: "[capture] failed",
            tick: m_publishedStateTick,
            writer: (m_captureWriter ??= path => {
                m_capturePng.ThrowIfUnavailable(path: path);
                if (
                    m_lastSurface.IsEmpty ||
                    !m_lastSurface.IsSameDeviceImage
                ) {
                    throw new InvalidOperationException(message: "A completed same-device output is required for capture.");
                }
                var format = m_lastSurface.Format;
                ReadOnlyMemory<byte> pixels;

                if (Surface.IsSurfaceFormat(format: format)) {
                    m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback();

                    // The readback sizes its staging buffer to the surface it reads, replacing one of another size.
                    m_readbackBytes = ReadbackBytes(
                        height: m_lastSurface.Height,
                        width: m_lastSurface.Width
                    );
                    pixels = m_readback.Read(
                        m_lastSurface.ImageHandle,
                        format,
                        m_lastSurface.Width,
                        m_lastSurface.Height,
                        4,
                        m_publishedLayout
                    );
                } else {
                    pixels = m_encoder!.ReadSdr(
                        height: m_lastSurface.Height,
                        image: m_lastSurface.ImageHandle,
                        imageView: m_lastSurface.ImageViewHandle,
                        layout: m_publishedLayout,
                        width: m_lastSurface.Width
                    );
                    m_readbackBytes = m_encoder.OwnedBytes;
                }

                if (!m_capturePng.TryWrite(
                    height: ((int)m_lastSurface.Height),
                    path: path,
                    rgba: pixels,
                    width: ((int)m_lastSurface.Width)
                )) {
                    throw new NotSupportedException(message: "PNG capture is unavailable.");
                }

                Console.Error.WriteLine(value: $"[capture] {m_name} -> {path}");
            })
        );
    }
}
