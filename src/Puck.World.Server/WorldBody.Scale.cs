using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Resynced wholesale from bodies.scaleRow at the same Install choke points WorldGrants.SyncState is (see
    // WorldPopulation.Scale.cs); never written per tick. One is the inert value every body not named by the row —
    // or every body under a world authoring no scaleRow at all — reads forever.
    private FixedQ4816 m_scale = FixedQ4816.One;

    /// <summary>Gets this body's live scale multiplier — 1 unless <c>bodies.scaleRow</c> names a keyed state row
    /// carrying this body's own cell. Collider volumes, resolved move speed and turn rate, and hold probe/standoff/
    /// reach all read it; presentation multiplies it into the rendered rig independently (the same state row read
    /// client-side).</summary>
    public FixedQ4816 Scale => m_scale;

    /// <summary>Sets this body's live scale multiplier — called only by <c>WorldPopulation.SyncBodyScale</c>'s
    /// resync. A non-positive candidate is refused in favor of <see cref="FixedQ4816.One"/> rather than admitting a
    /// degenerate or inverted body: the declared envelope (bodies.scaleRow's own row Min/Max) is a document-authoring
    /// concern already enforced at write time, so this floor is a last-resort guard against a raw value that could
    /// never have come from a validated cell.</summary>
    /// <param name="value">The candidate scale.</param>
    internal void SetScale(FixedQ4816 value) {
        m_scale = ((value > FixedQ4816.Zero) ? value : FixedQ4816.One);
    }

    // The kit-shared compiled collider's volumes, scaled uniformly about the body root for this body's live Scale —
    // written into the caller's own stackalloc'd scratch span, never onto the heap and never mutated in place
    // (volumes is shared by every body wearing this kit). The common case (Scale == One, every body under a world
    // authoring no scaleRow) returns the shared array unchanged and never touches scratch.
    private ReadOnlySpan<FixedBodyColliderVolume> ScaledColliderVolumes(FixedBodyColliderVolume[] volumes, Span<FixedBodyColliderVolume> scratch) {
        if (m_scale == FixedQ4816.One) {
            return volumes;
        }

        var destination = scratch[..volumes.Length];

        for (var index = 0; (index < volumes.Length); index++) {
            var volume = volumes[index];

            destination[index] = (volume with {
                Center = (volume.Center * m_scale),
                Endpoint = (volume.Endpoint * m_scale),
                HalfExtents = (volume.HalfExtents * m_scale),
                Radius = (volume.Radius * m_scale),
            });
        }

        return destination;
    }
    // The one seat-time turn-rate resolve, mirroring ResolveMoveSpeed: the seated profile's claimed rate, else the
    // kit's own, scaled by this body's live Scale.
    private FixedQ4816 ResolveTurnRate() => ((Profile?.FixedTurnSpeed ?? m_tuning.Turn.Rate) * m_scale);
}
