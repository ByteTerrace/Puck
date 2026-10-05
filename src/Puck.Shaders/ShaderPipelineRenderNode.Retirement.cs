using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// Retiring what an install or a selection replaces, without draining the device and without waiting for submissions a
// paused node never makes. The replaced objects have two kinds of reader.
//
// The node's own submissions read all of them, and those reads end with the node's latest submission. One queue runs
// submissions in order, so the replaced objects retire once that submission has completed: at once when it already has,
// which is always true of a paused node whose last frame finished, so replacements made while paused never pile up.
//
// A downstream consumer reads a published surface or named buffer: the latest frame, or, one frame late, the one before
// it. Each owned allocation behind those publications is taken out of the replaced objects and held, alone, while it
// is still one of them. A newer publication displaces it; it then retires once the node's second submission after that has
// completed, because every reader that could still sample it submitted its work before then.
public sealed partial class ShaderPipelineRenderNode {
    // How many of the node's own submissions after an output stops being published precede the one whose completion
    // retires it. A host replacing a whole node retires it by the same count of its successor's submissions.
    internal const long RetirementLag = 2;

    private readonly List<HeldResource> m_held = [];
    private readonly List<RetiredGraph> m_retired = [];

    private IGpuSubmissionFence? m_lastSubmissionFence;
    private Surface m_previousSurface;
    // The fence of the node's first submission since its device objects were last released, until that submission is
    // seen completed. A slot's fence is re-armed only after the node waits on it, so a wait on this fence also completes
    // the first submission.
    private IGpuSubmissionFence? m_firstSubmission;
    private bool m_completedSubmission;

    /// <summary>Gets whether a submission the node made since its device objects were last released has completed on
    /// the GPU: its fence has signaled or the node has waited on it. A node that has submitted nothing, or whose first
    /// submission is still in flight, reads <see langword="false"/>, as does one whose device was lost before the
    /// submission was seen completed.</summary>
    public bool HasCompletedSubmission {
        get {
            if (
                !m_completedSubmission &&
                (m_firstSubmission is { } first)
            ) {
                try {
                    if (first.IsSignaled) {
                        NoteWaited(fence: first);
                    }
                } catch (DeviceLostException) {
                    // The frame path meets the loss and releases the node; nothing it submitted counts as completed.
                }
            }

            return m_completedSubmission;
        }
    }
    /// <summary>Gets the bytes of every GPU resource the node owns: the installed graph with its preview, replaced
    /// objects waiting for the GPU to finish with them, published images and buffers held from a replaced graph, finite read-epoch
    /// copies, and the staging
    /// buffer the capture readback holds once a capture has been served, plus package and timestamp readback buffers.</summary>
    public ulong OwnedBytes {
        get {
            var bytes = checked(AllocationBytes + m_readbackBytes + TimingReadbackBytes + PackageReadbackBytes(m_passes));

            foreach (var retired in m_retired) {
                bytes = checked((bytes + retired.Bytes));
            }
            foreach (var held in m_held) {
                bytes = checked((bytes + held.Bytes));
            }

            return bytes;
        }
    }

    private bool IsPublished(nint imageHandle) =>
        (
            (imageHandle != 0) &&
            (
                (imageHandle == m_lastSurface.ImageHandle) ||
                (imageHandle == m_previousSurface.ImageHandle)
            )
        );
    // The bytes the replaced objects still own once any held image has been taken out of them, counted from the objects
    // themselves: the same kinds ShaderPipelineRenderNode.Budget.cs counts from the plan.
    private static ulong LiveBytes(RuntimePass[] passes, RuntimeResource[] resources, PreviewPass? preview) {
        var bytes = checked((preview?.LiveBytes() ?? 0UL) + PackageReadbackBytes(passes));

        foreach (var pass in passes) {
            if (pass?.GeometryBuffer is { } geometry) {
                bytes = checked((bytes + geometry.SizeBytes));
            }
            if (pass?.KernelCounters is { } counters) {
                bytes = checked((bytes + counters.TotalBytes));
            }
            foreach (var region in ((ReadOnlySpan<GpuRegion?>)[pass?.PassRegion, pass?.FrameRegion])) {
                if (region is not null) {
                    bytes = checked((bytes + (((ulong)region.ByteCount) * ((ulong)region.SlotCount))));
                }
            }
            foreach (var region in (pass?.RowRegions ?? []).Select(selector: static row => row.Region).Concat(second: (pass?.Regions ?? []))) {
                if (region is not null) {
                    bytes = checked((bytes + GpuRegion.BytesOf(
                        byteCount: region.ByteCount,
                        policy: region.Policy,
                        slotCount: region.SlotCount
                    )));
                }
            }
        }

        foreach (var resource in resources) {
            if (resource is null) {
                continue;
            }

            if ((resource.Spec.Kind != ShaderPipelineResourceKind.Buffer) && (resource.Images is { } images)) {
                foreach (var image in images) {
                    if (image is not null) {
                        bytes = checked((bytes + ImageBytes(
                            format: resource.Spec.Format,
                            height: image.Height,
                            width: image.Width
                        )));
                    }
                }
            }
            if (!resource.Borrowed && (resource.Buffers is { } buffers)) {
                foreach (var buffer in buffers) {
                    if (buffer is not null) {
                        bytes = checked((bytes + buffer.SizeBytes));
                    }
                }
            }
            if (resource.Export is { } export) {
                bytes = checked((bytes + ImageBytes(
                    format: resource.Spec.Format,
                    height: export.Height,
                    width: export.Width
                )));
            }
        }

        return bytes;
    }
    // Takes the image behind a published surface out of replaced objects, when one of them owns it, and holds it.
    private void Hold(nint imageHandle, RuntimeResource[] resources, PreviewPass? preview) {
        if (imageHandle == 0) {
            return;
        }

        foreach (var held in m_held) {
            if ((held.Kind != ShaderPipelineResourceKind.Buffer) && (held.Handle == imageHandle)) {
                return;
            }
        }

        foreach (var resource in resources) {
            if (resource is null) {
                continue;
            }

            if (resource.Images is { } images) {
                for (var slot = 0; (slot < images.Length); slot++) {
                    if ((images[slot] is { } image) && (image.ImageHandle == imageHandle)) {
                        images[slot] = null!;
                        m_held.Add(item: new HeldResource(
                            Bytes: ImageBytes(
                                format: resource.Spec.Format,
                                height: image.Height,
                                width: image.Width
                            ),
                            Handle: imageHandle,
                            Resource: image
                        ));

                        return;
                    }
                }
            }
        }

        if (preview?.TakeTarget(imageHandle: imageHandle) is { } previewTarget) {
            m_held.Add(item: new HeldResource(
                Bytes: ((((ulong)previewTarget.Width) * previewTarget.Height) * 4UL),
                Handle: imageHandle,
                Resource: previewTarget
            ));
        }
    }
    private bool IsPublishedBuffer(nint handle) {
        foreach (var output in m_publishedBuffers) {
            if (output.Buffer?.BufferHandle == handle) { return true; }
        }
        foreach (var output in m_previousBuffers) {
            if (output.Buffer?.BufferHandle == handle) { return true; }
        }
        return false;
    }
    private IGpuBuffer[] PublishedBorrowedBuffersOf(RuntimeResource[] resources) {
        var published = new List<IGpuBuffer>();
        foreach (var resource in resources) {
            if (resource is not { Borrowed: true, Buffers: { } buffers }) { continue; }
            foreach (var buffer in buffers) {
                if (buffer is not null && IsPublishedBuffer(buffer.BufferHandle) &&
                    !published.Contains(buffer, ReferenceEqualityComparer.Instance)) { published.Add(buffer); }
            }
        }
        return [.. published];
    }
    // A matching external handle conveys no ownership. Only an installed package-borrowed allocation has a
    // recorder/build that retains this exact buffer's owner under the BorrowedBuffer contract.
    private bool InstalledRetainsBorrowedBuffer(IGpuBuffer buffer) {
        foreach (var resource in m_resources) {
            if (resource is not { Borrowed: true, Buffers: { } buffers }) { continue; }
            foreach (var installed in buffers) {
                if (ReferenceEquals(installed, buffer)) { return true; }
            }
        }
        return false;
    }
    // Detaches an owned published ring slot once, even when several output names share the same allocation.
    // Borrowed buffers remain the package owner's responsibility and never enter node-owned held bytes.
    private void HoldBuffers(RuntimeResource[] resources) {
        foreach (var resource in resources) {
            if (resource is null || resource.Borrowed || resource.Buffers is not { } buffers) { continue; }
            for (var slot = 0; slot < buffers.Length; slot++) {
                if (buffers[slot] is not { } buffer || !IsPublishedBuffer(buffer.BufferHandle)) { continue; }
                var handle = buffer.BufferHandle;
                var heldAlready = false;
                foreach (var held in m_held) {
                    if (held.Kind == ShaderPipelineResourceKind.Buffer && held.Handle == handle) { heldAlready = true; break; }
                }
                buffers[slot] = null!;
                if (!heldAlready) {
                    m_held.Add(new HeldResource(Handle: handle, Resource: buffer, Bytes: buffer.SizeBytes,
                        Kind: ShaderPipelineResourceKind.Buffer));
                }
            }
        }
    }
    // Holds a published image another instance owns (a package that drew nothing stands for it) under a lease of the
    // node's own while it is the image the node publishes: however the owner
    // retires, a capture the node serves from it without rendering reads a live image.
    private void HoldPublication(in Surface surface) {
        if (
            (m_publishedBinding is null) ||
            (m_images is null) ||
            (surface.ImageHandle == 0)
        ) {
            return;
        }

        foreach (var held in m_held) {
            if ((held.Kind != ShaderPipelineResourceKind.Buffer) && (held.Handle == surface.ImageHandle)) {
                return;
            }
        }

        if (m_images.TryLease(
            imageHandle: surface.ImageHandle,
            lease: out var lease
        )) {
            m_held.Add(item: new HeldResource(
                Bytes: 0UL,
                Handle: surface.ImageHandle,
                Resource: new HeldBindingRetirement(lease: lease),
                Leased: true
            ));
        }
    }
    // Makes a surface the published one. Image and buffer publications advance together for rendered frames; an owned
    // allocation outside both completed publications retires once every reader could have submitted.
    private void Publish(Surface surface) {
        if (surface.ImageHandle != m_lastSurface.ImageHandle) {
            m_previousSurface = m_lastSurface;
        }

        m_lastSurface = surface;
        // A render publishes, and a reset owes its own initialization frame, so neither is still missing a lost image.
        m_publicationLost = false;
        HoldPublication(surface: in surface);

        for (var index = (m_held.Count - 1); (index >= 0); index--) {
            var held = m_held[index];

            // Readers resolve another instance's image to its owner and lease it there, so the node holds that image only
            // while it is the one the node publishes now, which a capture it serves without rendering reads.
            if (held.Epoch is { IsActive: true } || (held.Leased
                ? (held.Handle == m_lastSurface.ImageHandle)
                : ((held.Kind == ShaderPipelineResourceKind.Buffer)
                    ? IsPublishedBuffer(held.Handle) : IsPublished(imageHandle: held.Handle)))) {
                continue;
            }

            m_held.RemoveAt(index: index);

            if (held.Leased) {
                // Every submission that reads this image holds its own lease, including this node's. Dropping the
                // publication needs no future submission, which a paused node may never make.
                held.Resource.Dispose();

                continue;
            }

            m_retired.Add(item: new RetiredGraph(
                afterSubmission: (m_submissions + RetirementLag),
                bytes: held.Bytes,
                fence: null,
                allocation: held.Resource,
                passes: [],
                preview: null,
                resources: []
            ));
        }
        foreach (var retired in m_retired) { retired.ReleasePublication(this); }
    }
    // Retires objects an install or a selection replaced: published images and owned buffer slots are held, and the
    // rest is disposed once the node's latest submission has completed, which may already be true.
    private void Retire(RuntimePass[] passes, RuntimeResource[] resources, PreviewPass? preview) {
        if (
            (passes.Length == 0) &&
            (resources.Length == 0) &&
            (preview is null)
        ) {
            return;
        }

        // Read before anything is moved, so a device loss reported here leaves the replaced objects whole.
        var inFlight = (m_lastSubmissionFence is { IsSignaled: false });

        Hold(
            imageHandle: m_lastSurface.ImageHandle,
            preview: preview,
            resources: resources
        );
        Hold(
            imageHandle: m_previousSurface.ImageHandle,
            preview: preview,
            resources: resources
        );

        HoldBuffers(resources);
        // A borrowed allocation stays owned by its package. Keep the old recorder/build that retains that owner
        // while a published frame still names it without an installed retaining owner. Actual displacement then
        // retires through the same reader lag as detached owned slots.
        var publishedBorrowedBuffers = PublishedBorrowedBuffersOf(resources);
        var holdsBorrowedPublication = publishedBorrowedBuffers.Any(buffer => !InstalledRetainsBorrowedBuffer(buffer));
        var retired = new RetiredGraph(
            afterSubmission: m_submissions,
            bytes: LiveBytes(
                passes: passes,
                preview: preview,
                resources: resources
            ),
            fence: holdsBorrowedPublication ? null : m_lastSubmissionFence,
            allocation: null,
            passes: passes,
            preview: preview,
            resources: resources,
            publishedBorrowedBuffers: holdsBorrowedPublication ? publishedBorrowedBuffers : []
        );

        if (inFlight || holdsBorrowedPublication) {
            m_retired.Add(item: retired);
        } else {
            retired.Dispose(node: this);
        }
        foreach (var held in m_retired) { held.ReleasePublication(this); }
    }

    /// <summary>Gets whether the node holds anything of a graph: an installed or building graph's objects, its frame slots'
    /// command buffers and fences, a preview, a readback or objects waiting to retire. A node that has never been demanded,
    /// or whose graph <see cref="ReleaseUnnamed"/> released, holds none.</summary>
    internal bool HoldsGraphObjects {
        get {
            foreach (var slot in m_slots) {
                if (
                    (slot.Fence is not null) ||
                    (slot.Commands is not null)
                ) {
                    return true;
                }
            }

            return (
                m_build.IsPending ||
                (m_preview is not null) ||
                (m_readback is not null) ||
                (m_encoder is not null) ||
                (m_passes.Length != 0) ||
                (m_resources.Length != 0) ||
                (m_retired.Count != 0) ||
                (m_held.Count != 0)
            );
        }
    }

    /// <summary>Releases the graph of a node nothing names any more: its targets, history, buffers, descriptor sets, frame
    /// slots, preview and readback, and any build in flight. The host calls it only once the device has finished every
    /// submission that may read them, the node's own and its consumers'. The node keeps its installed pipeline, the regions
    /// and bindings its host gave it, and its region-copy pipeline, and rebuilds at the extent it is demanded at the next
    /// time it renders, with fresh history, as after a device loss. A capture armed on it is left armed.</summary>
    internal void ReleaseUnnamed() {
        CancelBuilds();
        ReleaseGraph();
    }

    /// <summary>Gets how many submissions the node has made.</summary>
    internal long SubmissionCount => m_submissions;
    /// <summary>Gets the fence of the node's latest submission, or <see langword="null"/> when it has made none since its
    /// device objects were last released.</summary>
    internal IGpuSubmissionFence? LatestSubmission => m_lastSubmissionFence;

    /// <summary>Releases a node its host has replaced, without draining the device. The host calls it only once a
    /// submission ordered after every reader of the node's objects has completed: on the one queue, that is the
    /// successor's <see cref="RetirementLag"/>th submission after the replacement, the same rule the node applies to an
    /// image it stops publishing.</summary>
    internal void DisposeRetired() {
        if (m_disposed) {
            return;
        }
        m_disposed = true;
        m_capture.Refuse(error: new ObjectDisposedException(objectName: nameof(ShaderPipelineRenderNode)));
        Release(wait: false);
    }

    // Retires every queued object whose retiring submission has completed.
    private void RetireCompleted() {
        for (var index = (m_retired.Count - 1); (index >= 0); index--) {
            var retired = m_retired[index];

            if (retired.Fence is { IsSignaled: true }) {
                retired.Dispose(node: this);
                m_retired.RemoveAt(index: index);
            }
        }
    }
    // Arms every queued retirement whose retiring submission is the one just made with that submission's fence.
    private void ArmRetirements(IGpuSubmissionFence fence) {
        foreach (var retired in m_retired) {
            if (
                !retired.HoldsBorrowedPublication &&
                (retired.Fence is null) &&
                (m_submissions >= retired.AfterSubmission)
            ) {
                retired.Fence = fence;
            }
        }
    }
    // Releases everything queued or held without waiting: the caller has drained the device, or lost it.
    private void ReleaseRetired() {
        foreach (var retired in m_retired) {
            retired.Dispose(node: this);
        }
        foreach (var held in m_held) {
            held.Resource.Dispose();
        }

        m_retired.Clear();
        m_held.Clear();
        m_lastSubmissionFence = null;
        m_firstSubmission = null;
        m_completedSubmission = false;
    }
    // Arms the first-submission watch with the fence of a submission just made, when none is armed and none completed.
    private void NoteSubmitted(IGpuSubmissionFence fence) {
        if (
            !m_completedSubmission &&
            (m_firstSubmission is null)
        ) {
            m_firstSubmission = fence;
        }
    }
    // Records that the node waited on, or found signaled, a slot's fence: when it is the first submission's, that
    // submission has completed.
    private void NoteWaited(IGpuSubmissionFence fence) {
        CompleteRenderFence(fence: fence);

        if (ReferenceEquals(
            objA: fence,
            objB: m_firstSubmission
        )) {
            m_firstSubmission = null;
            m_completedSubmission = true;
        }
    }

    // An owned published allocation or image copy, detached from replaced objects. Leased marks an image another
    // instance owns, held under the node's existing image lease. Buffers never take such a lease.
    private readonly record struct HeldResource(nint Handle, IDisposable Resource, ulong Bytes, bool Leased = false,
        RenderGraphReadEpoch? Epoch = null, ShaderPipelineResourceKind Kind = ShaderPipelineResourceKind.Image);
    // Objects replaced by an install or a selection, or a held allocation no longer published, waiting for the submission
    // that retires them: the fence of the node's latest submission when replaced, or, for a published allocation, the
    // fence of the submission made RetirementLag submissions after it stopped being published.
    private sealed class RetiredGraph(RuntimePass[] passes, RuntimeResource[] resources, PreviewPass? preview, IDisposable? allocation, ulong bytes, IGpuSubmissionFence? fence, long afterSubmission, IGpuBuffer[]? publishedBorrowedBuffers = null) {
        private readonly IGpuBuffer[] m_publishedBorrowedBuffers = publishedBorrowedBuffers ?? [];

        public long AfterSubmission { get; private set; } = afterSubmission;
        public ulong Bytes { get; } = bytes;
        public IGpuSubmissionFence? Fence { get; set; } = fence;
        public bool HoldsBorrowedPublication { get; private set; } = publishedBorrowedBuffers is { Length: > 0 };

        public void ReleasePublication(ShaderPipelineRenderNode node) {
            if (!HoldsBorrowedPublication) { return; }
            var allTransferred = true;
            foreach (var buffer in m_publishedBorrowedBuffers) {
                if (node.InstalledRetainsBorrowedBuffer(buffer)) { continue; }
                allTransferred = false;
                if (node.IsPublishedBuffer(buffer.BufferHandle)) { return; }
            }
            HoldsBorrowedPublication = false;
            AfterSubmission = node.m_submissions + (allTransferred ? 0 : RetirementLag);
            Fence = allTransferred ? node.m_lastSubmissionFence : null;
        }

        public void Dispose(ShaderPipelineRenderNode node) {
            node.DisposeGraph(
                passes: passes,
                resources: resources
            );
            preview?.Dispose();
            allocation?.Dispose();
        }
    }
}
