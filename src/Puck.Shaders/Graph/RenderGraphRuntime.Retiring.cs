using Puck.Abstractions.Gpu;
using Puck.Hosting;

namespace Puck.Shaders;

// Retiring an instance never waits for the device to go idle. One queue runs submissions in order, so once a submission
// made after an instance left the graph has completed, every submission that sampled it (its own, its consumers', the
// display's) has completed too. An instance a reconfiguration retires, and the graph of an instance nothing names any
// more, therefore waits for the latest submission of the first frame that renders after it left: that frame's last
// submitter holds a notification until the submission completes (ShaderPipelineRenderNode.HoldUntilLatestSubmission), and
// the frame after the notification arrives disposes the instance or releases its graph without waiting.
public sealed partial class RenderGraphRuntime {
    // Instances a reconfiguration retired, waiting for a submission ordered after every reader of them.
    private readonly List<Retiring> m_retiring = [];
    // Graphs of instances the schedule left unnamed, by node, waiting for the same.
    private readonly Dictionary<ShaderPipelineRenderNode, Retiring> m_unnamed = new(comparer: ReferenceEqualityComparer.Instance);

    /// <summary>Gets how many instances the runtime has retired or left unnamed whose device objects still wait for a
    /// submission ordered after every reader of them to complete.</summary>
    public int RetiringInstances => (m_retiring.Count + m_unnamed.Count);

    // One instance waiting out its readers: a retired node or external producer, or a node whose graph is released once
    // its readers are done. Submissions is the node's submission count when it left, so a node that renders again
    // before its graph is released keeps it.
    private sealed class Retiring(string instance, ShaderPipelineRenderNode? node, IRenderGraphExternalProducer? producer) {
        public string Instance { get; } = instance;
        public ShaderPipelineRenderNode? Node { get; } = node;
        public IRenderGraphExternalProducer? Producer { get; } = producer;
        public long Submissions { get; } = (node?.SubmissionCount ?? 0L);
        public bool Armed { get; private set; }
        public bool Drained { get; set; }

        public void Arm(ShaderPipelineRenderNode submitter) {
            Armed = true;
            submitter.HoldUntilLatestSubmission(lease: new GpuImageLease(
                ImageViewHandle: 0,
                Release: _ => Drained = true
            ));
        }
    }

    // Queues a retired instance's node and producer for disposal once its readers are done, or disposes them at once when
    // no submission is in flight; a queued node first hands over the renders it has completed and stays polled for the
    // rest, as a held one does (RetireNode).
    private void QueueRetired(string instance, ShaderPipelineRenderNode? node, IRenderGraphExternalProducer? producer) {
        var retiring = new Retiring(instance: instance, node: node, producer: producer);

        if (NothingInFlight()) {
            DisposeRetiring(retiring: retiring);

            return;
        }
        if (node is not null) {
            RetireNode(held: true, instance: instance, node: node);
        }

        m_retiring.Add(item: retiring);
    }
    // Whether every submission any node made has completed, so nothing still reads what an instance leaves behind. One
    // queue completes submissions in order, so a node whose latest submission completed has completed every earlier one.
    private bool NothingInFlight() {
        foreach (var node in m_nodes) {
            if (node?.LatestSubmission is { IsSignaled: false }) {
                return false;
            }
        }
        foreach (var retiring in m_retiring) {
            if (retiring.Node?.LatestSubmission is { IsSignaled: false }) {
                return false;
            }
        }

        return true;
    }
    // Retires the leases every node's completed submissions held, so an image whose readers have all finished goes
    // without waiting for those readers to submit again.
    private void RetireCompletedLeases() {
        foreach (var node in m_nodes) {
            node?.RetireCompletedLeases();
        }
        foreach (var retiring in m_retiring) {
            retiring.Node?.RetireCompletedLeases();
        }
    }
    // Marks every waiting instance drained once nothing is in flight, as when no node has submitted since they left.
    private void DrainWhenIdle() {
        if (
            (RetiringInstances == 0) ||
            !NothingInFlight()
        ) {
            return;
        }

        foreach (var retiring in m_retiring) {
            retiring.Drained = true;
        }
        foreach (var retiring in m_unnamed.Values) {
            retiring.Drained = true;
        }
    }
    // Arms every waiting instance with the frame's last submission, which the host made after they left.
    private void ArmRetiring(ShaderPipelineRenderNode? submitter) {
        if (submitter is null) {
            return;
        }

        foreach (var retiring in m_retiring) {
            if (!retiring.Armed) {
                retiring.Arm(submitter: submitter);
            }
        }
        foreach (var retiring in m_unnamed.Values) {
            if (!retiring.Armed) {
                retiring.Arm(submitter: submitter);
            }
        }
    }
    // Disposes every retired instance whose readers are done. Every disposal is attempted, and the failures are thrown
    // together after the last.
    private void DisposeDrainedRetiring() {
        List<Exception>? failures = null;

        RetireCompletedLeases();
        DrainWhenIdle();

        for (var position = (m_retiring.Count - 1); (position >= 0); position--) {
            var retiring = m_retiring[position];

            if (!retiring.Drained) {
                continue;
            }

            m_retiring.RemoveAt(index: position);

            try {
                DisposeRetiring(retiring: retiring);
            } catch (Exception error) {
                (failures ??= []).Add(item: error);
            }
        }

        if (failures is not null) {
            throw new AggregateException(
                innerExceptions: failures,
                message: "Disposing a retired render-graph instance failed."
            );
        }
    }
    private void DisposeRetiring(Retiring retiring) {
        if (retiring.Node is { } node) {
            DisposeHeld(instance: retiring.Instance, node: node);
        }

        retiring.Producer?.Dispose();
    }
    // Releases every waiting instance at once: the device is lost, so nothing it ran will complete, or the runtime is
    // being disposed, which waits for the device first. The unnamed graphs belong to nodes the caller releases.
    private void ReleaseRetiring(bool lost) {
        if (!lost && (m_retiring.Count != 0)) {
            m_device.TryWaitIdle();
        }

        foreach (var retiring in m_retiring) {
            if (lost) {
                retiring.Node?.OnDeviceLost();
                retiring.Producer?.OnDeviceLost();
            }

            DisposeRetiring(retiring: retiring);
        }

        m_retiring.Clear();
        m_unnamed.Clear();
    }
}
