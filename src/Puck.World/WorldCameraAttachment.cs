using Puck.Platform;

namespace Puck.World;

/// <summary>One sensor's live camera attachment, exactly as the probes host needs it: the shared-tier stream and
/// its GPU target handles, the render adapter's LUID, and the open device's physical control surface. Produced only
/// by <see cref="WorldScreenBinder.TryGetCameraAttachment"/> — a read-only snapshot of state the binder itself
/// owns.</summary>
/// <param name="Shared">The sensor's shared-tier stream.</param>
/// <param name="SharedHandles">The consumer-provisioned shared target handles <paramref name="Shared"/> publishes
/// into, in slot order — empty until the stream has started (see <c>ICameraSharedStream.Start</c>).</param>
/// <param name="TargetSet">The shared target ring's identity: reference-stable for the life of one open, and a
/// fresh reference on every reopen (a sensor-set change, a device replug, a device-lost recovery). A consumer that
/// runs work against the targets restarts that work only when this reference changes — it never compares the
/// targets' contents.</param>
/// <param name="AdapterLuid">The render device's adapter LUID, or <see langword="null"/> before the render device
/// resolves (headless, or the first frame before the adapter LUID is known).</param>
/// <param name="Controls">The open device's physical control surface, or <see langword="null"/> while no graph is
/// open. One physical camera carries one control surface shared by every sensor it streams.</param>
public readonly record struct WorldCameraAttachment(
    ICameraSharedStream? Shared,
    IReadOnlyList<nint> SharedHandles,
    object? TargetSet,
    long? AdapterLuid,
    ICameraControlSurface? Controls
);
