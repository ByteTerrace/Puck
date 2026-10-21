using Puck.Abstractions.Gpu;
using Puck.Abstractions.Presentation;

namespace Puck.Shaders;

// The presented capture (FrameCaptureMode.Presented): what the display showed, frame after frame. It is armed on the
// runtime and never forwarded, so no node serves it from inside its render and nothing holds or settles for it: no
// convergence begins, no package holds its residency's frame, the frame context keeps its presentation time, and the
// host's clock runs on. After each frame's renders, the runtime reads back the image it hands the display for the
// captured instance, the instance's latest output resolved through what it stands for, which is what the frame
// presented whether or not the instance rendered it this frame. A frame on which that image would not be served to a
// settled capture (no completed output, a stand-in, a resize building, external content the capture gate did not fill)
// is skipped, and the burst's presentation indices show the gap. While it is armed the capture is pending like any
// other, so the gate fills external content and a capture frame renders every tainted instance again.
public sealed partial class RenderGraphRuntime {
    private FrameCaptureRequest? m_presented;
    // The presentations a presented capture still skips while the history it had discarded converges again.
    private int m_presentedWarmup;

    /// <summary>Gets whether a settled capture (<see cref="FrameCaptureMode.Settled"/>) is armed on the runtime or
    /// forwarded and not yet served: the capture that holds presentation, and that a host pins its presented state to
    /// the completed tick for. A presented capture holds nothing.</summary>
    public bool HoldsPresentation => (SettledCapturePath is not null);

    private string? PresentedPath => ((m_presented is { Completion.IsCompleted: false } request)
        ? request.Path
        : null);

    // Arms a presented capture of one instance in place of the runtime's slot.
    private void ArmPresented(int index, FrameCaptureRequest request, bool followsRoot) {
        if (
            (PendingCapturePath is not null) ||
            request.Completion.IsCompleted
        ) {
            throw new InvalidOperationException(message: "A capture is already pending or the request is terminal.");
        }
        if (m_producers[index] is not null) {
            throw new InvalidOperationException(message: $"A presented capture reads a rendered graph instance; '{m_set.Instances[index].Name}' is an external producer.");
        }

        m_presented = request;
        m_presentedWarmup = 0;
        m_captureInstance = index;
        m_captureFollowsRoot = followsRoot;
    }
    // On a capture frame, before anything renders, for an instance the pending capture reads: has each of its packages
    // discard retained state holding unfilled external pixels (IRenderGraphPackageFactory.DiscardTaintedState), which
    // would otherwise keep every output it reaches tainted for as long as the capture is armed, settled or presented. A
    // presented capture then skips the presentations the state needs to converge again, so the burst reads the steady
    // image rather than its reconvergence. A presented capture that holds every frame discards nothing more.
    private void DiscardCaptureTaint(int index) {
        if ((m_presented is { Claimed: true }) || (m_graphs[index] is not { } graph)) {
            return;
        }

        var name = m_set.Instances[index].Name;
        var passes = graph.Pipeline.Plan.Passes;
        var settling = 0;

        for (var position = 0; (position < passes.Count); position++) {
            if ((passes[position].Package is { } step) && m_packages.TryGetFactory(step.Package, out var factory)) {
                settling = Math.Max(val1: settling, val2: factory.DiscardTaintedState(instance: name));
            }
        }

        m_presentedWarmup = ((m_presented is null) ? 0 : Math.Max(val1: m_presentedWarmup, val2: settling));
    }
    // Refuses the presented capture still armed, if any.
    private void RefusePresented(Exception error) {
        _ = m_presented?.TryFail(error: error);
        m_presented = null;
    }
    // Serves the presented capture from the frame just produced: the image the display presents for its instance.
    private void ServePresented(long presentation) {
        if (m_presented is not { } request) {
            return;
        }
        // A request that holds every frame stays pending until its last PNG is written; a withdrawn one is dropped.
        if (request.Claimed) {
            m_presented = (request.Completion.IsCompleted ? null : request);

            return;
        }

        var index = m_captureInstance;

        if (m_nodes[index] is not { } node) {
            RefusePresented(error: new InvalidOperationException(message: $"A presented capture reads a rendered graph instance; '{m_set.Instances[index].Name}' is an external producer."));

            return;
        }
        // Skipped where a settled capture would wait, and where the shown image, which may stand for another instance's,
        // read external content the gate did not fill.
        if ((ReasonOf(index: index) is not null) || (LatestOf(index: index) is not { Tainted: false } output)) {
            return;
        }
        if (m_presentedWarmup > 0) {
            m_presentedWarmup--;

            return;
        }

        try {
            if (!node.TryReadPresented(
                image: output.Image,
                layout: output.Layout,
                rgba: out var rgba
            )) {
                return;
            }

            _ = request.Present(
                encode: node.EncodePresented,
                height: ((int)output.Image.Height),
                presentation: presentation,
                rendered: output.Frame,
                rgba: rgba,
                tick: output.StateTick,
                width: ((int)output.Image.Width)
            );
        } catch (DeviceLostException exception) {
            RefusePresented(error: exception);

            throw;
        } catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException) {
            Console.Error.WriteLine(value: $"[capture] failed -> {request.Path} ({exception.Message})");
            RefusePresented(error: exception);
        }
    }
}
