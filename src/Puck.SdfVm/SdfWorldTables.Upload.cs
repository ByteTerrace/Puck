using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The upload: one fenced submission a frame, recorded by the residency's staging before any pass records, ahead of every
// view's submission. It takes the next ring slot, waits the previous upload's fence, which signals once that upload and
// every submission queued before it have finished, the views that read this slot two uploads earlier among them, but not
// the views deferred after it (its fence answers from a signal of its own), flushes the slot's share of every
// region, and records the fillers' first transitions, the regions' copies and the world's shadow layers (SdfWorldShadows),
// each handing what it wrote to every shader stage that reads it in a later submission. The graph's
// environment producer runs afterward, once its panorama images have been acquired.
public sealed partial class SdfWorldTables {
    // Device objects retained through pending carries and then the device queue's completed prefix.
    private readonly GpuRetirementQueue<IDisposable> m_retired = new();

    // One-based submitted uploads known complete through the existing previous-upload wait. A required geometry
    // upload older than this prefix never consults a fence whose ring slot may since have been reused for poses/GI.
    private ulong m_completedUploads;

    /// <summary>Gets the ring slot the latest upload wrote, whose region buffers the frame's passes bind, or -1 before the
    /// first upload.</summary>
    public int CurrentSlot => m_currentSlot;

    internal IGpuQueueSubmitter BackgroundQueue => m_deviceContext.Services.QueueSubmitter;

    /// <summary>Submits the frame's upload: waits the previous upload, flushes and copies what every region owes into
    /// the next ring slot, then submits it fenced. Nothing
    /// here waits for the upload itself: the views' submissions that follow it on the queue read what it wrote.</summary>
    /// <exception cref="ObjectDisposedException">The tables are disposed.</exception>
    public void SubmitUpload() {
        OpenUpload();
        RecordUpload();
        CloseUpload();
    }

    // The ring slot the upload before the open one wrote, which the open upload's recording copies the previous tables
    // from (OpenUpload, RecordUpload).
    private int m_uploadPreviousSlot = -1;
    // Whether the open upload's recording carries copies the mesh region owed (SdfMeshRegionStore.RecordOwed), which
    // its submission reports to the store.
    private bool m_meshStoreCopies;

    /// <summary>Opens the frame's upload on the frame thread: waits the previous upload and takes the next ring slot.
    /// <see cref="RecordUpload"/> then records it and <see cref="CloseUpload"/> submits it (or <see cref="QueueUpload"/>,
    /// <see cref="HandOffUpload"/> and <see cref="PublishUpload"/> do, for uploads handed over together), in that order,
    /// once each.</summary>
    /// <exception cref="ObjectDisposedException">The tables are disposed.</exception>
    internal void OpenUpload() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Upload);

        // Publishes the newest upload whose fence has already signaled.
        m_work.Poll();

        var slot = ((int)(m_uploads % FrameRingSize));

        // The previous upload's fence follows, on the queue, the views that read this slot an upload earlier. A batch
        // answers an upload's fence from its own hand-over's signal (GpuQueueWork.OwnsSignal), never from the views
        // deferred behind it, so this waits for the frame before the previous one and not for the frame just handed to
        // the GPU. Waiting completes it in the ledger before another upload reuses its ring slot.
        if (m_uploads > 0UL) {
            m_frameFences[((int)((m_uploads - 1UL) % FrameRingSize))].Wait();
            m_completedUploads = m_uploads;
        }
        // Every earlier upload is complete, the slot's last among them: what its shadow selections read back is ready.
        m_shadows?.Completed(slot: slot);
        // The flush below rewrites the slot's buffers, which a background job that leased the slot may still read.
        m_gpuWork.EndLeases(ringSlot: slot);
        m_uploadPreviousSlot = m_currentSlot;
        m_currentSlot = slot;

        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[slot].CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
        // The outer debug-marker group scoping this residency's upload (see DebugLabel); pixel-neutral.
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: DebugLabel
        );
        // The presentation's mesh region records what it owes that no landing job carries (a compaction's moves, a
        // grown region's carry) ahead of this residency's passes, whichever residency's plan owed it. Invariant: the
        // upload that carries the copies reaches the queue before every pass that reads the moved words, this residency's
        // shadows and every view of the frame included. It is enforced here: uploads open on the frame thread in the
        // order they are submitted (SdfWorldPasses.RendersScheduled opens and closes its fork in one order, and a
        // residency staged alone opens, records and submits in turn), and the store's gate hands the copies to the first
        // upload that opens after they were owed, so no upload submitted earlier draws a mesh at words a later one fills.
        // Never record them in RecordUpload, which runs side by side with other residencies' recordings in no order.
        m_meshStoreCopies = m_meshStore.RecordOwed(client: this, commandBuffer: commandBuffer, readers: RegionReaders, recorder: recorder);
        // The probe scenes' words ride the same door, under the same invariant: a probe job of any residency recorded
        // after this upload opens reads a region whose words and growth this upload carries, and no client publishes
        // a root until the upload that carried it has completed (SdfMeshProbeScenes.Upload.Rooted).
        m_probeSceneCopies = (m_meshStore.ProbeScenesIfCreated?.RecordOwed(commandBuffer: commandBuffer, readers: GpuStage.ComputeShader, recorder: recorder) ?? false);
    }
    // Whether the open upload's recording carries the probe scenes' owed copies (SdfMeshProbeScenes.RecordOwed).
    private bool m_probeSceneCopies;
    /// <summary>Gets the probe trace's scene words this residency's lighting shares with every residency attached to its
    /// mesh region, or <see langword="null"/> once that region is gone.</summary>
    internal SdfMeshProbeScenes? ProbeScenes => m_meshStore.ProbeScenes;
    /// <summary>Records the open upload into its ring slot's command list: the fillers' first transitions, the regions'
    /// copies, the lighting's and the world's shadow layers. It touches only these tables and the device's recorder, so
    /// it runs on whichever thread holds the tables between <see cref="OpenUpload"/> and <see cref="CloseUpload"/>.</summary>
    internal void RecordUpload() {
        var slot = m_currentSlot;
        var previousSlot = m_uploadPreviousSlot;
        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[slot].CommandBufferHandle;

        // OpenUpload began the command list and recorded the mesh region's owed copies into it.
        if (m_fillersInitialized) {
            m_work.SkipPass(pass: FillersPass);
        } else {
            m_work.EnterPass(pass: FillersPass);
            InitializeFillers(commandBuffer: commandBuffer);
            m_work.LeavePass();
        }
        m_work.EnterPass(pass: UploadPass);
        var keepsMotion = KeepsMotion;

        if (!keepsMotion) {
            RecordPreviousTables(commandBuffer: commandBuffer, previousSlot: previousSlot);
        }
        RecordRegionCopies();
        RecordMeshIrradiance(command: commandBuffer);
        if (!keepsMotion) {
            CompletePreviousTables(commandBuffer: commandBuffer);
        }
        m_work.LeavePass();
        RecordSkyEnvironmentDecision();
        // After the region copies, which hand the mesh region and transforms to the vertex stage the shadows draw with.
        RecordShadows(commandBuffer: commandBuffer, slot: slot);
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
    }
    /// <summary>Submits the recorded upload fenced, hands it to the queue and publishes it to the tables' readers, on the
    /// frame thread: <see cref="QueueUpload"/>, the hand-off, then <see cref="PublishUpload"/>.</summary>
    internal void CloseUpload() {
        QueueUpload();
        HandOffUpload();
        PublishUpload();
    }
    /// <summary>Submits the recorded upload fenced, on the frame thread. In an open batch the submission is deferred, and
    /// its fence reads as unsignaled until the batch hands it to the queue, so a caller that queues several residencies'
    /// uploads hands them over together, by one split, before it publishes any (<see cref="PublishUpload"/>).</summary>
    internal void QueueUpload() {
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Upload);
        var slot = m_currentSlot;

        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [m_commandPools[slot].CommandBufferHandle],
            fence: m_frameFences[slot]
        );
    }
    /// <summary>Hands the queued upload to the queue if its batch has not yet (<see cref="IGpuSubmissionFence.HandOff"/>),
    /// with every submission deferred before it: the first of a frame's uploads to hand off carries the others queued
    /// ahead of the frame's views in one execute.</summary>
    internal void HandOffUpload() {
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Upload);
        m_frameFences[m_currentSlot].HandOff();
    }
    /// <summary>Publishes the queued upload to the tables' readers once it has reached the queue.</summary>
    /// <exception cref="InvalidOperationException">The upload is still deferred in an open batch.</exception>
    internal void PublishUpload() {
        var slot = m_currentSlot;

        // Invariant: nothing the upload writes is published (the shared mesh store's compaction, which another residency's
        // views read, the shadows' slot, the upload count the background work binds against) before the upload reached
        // the queue. Enforced here, the one door to publication: a deferred upload is refused. The fence stays armed for
        // retirement; reaching the queue waits for no GPU completion.
        if (!m_frameFences[slot].ReachedQueue) {
            throw new InvalidOperationException(message: "An upload is published only once it has reached the queue; hand it off first.");
        }
        m_shadows?.Submitted(slot: slot);
        m_uploads = checked((m_uploads + 1UL));
        if (m_meshStoreCopies) { m_meshStoreCopies = false; m_meshStore.SubmittedOwed(); }
        if (m_probeSceneCopies) { m_probeSceneCopies = false; m_meshStore.ProbeScenesIfCreated?.SubmittedOwed(); }
        m_uploadedBindingRevision = m_bindingRevision;
        m_backgroundProgram = m_liveProgram;
        ServiceRetirements();
    }

    // Poll only at submission boundaries: all recorded uses have been submitted, except an explicitly retained carry.
    private void Retire(IDisposable replaced, GpuRegion? carryingRegion = null) =>
        m_retired.Hold(item: replaced, pendingUse: carryingRegion?.PendingCarry);
    private void ServiceRetirements() {
        if (m_retired.Count > 0) {
            m_retired.Poll(timeline: m_gpu.QueueSubmitter.Timeline, release: static replaced => replaced.Dispose());
        }
    }

    // A background client's buffer can be a source of the frame upload. Use the same retirement door as tables.
    /// <summary>Retains a replaced background resource through pending copies and the device timeline.</summary>
    /// <param name="replaced">The object to dispose after its last submitted use.</param>
    public void RetireBackgroundResource(GpuRegion replaced) {
        var source = replaced.Buffer(slot: 0);

        m_retired.Hold(item: replaced,
            pendingUse: () => (ReferenceEquals(objA: m_meshIrradianceSource, objB: source) && (m_meshIrradianceSlots.Count > 0)));
    }

    // Device teardown after drain/loss releases even unsubmitted carries.
    private void DisposeRetired(ulong completed) => m_retired.Release(completed: completed, release: static replaced => replaced.Dispose());
    // Moves the fillers from their first, undefined layout to the ones a pass binds them in, once, before any pass can
    // bind them: the sampled filler shader-readable and the storage filler General, both cleared, so a screen whose source
    // is missing from a frame samples black.
    private void InitializeFillers(nint commandBuffer) {
        var recorder = m_gpu.Recorder;

        foreach (var filler in ((ReadOnlySpan<IGpuImage>)[m_sampledFiller, m_storageFiller])) {
            recorder.TransitionImageLayout(
                commandBufferHandle: commandBuffer,
                destinationAccessMask: GpuAccess.TransferWrite,
                destinationStageMask: GpuStage.Transfer,
                imageHandle: filler.ImageHandle,
                newLayout: GpuImageLayout.General,
                oldLayout: GpuImageLayout.Undefined,
                sourceAccessMask: GpuAccess.None,
                sourceStageMask: GpuStage.TopOfPipe
            );
            recorder.ClearStorageImage(
                commandBufferHandle: commandBuffer,
                format: Format,
                imageHandle: filler.ImageHandle
            );
        }

        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_sampledFiller.ImageHandle,
            newLayout: GpuImageLayout.ShaderReadOnly,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        recorder.TransitionImageLayout(
            commandBufferHandle: commandBuffer,
            destinationAccessMask: GpuAccess.ShaderRead | GpuAccess.ShaderWrite,
            destinationStageMask: GpuStage.ComputeShader,
            imageHandle: m_storageFiller.ImageHandle,
            newLayout: GpuImageLayout.General,
            oldLayout: GpuImageLayout.General,
            sourceAccessMask: GpuAccess.TransferWrite,
            sourceStageMask: GpuStage.Transfer
        );
        m_fillersInitialized = true;
    }
}
