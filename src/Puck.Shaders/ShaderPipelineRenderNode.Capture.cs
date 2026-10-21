using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// The capture of the published output: a surface format read back as it is, any other image through the display encode.
public sealed partial class ShaderPipelineRenderNode {
    /// <summary>Polls a pending capture against the last published image without rendering another sample.</summary>
    public void PollCapture() => CaptureIfPending();
    /// <summary>Reads back an image the display presents this frame for a presented capture
    /// (<see cref="FrameCaptureMode.Presented"/>): a surface format as it is, any other image through the display
    /// encode's SDR. The read waits for the device to finish the image's submissions.</summary>
    /// <param name="image">The presented image, a same-device image the frame's submissions have written.</param>
    /// <param name="layout">The layout the image was published in, which the read leaves it in.</param>
    /// <param name="rgba">Returns a copy of the pixels, four bytes a pixel, rows top to bottom, which the caller owns;
    /// empty when the read waits.</param>
    /// <returns><see langword="false"/> while the display encode an image of another format needs is building.</returns>
    /// <exception cref="InvalidOperationException"><paramref name="image"/> is empty or not a same-device image.</exception>
    /// <exception cref="DeviceLostException">The read lost the graphics device.</exception>
    public bool TryReadPresented(in Surface image, GpuImageLayout layout, out byte[] rgba) {
        rgba = [];

        if (
            image.IsSameDeviceImage &&
            !Surface.IsSurfaceFormat(format: image.Format) &&
            !EncoderReady()
        ) {
            return false;
        }

        rgba = ReadPixels(image: in image, layout: layout).ToArray();

        return true;
    }
    /// <summary>Writes one presented frame's PNG (<see cref="FrameCaptureRequest.Present"/>) and closes it before
    /// returning; it runs on the thread pool.</summary>
    /// <param name="path">The PNG path.</param>
    /// <param name="rgba">The pixels, four bytes a pixel.</param>
    /// <param name="width">The width, in pixels.</param>
    /// <param name="height">The height, in pixels.</param>
    /// <exception cref="NotSupportedException">The PNG encoder is unavailable.</exception>
    public void EncodePresented(string path, byte[] rgba, int width, int height) {
        m_capturePng.ThrowIfUnavailable(path: path);
        _ = (m_capturePng.TryWrite(height: height, path: path, rgba: rgba, width: width)
            ? true
            : throw new NotSupportedException(message: "PNG capture is unavailable."));
    }

    // A capture armed after a selection reads that selection: while its preview builds, the published image is still the
    // previous selection's, so the capture waits for the frame that publishes the new one. It likewise waits past an image
    // rendered at an extent the node was not asked for (ShownAtItsExtent). A published image no surface
    // carries (a float working image) is captured through the display encode's SDR, which waits while its pipeline
    // builds and moves the image out of the layout it was published in to sample it, and back.
    private void CaptureIfPending() {
        if (
            (m_previewRequest is not null) ||
            !m_publishedAtRequestedExtent
        ) {
            return;
        }
        if (
            (m_capture.PendingPath is not null) &&
            m_lastSurface.IsSameDeviceImage &&
            !Surface.IsSurfaceFormat(format: m_lastSurface.Format) &&
            !EncoderReady()
        ) {
            return;
        }

        m_capture.Serve(
            failureLabel: "[capture] failed",
            frame: m_publishedFrame,
            tick: m_publishedStateTick,
            writer: (m_captureWriter ??= path => {
                m_capturePng.ThrowIfUnavailable(path: path);

                var pixels = ReadPixels(image: in m_lastSurface, layout: m_publishedLayout);

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
    // Whether the display encode an image of a non-surface format is read through has built, starting its build.
    private bool EncoderReady() => (m_encoder ??= new SurfaceEncoder(
        device: m_device,
        directX: m_directX,
        owner: m_name,
        pipelines: m_pipelines
    )).IsReady;
    // Reads a completed same-device image back as RGBA8: a surface format through the node's readback, which sizes its
    // staging buffer to the image, any other through the display encode's SDR. The memory is valid until the next read.
    private ReadOnlyMemory<byte> ReadPixels(in Surface image, GpuImageLayout layout) {
        if (
            image.IsEmpty ||
            !image.IsSameDeviceImage
        ) {
            throw new InvalidOperationException(message: "A completed same-device output is required for capture.");
        }

        if (!Surface.IsSurfaceFormat(format: image.Format)) {
            var encoded = m_encoder!.ReadSdr(
                height: image.Height,
                image: image.ImageHandle,
                imageView: image.ImageViewHandle,
                layout: layout,
                width: image.Width
            );

            m_readbackBytes = m_encoder.OwnedBytes;

            return encoded;
        }

        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback();
        m_readbackBytes = ReadbackBytes(
            height: image.Height,
            width: image.Width
        );

        return m_readback.Read(
            image.ImageHandle,
            image.Format,
            image.Width,
            image.Height,
            4,
            layout
        );
    }
}
