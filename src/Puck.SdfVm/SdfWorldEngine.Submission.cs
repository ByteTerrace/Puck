using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Gets the layout the output image rests in between frames once the engine has produced one:
    /// <see cref="GpuImageLayout.ShaderReadOnly"/>, for a same-device consumer to sample, or
    /// <see cref="GpuImageLayout.External"/> in export mode. A consumer that samples the output declares this layout and
    /// hands the image back in it.</summary>
    public GpuImageLayout OutputLayout => (m_exportMode
        ? GpuImageLayout.External
        : GpuImageLayout.ShaderReadOnly
    );

    // Waits until every in-flight ring frame has retired — the drain the rare SHARED-resource rewrites (program
    // upload, glyph-atlas re-upload) pay so they never race a submitted frame. A no-op when nothing is outstanding.
    private void WaitForFrameRing() {
        foreach (var fence in m_frameFences) {
            fence.Wait();
        }
    }

    /// <summary>Reads the composited output back from the GPU (tightly packed RGBA8, row-major). The returned memory
    /// is the readback's reusable staging view — copy it before the next frame if it must outlive one.</summary>
    /// <returns>The composited output pixels.</returns>
    public ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback();

        return m_readback.Read(
            bytesPerPixel: 4,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            sourceLayout: OutputLayout,

            width: m_width
        );
    }
    /// <summary>Renders one frame — beam → cull-args → views (indirect) → composite in a single submit — against the
    /// uploaded program, waits for completion, and returns the composited RGBA readback. The deterministic harness
    /// path (validation stages, headless renders).</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <returns>The composited output, tightly packed RGBA8, row-major.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    public byte[] RenderFrame(SdfFrame frame) {
        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.SubmitAndWait(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle]
        );

        return ReadPixels().ToArray();
    }
    /// <summary>Records and submits one frame fire-and-forget — the live node path. The submit arms the current ring
    /// slot's fence: nothing waits here, and the only wait a later frame pays is that slot fence in
    /// <c>PrepareFrame</c>, <see cref="FrameRingSize"/> frames later — so a pipelining host overlaps this frame's GPU
    /// execution with the next frame's CPU production. In export mode the consumer lives on another backend with no
    /// shared timeline, so this does drain the producer queue (<see cref="IGpuExportableImage.FinalizeForExport"/>)
    /// before the shared handle is handed off.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    public void SubmitFrame(SdfFrame frame) => SubmitFrameCore(
        frame: frame,
        onFrameSlotAvailable: null
    );

    // The live node's additive external-resource seam: keep the longstanding public SubmitFrame signature intact,
    // while letting it retire/adopt leased screen sources in the exact frame-ring fence interval.
    internal void SubmitFrameWithExternalResources(SdfFrame frame, Action<int> onFrameSlotAvailable) => SubmitFrameCore(
        frame: frame,
        onFrameSlotAvailable: onFrameSlotAvailable
    );

    private void SubmitFrameCore(SdfFrame frame, Action<int>? onFrameSlotAvailable) {
        // Publishes the newest frame whose fence has already signaled; the slot-fence wait in PrepareFrame completes
        // the frame FrameRingSize back regardless.
        m_work.Poll();

        var viewportCount = PrepareFrame(
            frame: frame,
            onFrameSlotAvailable: onFrameSlotAvailable
        );

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle],
            fence: m_frameFences[m_currentSlot]
        );
        m_exportableImage?.FinalizeForExport();
    }

    /// <summary>Gets the exported image's shared NT handle (zero-copy cross-backend present); 0 outside export mode.</summary>
    public nint ExportSharedHandle => (m_exportableImage?.SharedHandle ?? 0);
    /// <summary>Gets the native image handle of the composited output image. After a frame, the image rests in the
    /// <see cref="GpuImageLayout.ShaderReadOnly"/> layout (or the cross-backend <see cref="GpuImageLayout.External"/>
    /// layout in export mode) — a downstream pass may transition it and read it in place, zero-copy.</summary>
    public nint OutputImageHandle => m_storageImage.ImageHandle;
    /// <summary>Gets the native image-view handle of the composited output image (for binding it as a source in a
    /// downstream descriptor set).</summary>
    public nint OutputImageViewHandle => m_storageImage.ImageViewHandle;
}
