using Puck.Platform;
using Puck.Platform.Probes;

namespace Puck.World;

/// <summary>One sensor's live camera attachment, exactly as the probes host needs it: the shared-tier stream, the
/// graph that hosts kernels over it, and the open device's physical control surface. Produced only by
/// <see cref="WorldScreenBinder.TryGetCameraAttachment"/> — a read-only snapshot of state the binder itself
/// owns.</summary>
/// <param name="Shared">The sensor's shared-tier stream.</param>
/// <param name="Kernels">The open graph's kernel host, or <see langword="null"/> when the graph hosts none.</param>
/// <param name="TargetSet">The shared target ring's identity: reference-stable for the life of one open, and a
/// fresh reference on every reopen (a sensor-set change, a device replug, a device-lost recovery). A consumer that
/// runs work against the graph restarts that work only when this reference changes — it never compares the
/// targets' contents.</param>
/// <param name="Controls">The open device's physical control surface, or <see langword="null"/> while no graph is
/// open. One physical camera carries one control surface shared by every sensor it streams.</param>
public readonly record struct WorldCameraAttachment(
    ICameraSharedStream? Shared,
    ICameraKernelHost? Kernels,
    object? TargetSet,
    ICameraControlSurface? Controls
);
