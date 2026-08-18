using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

public sealed partial class SdfWorldEngine {
    // The single-in-flight guard shared by all three submission paths: a pipelined frame's fence is still outstanding,
    // so re-recording the one shared command buffer (any submit path) would corrupt the in-flight work. Drain it with
    // AcquireFramePixels first.
    private void ThrowIfPipelinedFrameInFlight() {
        if (m_pipelinedFrameInFlight) {
            throw new InvalidOperationException(message: "A pipelined preview frame is still in flight on this engine; complete it with AcquireFramePixels before submitting another frame (SubmitFramePipelined must not be interleaved with RenderFrame or SubmitFrame on one engine).");
        }
    }
    // Waits until every in-flight ring frame has retired — the drain the rare SHARED-resource rewrites (program
    // upload, glyph-atlas re-upload) pay so they never race a pipelined frame. A no-op when nothing is outstanding.
    private void WaitForFrameRing() {
        foreach (var fence in m_frameFences) {
            fence.Wait();
        }
    }

    /// <summary>Collects the pixels the outstanding <see cref="SubmitFramePipelined"/> produced (call only once
    /// <see cref="IsFramePixelsReady"/> is <see langword="true"/>), and clears the single-in-flight guard so the next
    /// pipelined (or waited) frame may be submitted. The returned memory is the readback's reusable staging view —
    /// copy it before the next submit if it must outlive one.</summary>
    /// <returns>The composited output pixels, tightly packed RGBA8, row-major.</returns>
    public ReadOnlyMemory<byte> AcquireFramePixels() {
        var pixels = m_readback!.MapPixels();

        m_pipelinedFrameInFlight = false;

        return pixels;
    }
    /// <summary>Polls, without blocking, whether the outstanding <see cref="SubmitFramePipelined"/>'s readback has
    /// landed. Fail-safe on a torn-down device (returns <see langword="false"/>, never throws into the render loop).</summary>
    /// <returns>Whether the pipelined frame's pixels are ready to <see cref="AcquireFramePixels"/>.</returns>
    public bool IsFramePixelsReady() =>
        (m_readback?.IsReadComplete() ?? false);
    /// <summary>Reads the composited output back from the GPU (tightly packed RGBA8, row-major). The returned memory
    /// is the readback's reusable staging view — copy it before the next frame if it must outlive one.</summary>
    /// <returns>The composited output pixels.</returns>
    public ReadOnlyMemory<byte> ReadPixels() {
        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);

        return m_readback.Read(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            width: m_width
        );
    }
    /// <summary>Renders one frame — beam → cull-args → views (indirect) → composite in a single submit — against the
    /// uploaded program, waits for completion, and returns the composited RGBA readback. The deterministic harness
    /// path (validation stages, headless renders). Must not be called while a <see cref="SubmitFramePipelined"/> frame
    /// is outstanding on this engine (it would re-record the one shared command buffer under a live fence).</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <returns>The composited output, tightly packed RGBA8, row-major.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is still in flight on this engine.</exception>
    public byte[] RenderFrame(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.SubmitAndWait(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle],
            deviceContext: m_deviceContext
        );

        // The wait above completed this frame's pool, so its marks are readable immediately. m_frameTimingActive was
        // latched by Record; the ring index only advances on timed frames so the pool selection stays consistent.
        if (m_frameTimingActive) {
            Span<ulong> ticks = stackalloc ulong[((int)TimingMarkCount)];
            var pool = m_timingPools![((int)(m_timingFrame % ((ulong)TimingPoolCount)))];

            m_lastFrameGpuMilliseconds = ((m_timingRecorder!.ReadTimestamps(
                deviceHandle: m_deviceHandle,
                firstQuery: 0,
                poolHandle: pool.PoolHandle,
                queryCount: TimingMarkCount,
                rawTicks: ticks
            ) < TimingMarkCount)
                ? null
                : m_timingCapabilities.TicksToMilliseconds(
                    startTicks: ticks[0],
                    endTicks: ticks[(((int)TimingMarkCount) - 1)]
                )
            );

            m_timingFrame++;
        }

        return ReadPixels().ToArray();
    }
    /// <summary>Records and submits one frame fire-and-forget — the live node path. The submit arms the current ring
    /// slot's fence: nothing waits here, and the only wait a later frame pays is that slot fence in
    /// <c>PrepareFrame</c>, <see cref="FrameRingSize"/> frames later — so a pipelining host overlaps this frame's GPU
    /// execution with the next frame's CPU production. In export mode the consumer lives on another backend with no
    /// shared timeline, so this does drain the producer queue (<see cref="IGpuExportableStorageImage.FinalizeForExport"/>)
    /// before the shared handle is handed off.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is still in flight on this engine.</exception>
    public void SubmitFrame(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle],
            deviceContext: m_deviceContext,
            fence: m_frameFences[m_currentSlot]
        );
        m_exportableImage?.FinalizeForExport();

        // The ring index advances only on TIMED frames (Record latched m_frameTimingActive), so disarmed frames leave
        // the last timed frame's pool readable and the N−FrameRingSize readback contract intact across arm/disarm gaps.
        if (m_frameTimingActive) {
            m_timingFrame++;
        }
    }
    /// <summary>Records and submits one frame fire-and-forget, then issues a non-blocking fenced readback of the
    /// composited output — the demo bake-preview path. Neither the compute submit nor the readback copy waits: the
    /// caller polls <see cref="IsFramePixelsReady"/> on a later produced frame and, once it is ready, collects the
    /// pixels with <see cref="AcquireFramePixels"/>. This spreads the render + readback across produced frames so the
    /// live in-editor preview never idles the shared present queue mid-sculpt. Only one pipelined
    /// frame may be outstanding at a time, and this path must not be interleaved with <see cref="RenderFrame"/> or
    /// <see cref="SubmitFrame"/> on one engine (all three re-record the single shared command buffer) — mixing them
    /// while a fence is live corrupts the in-flight submission.</summary>
    /// <param name="frame">The per-frame data: views (cameras + regions), time, and the dynamic entity transforms.</param>
    /// <exception cref="ArgumentNullException"><paramref name="frame"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">The frame has zero views or more than the provisioned capacity.</exception>
    /// <exception cref="InvalidOperationException">A pipelined preview frame is already in flight on this engine.</exception>
    public void SubmitFramePipelined(SdfFrame frame) {
        ThrowIfPipelinedFrameInFlight();

        var viewportCount = PrepareFrame(frame: frame);

        Record(viewportCount: viewportCount);
        // Fire-and-forget compute submit (the SAME fenced call SubmitFrame uses), then the fenced-but-unwaited
        // readback copy. The readback lives on this engine and tracks its own single outstanding fence; the timing
        // path is not driven here (this path is preview-only and never constructed with a timing pool).
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [m_commandPools[m_currentSlot].CommandBufferHandle],
            deviceContext: m_deviceContext,
            fence: m_frameFences[m_currentSlot]
        );
        m_readback ??= m_gpu.SurfaceTransferFactory.CreateReadback(deviceContext: m_deviceContext);
        m_readback.SubmitRead(
            bytesPerPixel: 4,
            deviceContext: m_deviceContext,
            format: Format,
            height: m_height,
            sourceImageHandle: m_storageImage.ImageHandle,
            width: m_width
        );
        m_pipelinedFrameInFlight = true;
    }

    /// <summary>Gets whether the engine renders into an exportable image (cross-backend handoff layout + shared handle).</summary>
    public bool ExportMode => m_exportMode;
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
