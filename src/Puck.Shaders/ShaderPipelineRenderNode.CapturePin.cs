using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// A capture served without rendering, from a published image another instance owns. A node whose package drew nothing
// publishes its input's image in its output's place, an image in its owner's frame-slot ring. The owner renders into
// every slot of that ring again within a few frames, and the ring cannot step past an image a capture still needs: each
// storage has one instance per frame slot, the slot is the frame counter modulo the ring, a previous-frame read is the
// slot before it, and the descriptor sets every slot binds come from a pool admitted at install, so no extra slot can
// appear at frame time. A capture the node serves without rendering this frame (paused, or waiting for a build)
// therefore reads a copy: its host offers the image the node's output stands for this frame (OfferCaptureSource), and
// the node copies it into an image of its own in a submission of its own, publishes the copy, and serves the capture
// from it, however many frames the readback waits for its encoder. The copy is held like any image a replaced graph
// published, and retires once a later render displaces it.
public sealed partial class ShaderPipelineRenderNode {
    private ShaderPipelineExternalImage m_captureSource;
    private ulong? m_captureSourceTick;
    private bool m_captureSourceOffered;

    /// <summary>Offers the image the node's output stands for now, for a pending capture the node serves this frame without
    /// rendering while it publishes another instance's image in its output's place (<see cref="PublishedBinding"/>). The
    /// node copies the offered image into one of its own before serving, so the capture reads the pixels of the frame it
    /// was served on, never pixels the owner wrote into the same image later. An offer the frame does not take is
    /// retired with the frame's leases.</summary>
    /// <param name="image">The image, in the layout its owner left it in.</param>
    /// <param name="lease">The host's lease on the image, held until the copy's submission has finished.</param>
    /// <param name="tick">The simulation tick the owner rendered the image from, which the capture records.</param>
    internal void OfferCaptureSource(ShaderPipelineExternalImage image, GpuImageLease lease, ulong? tick) {
        m_frameLeases.Hold(lease: in lease);
        m_captureSource = image;
        m_captureSourceTick = tick;
        m_captureSourceOffered = true;
    }

    // Gets the simulation tick of the frame that rendered the published image, or null when none did.
    internal ulong? PublishedStateTick => m_publishedStateTick;

    // Before a capture is served without rendering: publishes a copy of the offered image in place of another instance's
    // image the node published, so the capture reads pixels its owner can no longer overwrite. A frame with no offer
    // touches nothing.
    private void PinStandingCaptureIfOffered() {
        if (m_captureSourceOffered) {
            PinStandingCapture();
        }
    }
    private void PinStandingCapture() {
        m_captureSourceOffered = false;

        if (
            (m_capture.PendingPath is null) ||
            (m_publishedBinding is null) ||
            (m_frame == 0)
        ) {
            return;
        }

        var source = m_captureSource;

        WaitAll();
        HoldLeases();

        var slot = ((int)((m_frame - 1) % m_inFlight));
        var copy = m_gpu.ImageFactory.Create(
            format: source.Format,
            height: source.Height,
            name: new GpuObjectName(
                owner: m_name,
                part: "capture-copy"
            ),
            usage: GpuImageUsage.Sampled,
            width: source.Width
        );

        m_held.Add(item: new HeldImage(
            Bytes: ImageBytes(
                format: source.Format.ToString(),
                height: source.Height,
                width: source.Width
            ),
            Handle: copy.ImageHandle,
            Image: copy
        ));

        var recorder = m_gpu.Recorder;
        var commands = m_commands;
        var command = BeginFrameCommands(slot: slot);

        commands.Clear();
        recorder.TransitionImageLayout(command, source.ImageHandle, source.Layout, GpuImageLayout.TransferSource, GpuAccess.ShaderRead, GpuAccess.TransferRead, ShaderStages, GpuStage.Transfer);
        recorder.TransitionImageLayout(command, copy.ImageHandle, GpuImageLayout.Undefined, GpuImageLayout.TransferDestination, GpuAccess.None, GpuAccess.TransferWrite, GpuStage.TopOfPipe, GpuStage.Transfer);
        recorder.CopyImage(command, source.ImageHandle, copy.ImageHandle, source.Width, source.Height);
        recorder.TransitionImageLayout(command, copy.ImageHandle, GpuImageLayout.TransferDestination, m_outputLayout, GpuAccess.TransferWrite, GpuAccess.ShaderRead, GpuStage.Transfer, ShaderStages);
        recorder.TransitionImageLayout(command, source.ImageHandle, GpuImageLayout.TransferSource, source.Layout, GpuAccess.TransferRead, GpuAccess.ShaderRead, GpuStage.Transfer, ShaderStages);
        recorder.EndCommandBuffer(commandBufferHandle: command);
        commands.Add(item: command);
        SubmitCounted(
            commands: commands,
            fence: m_slots[slot].Fence!
        );
        m_frameLeases.MoveTo(destination: m_slots[slot].Leases);
        m_latestSlot = slot;
        m_lastSurface = Surface.SameDeviceImage(
            copy.ImageHandle,
            copy.ImageViewHandle,
            copy.Width,
            copy.Height,
            copy.Format
        );
        m_publishedBinding = null;
        m_publishedLayout = m_outputLayout;
        m_publishedStateTick = m_captureSourceTick;
    }
}
