using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Resynced wholesale from bodies.scaleRow at the same Install choke points WorldGrants.SyncState is (see
    // WorldPopulation.Scale.cs); never written per tick. One is the inert value every body not named by the row —
    // or every body under a world authoring no scaleRow at all — reads forever.
    private FixedQ4816 m_scale = FixedQ4816.One;
    // m_collider's volumes scaled by m_scale, or null while the scale is one and the kit's own volumes serve. Derived
    // wholly from those two fields, so it is rebuilt wherever either is written and never carried by a checkpoint.
    private FixedBodyColliderVolume[]? m_scaledColliderVolumes;
    // The enclosing sphere of the scaled volumes, derived with them in RescaleColliderVolumes, the one place either
    // input is written, so the per-tick broadphase reads it instead of taking three roots per volume per body.
    private FixedQ4816 m_broadphaseRadius;

    /// <summary>Gets this body's live scale multiplier — 1 unless <c>bodies.scaleRow</c> names a keyed state row
    /// carrying this body's own cell. Collider volumes, resolved move speed and turn rate, and hold probe/standoff/
    /// reach all read it; presentation multiplies it into the rendered rig independently (the same state row read
    /// client-side).</summary>
    public FixedQ4816 Scale => m_scale;
    /// <summary>Gets the rotation-independent sphere enclosing <see cref="ScaledColliderVolumes"/>
    /// (<see cref="FixedDynamicBodyContacts.BroadphaseRadius"/>), zero for a body with no collider. It is derived
    /// whenever this body's scale or collider is written and read by every broadphase that pairs bodies.</summary>
    public FixedQ4816 BroadphaseRadius => m_broadphaseRadius;
    /// <summary>Gets how many times this body has derived <see cref="BroadphaseRadius"/>: once per write of its scale
    /// or collider, and never on a tick that writes neither.</summary>
    public long BroadphaseRadiusComputations { get; private set; }

    /// <summary>Sets this body's live scale multiplier — called only by <c>WorldPopulation.SyncBodyScale</c>'s
    /// resync. A non-positive candidate is refused in favor of <see cref="FixedQ4816.One"/> rather than admitting a
    /// degenerate or inverted body: the declared envelope (bodies.scaleRow's own row Min/Max) is a document-authoring
    /// concern already enforced at write time, so this floor is a last-resort guard against a raw value that could
    /// never have come from a validated cell.</summary>
    /// <param name="value">The candidate scale.</param>
    internal void SetScale(FixedQ4816 value) {
        var scale = ((value > FixedQ4816.Zero)
            ? value
            : FixedQ4816.One
        );

        if (scale == m_scale) {
            return;
        }

        m_scale = scale;
        RescaleColliderVolumes();
    }

    /// <summary>Returns the collider's volumes scaled uniformly about the body root for this body's live
    /// <see cref="Scale"/>. The kit-shared compiled volumes are never mutated in place — every body wearing the kit
    /// shares them — so a body at scale one returns them unchanged and any other scale returns this body's own scaled
    /// copy, rebuilt only when its scale or collider changes. Every caller that resolves contact against another
    /// body — dynamic depenetration, overlap events, the cross-boundary continuum trajectory, the adjacency sweep,
    /// and a rigid body's own static-contact sweep — reads THIS, never the kit's raw volumes directly, so a shrunk or
    /// grown body's contact geometry agrees everywhere.</summary>
    /// <returns>The scaled volumes, or empty for a body with no collider.</returns>
    public ReadOnlySpan<FixedBodyColliderVolume> ScaledColliderVolumes() => (m_scaledColliderVolumes ?? m_collider?.Volumes);

    // Rebuilds everything derived from the scale and the collider: the scaled volumes and the broadphase radius.
    // Every write of either input calls it, and nothing else writes what it derives.
    private void RescaleColliderVolumes() {
        if (
            (m_scale == FixedQ4816.One) ||
            (m_collider is not { } collider)
        ) {
            m_scaledColliderVolumes = null;
        } else {
            var volumes = collider.Volumes;
            var scaled = new FixedBodyColliderVolume[volumes.Length];

            for (var index = 0; (index < volumes.Length); index++) {
                var volume = volumes[index];

                scaled[index] = (volume with {
                    Center = (volume.Center * m_scale),
                    Endpoint = (volume.Endpoint * m_scale),
                    HalfExtents = (volume.HalfExtents * m_scale),
                    Radius = (volume.Radius * m_scale),
                });
            }

            m_scaledColliderVolumes = scaled;
        }

        m_broadphaseRadius = FixedDynamicBodyContacts.BroadphaseRadius(volumes: ScaledColliderVolumes());
        BroadphaseRadiusComputations++;
    }
    // The one seat-time turn-rate resolve, mirroring ResolveMoveSpeed: the seated profile's claimed rate, else the
    // kit's own, scaled by this body's live Scale.
    private FixedQ4816 ResolveTurnRate() => ((Profile?.FixedTurnSpeed ?? m_tuning.Turn.Rate) * m_scale);
}
