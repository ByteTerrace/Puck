using Puck.Abstractions.Gpu;

namespace Puck.SdfVm;

// The upload: one fenced submission a frame, recorded by the residency's staging before any pass records, ahead of every view's submission. It takes the next ring slot, waits the previous upload's fence, which signals once every submission queued
// before it has finished, the views that read this slot two uploads earlier among them, flushes the slot's share of every
// region, and records the fillers' first transitions and the regions' copies, each handing what it wrote to every shader stage that reads it in a later submission. The graph's
// environment producer runs afterward, once its panorama images have been acquired.
public sealed partial class SdfWorldTables {
    // Device objects the tables replaced without waiting for the device, each with the upload after which it is disposed.
    private readonly GpuRetirementQueue<IDisposable> m_retired = new();
    // One-based submitted uploads known complete through the existing previous-upload wait. A required geometry
    // upload older than this prefix never consults a fence whose ring slot may since have been reused for poses/GI.
    private ulong m_completedUploads;

    /// <summary>Gets the ring slot the latest upload wrote, whose region buffers the frame's passes bind, or -1 before the
    /// first upload.</summary>
    public int CurrentSlot => m_currentSlot;
    /// <summary>Gets the generation a background job taken now reads: the uploads submitted so far. Such a job binds what
    /// the latest of them sent, in <see cref="CurrentSlot"/>, so it may read any object the tables retire at or after
    /// this generation and none retired before it; the job leases the generation until its fence
    /// (<see cref="SdfGpuWork.OldestLease"/>).</summary>
    public ulong BackgroundGeneration => m_uploads;

    /// <summary>Submits the frame's upload: waits the previous upload, flushes and copies what every region owes into
    /// the next ring slot, then submits it fenced. Nothing
    /// here waits for the upload itself: the views' submissions that follow it on the queue read what it wrote.</summary>
    /// <exception cref="ObjectDisposedException">The tables are disposed.</exception>
    public void SubmitUpload() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        using var attribution = GpuQueueWork.Attribute(category: GpuQueueCategory.Upload);

        // Publishes the newest upload whose fence has already signaled.
        m_work.Poll();

        var slot = ((int)(m_uploads % FrameRingSize));

        // Waiting an upload completes it in the ledger before another upload reuses its ring slot.
        if (m_uploads > 0UL) {
            m_frameFences[((int)((m_uploads - 1UL) % FrameRingSize))].Wait();
            m_completedUploads = m_uploads;
            DisposeRetired(completed: (m_uploads - 1UL));
        }
        // The flush below rewrites the slot's buffers, which a background job that leased the slot may still read.
        m_gpuWork.EndLeases(ringSlot: slot);

        var previousSlot = m_currentSlot;

        m_currentSlot = slot;

        var recorder = m_gpu.Recorder;
        var commandBuffer = m_commandPools[slot].CommandBufferHandle;

        recorder.BeginCommandBuffer(commandBufferHandle: commandBuffer);
        // The outer debug-marker group scoping this residency's upload (see DebugLabel); pixel-neutral.
        recorder.BeginDebugGroup(
            commandBufferHandle: commandBuffer,
            label: DebugLabel
        );
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
        RecordMeshIrradiance(commandBuffer);
        if (!keepsMotion) {
            CompletePreviousTables(commandBuffer: commandBuffer);
        }
        m_work.LeavePass();
        RecordSkyEnvironmentDecision();
        recorder.EndDebugGroup(commandBufferHandle: commandBuffer);
        recorder.EndCommandBuffer(commandBufferHandle: commandBuffer);
        m_gpu.QueueSubmitter.Submit(
            commandBufferHandles: [commandBuffer],
            fence: m_frameFences[slot]
        );
        // Publish submission only after the fence has been armed by the queue. Before this point an initially
        // signaled fence must not satisfy geometry staged for the next upload.
        m_uploads = checked(m_uploads + 1UL);
        // The flush above staged the moved meshes' words.
        ReleaseUploadNeeds();
        m_uploadedBindingRevision = m_bindingRevision;
        m_backgroundProgram = m_liveProgram;
    }

    // Hands a device object the tables replaced to the upload that follows: GPU work recorded before it may still read
    // the object, and that upload may read it once more (a grown region's carry), so it is disposed once that upload's
    // fence signals, which one queue signals only after every earlier submission, every view that bound it among them.
    private void Retire(IDisposable replaced) => m_retired.Retire(after: m_uploads, item: replaced);
    /// <summary>Retires a device object a background client replaced, through the tables' own retirement door: it is
    /// disposed once the upload that follows has completed and no background job that could read it is in flight.</summary>
    /// <param name="replaced">The replaced object, disposed by the tables.</param>
    /// <exception cref="ArgumentNullException"><paramref name="replaced"/> is <see langword="null"/>.</exception>
    public void RetireBackgroundResource(IDisposable replaced) {
        ArgumentNullException.ThrowIfNull(argument: replaced);
        Retire(replaced: replaced);
    }
    // The one door a retired object is disposed through: every replaced object whose following upload has completed and
    // that no background job in flight leased. A job taken at generation g may read whatever was retired at or after g
    // (SdfGpuWork.OldestLease), so nothing from the oldest lease on is released; disposal of the tables themselves
    // passes the largest point, after the service has drained its jobs.
    private void DisposeRetired(ulong completed) {
        var leased = m_gpuWork.OldestLease();

        m_retired.Release(completed: ((leased == ulong.MaxValue) ? completed : Math.Min(val1: completed, val2: (leased - 1UL))), release: static replaced => replaced.Dispose());
    }
    // Waits until every upload in flight has retired: what a rewrite of the only table no view reads, a bake request,
    // pays.
    private void WaitForUploads() {
        foreach (var fence in m_frameFences) {
            fence.Wait();
        }
        m_completedUploads = m_uploads;
    }
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
