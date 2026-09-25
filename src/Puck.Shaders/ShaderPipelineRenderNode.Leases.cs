using Puck.Hosting;

namespace Puck.Shaders;

// External images another producer keeps writing. A leased binding serves the one frame it is bound for: the frame
// that records holds its lease (resolve), moves it into the frame slot's list when it submits, and the slot retires the
// list after its fence wait, so the image outlives exactly the submissions that sampled it. A frame that records
// nothing retires the lease at once, and a device loss or disposal retires every list.
public sealed partial class ShaderPipelineRenderNode {
    // This frame's held leases, until the submission that samples them moves them into its slot.
    private readonly LeaseRetireList m_frameLeases = new();
    // One entry per external image ever bound with a lease, reused by every later binding of that name.
    private readonly Dictionary<string, LeasedImage> m_leasedImages = new(comparer: StringComparer.Ordinal);

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

    private sealed class LeasedImage {
        public GpuImageLease Lease;
        // Bound and not yet held by a recorded frame.
        public bool Pending;
        // Held by a recorded frame, so the binding serves no further frame.
        public bool Spent;
    }
}
