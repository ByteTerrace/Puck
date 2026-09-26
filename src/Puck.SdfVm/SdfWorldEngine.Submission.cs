using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    /// <summary>Gets the layout each view's output image rests in between frames once the engine has rendered it:
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

    /// <summary>Reads view 0's output back from the GPU (tightly packed RGBA8, row-major, at the view's render extent,
    /// <see cref="OutputWidth"/> by <see cref="OutputHeight"/>). The returned memory is the readback's reusable staging
    /// view — copy it before the next frame if it must outlive one.</summary>
    /// <returns>View 0's output pixels.</returns>
    /// <exception cref="InvalidOperationException">No frame has rendered view 0.</exception>
    public ReadOnlyMemory<byte> ReadPixels() {
        var output = ((m_viewOutputs[0] is { Initialized: true } rendered)
            ? rendered
            : throw new InvalidOperationException(message: "No frame has rendered view 0."));

        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback();

        return m_readback.Read(
            bytesPerPixel: 4,
            format: Format,
            height: output.Height,
            sourceImageHandle: output.Image.ImageHandle,
            sourceLayout: OutputLayout,
            width: output.Width
        );
    }
    /// <summary>Renders one frame — every view's set, sky through views, in a single submit — against the uploaded
    /// program, waits for completion, and returns view 0's RGBA readback. The deterministic harness path (validation
    /// stages, headless renders).</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <returns>View 0's output, tightly packed RGBA8, row-major, at its render extent.</returns>
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
        addWaits: null,
        frame: frame,
        onFrameSlotAvailable: null
    );

    // The live node's leased screen sources: it retires and adopts them in the exact frame-ring fence interval
    // (onFrameSlotAvailable), and adds the waits their images carry to the one submission that samples them (addWaits,
    // called with the submitter immediately before it).
    internal void SubmitFrameWithExternalResources(SdfFrame frame, Action<int> onFrameSlotAvailable, Action<IGpuQueueSubmitter> addWaits) => SubmitFrameCore(
        addWaits: addWaits,
        frame: frame,
        onFrameSlotAvailable: onFrameSlotAvailable
    );

    private void SubmitFrameCore(SdfFrame frame, Action<int>? onFrameSlotAvailable, Action<IGpuQueueSubmitter>? addWaits) {
        // Publishes the newest frame whose fence has already signaled; the slot-fence wait in PrepareFrame completes
        // the frame FrameRingSize back regardless.
        m_work.Poll();

        var viewportCount = PrepareFrame(
            frame: frame,
            onFrameSlotAvailable: onFrameSlotAvailable
        );

        Record(viewportCount: viewportCount);
        addWaits?.Invoke(obj: m_gpu.QueueSubmitter);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle],
            fence: m_frameFences[m_currentSlot]
        );
        m_exportableImage?.FinalizeForExport();
    }

    /// <summary>Gets the exported image's shared NT handle (zero-copy cross-backend present); 0 outside export mode.</summary>
    public nint ExportSharedHandle => (m_exportableImage?.SharedHandle ?? 0);
    /// <summary>Gets the native image handle of view 0's output image, or zero before a frame has sized it. After a
    /// frame, the image rests in the <see cref="GpuImageLayout.ShaderReadOnly"/> layout (or the cross-backend
    /// <see cref="GpuImageLayout.External"/> layout in export mode) — a downstream pass may transition it and read it in
    /// place, zero-copy.</summary>
    public nint OutputImageHandle => (m_viewOutputs[0]?.Image.ImageHandle ?? 0);
    /// <summary>Gets the native image-view handle of view 0's output image (for binding it as a source in a downstream
    /// descriptor set), or zero before a frame has sized it.</summary>
    public nint OutputImageViewHandle => (m_viewOutputs[0]?.Image.ImageViewHandle ?? 0);
    /// <summary>Gets the width in pixels of view 0's output image, its render extent, or zero before a frame has sized
    /// it.</summary>
    public uint OutputWidth => (m_viewOutputs[0]?.Width ?? 0);
    /// <summary>Gets the height in pixels of view 0's output image, or zero before a frame has sized it.</summary>
    public uint OutputHeight => (m_viewOutputs[0]?.Height ?? 0);
}
