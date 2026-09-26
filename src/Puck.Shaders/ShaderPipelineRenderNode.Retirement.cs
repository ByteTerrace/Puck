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
// A downstream consumer reads only a surface the node published: the latest one, or, one frame late, the one before it.
// The image behind each of those two surfaces is taken out of the replaced objects and held, alone, while it is still
// one of them. A newer publication displaces it; it then retires once the node's second submission after that has
// completed, because every reader that could still sample it submitted its work before then.
public sealed partial class ShaderPipelineRenderNode {
    // How many of the node's own submissions after an image stops being published precede the one whose completion
    // retires it. A host replacing a whole node retires it by the same count of its successor's submissions.
    internal const long RetirementLag = 2;

    private readonly List<HeldImage> m_held = [];
    private readonly List<RetiredGraph> m_retired = [];

    private IGpuSubmissionFence? m_lastSubmissionFence;
    private Surface m_previousSurface;

    /// <summary>Gets the bytes of every GPU resource the node owns: the installed graph with its float preview, replaced
    /// objects waiting for the GPU to finish with them, published images held from a replaced graph, and the staging
    /// buffer the capture readback holds once a capture has been served.</summary>
    public ulong OwnedBytes {
        get {
            var bytes = checked((AllocationBytes + m_readbackBytes));

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
    private static ulong LiveBytes(RuntimePass[] passes, RuntimeResource[] resources, FloatPreviewPass? preview) {
        var bytes = (preview?.LiveBytes() ?? 0UL);

        foreach (var pass in passes) {
            if (pass?.GeometryBuffer is { } geometry) {
                bytes = checked((bytes + geometry.SizeBytes));
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
            if (resource.Buffers is { } buffers) {
                foreach (var buffer in buffers) {
                    if (buffer is not null) {
                        bytes = checked((bytes + (resource.Spec.SizeBytes ?? 0UL)));
                    }
                }
            }
        }

        return bytes;
    }
    // Takes the image behind a published surface out of replaced objects, when one of them owns it, and holds it.
    private void Hold(nint imageHandle, RuntimeResource[] resources, FloatPreviewPass? preview) {
        if (imageHandle == 0) {
            return;
        }

        foreach (var held in m_held) {
            if (held.Handle == imageHandle) {
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
                        m_held.Add(item: new HeldImage(
                            Bytes: ImageBytes(
                                format: resource.Spec.Format,
                                height: image.Height,
                                width: image.Width
                            ),
                            Handle: imageHandle,
                            Image: image
                        ));

                        return;
                    }
                }
            }
        }

        if (preview?.TakeTarget(imageHandle: imageHandle) is { } previewTarget) {
            m_held.Add(item: new HeldImage(
                Bytes: ((((ulong)previewTarget.Width) * previewTarget.Height) * 4UL),
                Handle: imageHandle,
                Image: previewTarget
            ));
        }
    }
    // Makes a surface the published one. The surface it displaces stays published as the one before it; a held image
    // that is now neither retires once every reader that could still sample it has submitted.
    private void Publish(Surface surface) {
        if (surface.ImageHandle != m_lastSurface.ImageHandle) {
            m_previousSurface = m_lastSurface;
        }

        m_lastSurface = surface;
        // A render publishes, and a reset owes its own initialization frame, so neither is still missing a lost image.
        m_publicationLost = false;

        for (var index = (m_held.Count - 1); (index >= 0); index--) {
            var held = m_held[index];

            if (IsPublished(imageHandle: held.Handle)) {
                continue;
            }

            m_held.RemoveAt(index: index);
            m_retired.Add(item: new RetiredGraph(
                afterSubmission: (m_submissions + RetirementLag),
                bytes: held.Bytes,
                fence: null,
                image: held.Image,
                passes: [],
                preview: null,
                resources: []
            ));
        }
    }
    // Retires objects an install or a selection replaced: the images behind the published surfaces are held, and the
    // rest is disposed once the node's latest submission has completed, which may already be true.
    private void Retire(RuntimePass[] passes, RuntimeResource[] resources, FloatPreviewPass? preview) {
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

        var retired = new RetiredGraph(
            afterSubmission: m_submissions,
            bytes: LiveBytes(
                passes: passes,
                preview: preview,
                resources: resources
            ),
            fence: m_lastSubmissionFence,
            image: null,
            passes: passes,
            preview: preview,
            resources: resources
        );

        if (inFlight) {
            m_retired.Add(item: retired);
        } else {
            retired.Dispose(node: this);
        }
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
            held.Image.Dispose();
        }

        m_retired.Clear();
        m_held.Clear();
        m_lastSubmissionFence = null;
    }

    // An image behind a published surface, taken out of the objects that replaced it.
    private readonly record struct HeldImage(nint Handle, IDisposable Image, ulong Bytes);
    // Objects replaced by an install or a selection, or a held image no longer published, waiting for the submission
    // that retires them: the fence of the node's latest submission when they were replaced, or, for an image, the
    // fence of the submission made RetirementLag submissions after it stopped being published.
    private sealed class RetiredGraph(RuntimePass[] passes, RuntimeResource[] resources, FloatPreviewPass? preview, IDisposable? image, ulong bytes, IGpuSubmissionFence? fence, long afterSubmission) {
        public long AfterSubmission { get; } = afterSubmission;
        public ulong Bytes { get; } = bytes;
        public IGpuSubmissionFence? Fence { get; set; } = fence;

        public void Dispose(ShaderPipelineRenderNode node) {
            node.DisposeGraph(
                passes: passes,
                resources: resources
            );
            preview?.Dispose();
            image?.Dispose();
        }
    }
}
