using Puck.Abstractions.Presentation;
using Puck.Hosting;

namespace Puck.Shaders;

// Image lifetime. Every image an instance's node creates is one of the runtime's GpuImageLeases, and every reader the
// runtime hands one to holds a lease that names that reader's completion: a consumer node's binding and a package's or
// an external producer's read retire theirs once the submission that sampled the image has finished, and the display's
// lease retires once a submission made after the host presented the image has. An image whose owner drops it (a graph
// released because nothing names it, retired in a reconfiguration, or replaced) is disposed only once every lease on it
// has retired. Whose image it is does not matter to a reader: an output standing for another instance's image (a pass
// that drew nothing) is leased from the table like the owner's own, however long the chain it resolves through.
public sealed partial class RenderGraphRuntime {
    // The leases of images handed to the display, waiting for a submission made after the host presented them.
    private readonly LeaseRetireList m_shown = new();

    // A reader's lease on an image: from the table when one of the runtime's nodes created it, otherwise the image's view
    // alone, which needs no retirement (a stand-in, which lives until the device is lost or the runtime disposed).
    private GpuImageLease LeaseOf(Surface image) => (m_images.TryLease(
        imageHandle: image.ImageHandle,
        lease: out var lease
    )
        ? lease
        : image.ImageViewHandle);
    // Retires every lease a finished submission held, once the device has drained: each node's, and the display's, whose
    // host presented them before this frame began. An image an owner then drops is disposed with the drain's guarantee
    // rather than a few frames later.
    private void RetireDrainedLeases() {
        foreach (var node in m_nodes) {
            node?.RetireDrainedLeases();
        }

        m_shown.RetireAll();
    }
    // Hands the display the root's image under a lease. The host presents it in a submission before its next call, so the
    // leases of earlier frames' images move to the latest submission made this frame, which follows that presentation on
    // the one queue; with no submission this frame they keep waiting.
    private Surface Shown(Surface image, ShaderPipelineRenderNode? submitter) {
        if (submitter is not null) {
            submitter.HoldUntilLatestSubmission(leases: m_shown);
        }

        m_shown.Hold(lease: LeaseOf(image: image));

        return image;
    }
}
