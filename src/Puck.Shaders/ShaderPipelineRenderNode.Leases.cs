using Puck.Hosting;

namespace Puck.Shaders;

// External images another producer keeps writing. A leased binding serves the one frame it is bound for: the frame
// that records holds its lease (resolve), moves it into the frame slot's list when it submits, and the slot retires the
// list after its fence wait, so the image outlives exactly the submissions that sampled it. A frame that records
// nothing retires the lease at once, and a device loss or disposal retires every list. A binding hold keeps what a
// binding names alive past its producer's retirement, for as long as the installed graph may still sample it.
public sealed partial class ShaderPipelineRenderNode {
    // This frame's held leases, until the submission that samples them moves them into its slot.
    private readonly LeaseRetireList m_frameLeases = new();
    // One entry per external image ever bound with a lease, reused by every later binding of that name.
    private readonly Dictionary<string, LeasedImage> m_leasedImages = new(comparer: StringComparer.Ordinal);
    // Per bound external resource whose producer the host retired while the installed graph still reads it: the host's
    // hold on that producer (HoldBinding).
    private readonly Dictionary<string, GpuImageLease> m_bindingHolds = new(comparer: StringComparer.Ordinal);

    // Resolves every leased binding this frame records against: its lease is held until the frame submits.
    private void HoldLeases() {
        foreach (var leased in m_leasedImages.Values) {
            if (!leased.Pending) {
                continue;
            }

            m_frameLeases.Hold(lease: in leased.Lease);
            leased.Lease = default;
            leased.Pending = false;
            leased.Spent = true;
        }
    }
    // Retires a lease no submission sampled: a frame that recorded nothing, or a binding replaced before any frame.
    private void ReleaseUnheldLeases() {
        m_frameLeases.RetireAll();

        foreach (var leased in m_leasedImages.Values) {
            if (!leased.Pending) {
                continue;
            }

            var lease = leased.Lease;

            leased.Lease = default;
            leased.Pending = false;
            leased.Spent = true;
            lease.Retire();
        }
    }
    // Retires every lease without waiting: after a fence wait on every slot, a device loss, or disposal.
    private void RetireAllLeases() {
        foreach (var slot in m_slots) {
            slot.Leases.RetireAll();
        }

        ReleaseUnheldLeases();
    }
    // A frame about to record refuses a leased image whose lease an earlier frame already spent.
    private void ValidateLeases() {
        foreach (var (name, leased) in m_leasedImages) {
            if (leased.Spent) {
                throw new InvalidDataException(message: $"External image '{name}' was leased for one frame; bind it again before the next.");
            }
        }
    }

    /// <summary>Binds an image another producer keeps writing for a named external resource, for the next produced
    /// frame only. The frame that records holds <paramref name="lease"/> and retires it once that submission's fence
    /// has signaled, on device loss or at disposal; a frame that records nothing retires it at once, and so does a
    /// newer binding of the same name made before any frame recorded. A frame recorded without a newer binding is
    /// refused. The image must have the declared format and may have any extent.</summary>
    /// <param name="name">The name of a declared external image.</param>
    /// <param name="image">The image, in the layout its producer leaves it in, which the frame hands it back in.</param>
    /// <param name="lease">The producer's acquisition of the image; one that requires no retirement binds as
    /// <see cref="BindImage(string, ShaderPipelineExternalImage)"/> does.</param>
    /// <exception cref="ObjectDisposedException">The node is disposed.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty or names no declared external image, or an
    /// image handle is zero.</exception>
    public void BindImage(string name, ShaderPipelineExternalImage image, GpuImageLease lease) {
        BindImage(
            image: image,
            name: name
        );

        if (!lease.RequiresRetirement) {
            return;
        }
        if (!m_leasedImages.TryGetValue(
            key: name,
            value: out var leased
        )) {
            leased = new LeasedImage();
            m_leasedImages.Add(
                key: name,
                value: leased
            );
        }

        leased.Lease = lease;
        leased.Pending = true;
        leased.Spent = false;
    }
    /// <summary>Holds what a named external binding names alive while the installed graph may still sample it: a host
    /// that retired the binding's producer while this node's installed graph still reads it hands the node a lease on
    /// the producer. The node retires the lease once no submission can sample the binding any more: after a newer
    /// binding of the name, or after an install of a graph that declares no external resource of the name, once the
    /// node's latest submission has completed (at once when it has), and without waiting at a device loss or disposal.
    /// A second hold of a name retires the first the same way.</summary>
    /// <param name="name">The name of a bound external resource.</param>
    /// <param name="lease">The host's hold on what the binding names.</param>
    /// <exception cref="ArgumentException"><paramref name="name"/> is empty.</exception>
    public void HoldBinding(string name, GpuImageLease lease) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (m_disposed) {
            lease.Retire();

            return;
        }

        ReleaseBindingHold(name: name);
        m_bindingHolds.Add(
            key: name,
            value: lease
        );
    }

    // Retires the hold on a binding once the node's latest submission, the last that could sample it, has completed.
    private void ReleaseBindingHold(string name) {
        if (!m_bindingHolds.Remove(
            key: name,
            value: out var lease
        )) {
            return;
        }
        if (m_lastSubmissionFence is { IsSignaled: false } fence) {
            m_retired.Add(item: new RetiredGraph(
                afterSubmission: m_submissions,
                bytes: 0UL,
                fence: fence,
                image: new HeldBindingRetirement(lease: lease),
                passes: [],
                preview: null,
                resources: []
            ));
        } else {
            lease.Retire();
        }
    }
    // Retires the hold on every binding the installed graph no longer declares as external.
    private void ReleaseUndeclaredBindingHolds() {
        if (m_bindingHolds.Count == 0) {
            return;
        }

        foreach (var name in m_bindingHolds.Keys.ToArray()) {
            if (!(m_resourceLookup.TryGetValue(
                key: name,
                value: out var resource
            ) && resource.Spec.IsExternal)) {
                ReleaseBindingHold(name: name);
            }
        }
    }
    // Retires every binding hold without waiting: the device has drained or been lost.
    private void RetireBindingHolds() {
        foreach (var lease in m_bindingHolds.Values) {
            lease.Retire();
        }

        m_bindingHolds.Clear();
    }
    // A plain binding of a name that was leased before retires an unheld lease and serves every later frame.
    private void ClearLease(string name) {
        if (!m_leasedImages.TryGetValue(
            key: name,
            value: out var leased
        )) {
            return;
        }

        var lease = leased.Lease;
        var pending = leased.Pending;

        leased.Lease = default;
        leased.Pending = false;
        leased.Spent = false;

        if (pending) {
            lease.Retire();
        }
    }

    // A binding hold waiting for the submission that retires it, as a replaced object's retirement disposes it.
    private sealed class HeldBindingRetirement(GpuImageLease lease) : IDisposable {
        public void Dispose() => lease.Retire();
    }
    private sealed class LeasedImage {
        public GpuImageLease Lease;
        // Bound and not yet held by a recorded frame.
        public bool Pending;
        // Held by a recorded frame, so the binding serves no further frame.
        public bool Spent;
    }
}
