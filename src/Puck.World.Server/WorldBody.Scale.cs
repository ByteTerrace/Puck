using Puck.Maths;
using Puck.Physics;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // Resynced from the register bodies.scale names (see WorldPopulation.Scale.cs), never written elsewhere. One is the
    // inert value every body under a world naming no register reads forever.
    private FixedQ4816 m_scale = FixedQ4816.One;
    // m_collider's volumes scaled by m_scale, or null while the scale is one and the kit's own volumes serve. Derived
    // wholly from those two fields, so it is rebuilt wherever either is written and never carried by a checkpoint.
    private FixedBodyColliderVolume[]? m_scaledColliderVolumes;
    // The enclosing sphere of the scaled volumes, derived with them in RescaleColliderVolumes, the one place either
    // input is written, so the per-tick broadphase reads it instead of taking three roots per volume per body.
    private FixedQ4816 m_broadphaseRadius;

    /// <summary>Gets this body's live scale multiplier — 1 unless <c>bodies.scale</c> names a <c>state.body</c> register
    /// carrying this body's size. Collider volumes, resolved move speed, authored launch and impulse velocities,
    /// gravity, engage reach and hold probe/standoff/reach all read it; turn rates do not, an angle being the same at
    /// every size. Presentation multiplies it into the rendered rig independently (the same state row read
    /// client-side).</summary>
    public FixedQ4816 Scale => m_scale;
    /// <summary>Gets the rotation-independent sphere enclosing <see cref="ScaledColliderVolumes"/>
    /// (<see cref="FixedDynamicBodyContacts.BroadphaseRadius"/>), zero for a body with no collider. It is derived
    /// whenever this body's scale or collider is written and read by every broadphase that pairs bodies.</summary>
    public FixedQ4816 BroadphaseRadius => m_broadphaseRadius;
    /// <summary>Gets how many times this body has derived <see cref="BroadphaseRadius"/>: once per write of its scale
    /// or collider, and never on a tick that writes neither.</summary>
    public long BroadphaseRadiusComputations { get; private set; }

    /// <summary>Returns how far this body reaches for an authored engage radius: the radius, in metres, times its live
    /// <see cref="Scale"/>, so a body a quarter its authored size must stand a quarter as far from a screen or a
    /// table to engage it.</summary>
    /// <param name="radius">The authored engage radius, in metres.</param>
    /// <returns>The reach, in metres.</returns>
    public FixedQ4816 EngageReach(double radius) => (FixedQ4816.FromDouble(value: radius) * m_scale);

    // The growth clearance test: how far the grown body is lifted off its floor before it is asked what holds it, so a
    // floor it rests on cannot answer, and the pushes past which the answer refuses the size: a 1/256 m push down its
    // own up (something over it), or a 1/64 m push in all (walls closer than it is wide).
    private static readonly FixedQ4816 ClearanceLift = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 5)));
    private static readonly FixedQ4816 ClearanceDownPush = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 8)));
    private static readonly FixedQ4816 ClearancePushSquared = FixedQ4816.FromRawBits(value: (1L << (FixedQ4816.FractionBitCount - 12)));
    // How many halvings the growth search takes between the size held and the size asked for.
    private const int ClearanceSearchSteps = 4;
    // The candidate volumes the growth search asks about, reused from search to search; never read outside it.
    private FixedBodyColliderVolume[]? m_clearanceVolumes;

    /// <summary>Sets this body's live scale multiplier from its register, holding growth to the room about it. Shrinking
    /// is always admitted. Growth is admitted only as far as the grown body fits where it stands: lifted clear of its
    /// floor, it must take no push down its own up (a ceiling or an overhang over it) and no push past a contact skin's
    /// worth in all (walls closer than it is wide); a lesser size is found by halving the step between the size it holds
    /// and the size asked for. So a body growing under a low ceiling stays the largest size that fits and grows on once
    /// it walks out, never pressed into its floor or thrown clear by the contact solve. A rigid body, a body with no
    /// collider and a body with no contact field take <paramref name="value"/> whole, as <see cref="SetScale"/>
    /// does.</summary>
    /// <param name="value">The size the register asks for.</param>
    /// <returns>The scale the body took: <paramref name="value"/>, or a lesser size its clearance held it to.</returns>
    internal FixedQ4816 SetScaleWithinClearance(FixedQ4816 value) {
        var held = m_scale;

        if (
            (value <= held) ||
            IsRigid ||
            (m_collider is null) ||
            (m_contactField is not { } field) ||
            FitsAtScale(field: field, scale: value)
        ) {
            SetScale(value: value);

            return m_scale;
        }

        var low = held;
        var high = value;

        for (var step = 0; (step < ClearanceSearchSteps); step++) {
            var middle = FixedQ4816.FromRawBits(value: (low.Value + ((high.Value - low.Value) >> 1)));

            if (FitsAtScale(field: field, scale: middle)) {
                low = middle;
            } else {
                high = middle;
            }
        }

        SetScale(value: low);

        return m_scale;
    }
    // Whether this body at a candidate scale fits where it stands, by the growth clearance test above. It asks with
    // the collider's volumes scaled into the search's own scratch, so the body's live scale and volumes stay as they are.
    private bool FitsAtScale(IContactField field, FixedQ4816 scale) {
        var volumes = m_collider!.Value.Volumes;
        var candidate = ((m_clearanceVolumes is { } scratch) && (scratch.Length == volumes.Length))
            ? scratch
            : (m_clearanceVolumes = new FixedBodyColliderVolume[volumes.Length]);

        for (var index = 0; (index < volumes.Length); index++) {
            candidate[index] = volumes[index].Scaled(scale: scale);
        }

        var lifted = (m_position + (m_up * ClearanceLift));
        var resolved = lifted;
        var velocity = FixedVector3.Zero;

        _ = field.Resolve(
            orientation: in m_orientation,
            position: ref resolved,
            up: in m_up,
            velocity: ref velocity,
            volumes: candidate
        );

        var push = (resolved - lifted);

        return (
            (FixedVector3.Dot(left: push, right: m_up) >= -ClearanceDownPush) &&
            (push.LengthSquared <= ClearancePushSquared)
        );
    }

    /// <summary>Sets this body's live scale multiplier — called by <c>WorldPopulation.SyncBodyScale</c>'s resync (through
    /// <see cref="SetScaleWithinClearance"/> for a tick's growth) and a refused step's restore. A non-positive candidate is refused in favor of
    /// <see cref="FixedQ4816.One"/> rather than admitting a degenerate or inverted body: the register's envelope is
    /// enforced at write time, so this floor is a last-resort guard against a raw value that could never have come from a
    /// validated register.</summary>
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
        InvalidateRigidRest();
        RescaleColliderVolumes();
        NotifyPopulationFactChanged();
    }

    /// <summary>Returns the collider's volumes scaled uniformly about the body root for this body's live
    /// <see cref="Scale"/>. The kit-shared compiled volumes are never mutated in place — every body wearing the kit
    /// shares them — so a body at scale one returns them unchanged and any other scale returns this body's own scaled
    /// copy, rebuilt only when its scale or collider changes. Every caller that resolves contact against another
    /// body — dynamic depenetration, overlap events, the cross-boundary continuum trajectory, the adjacency sweep,
    /// and a rigid body's own static-contact sweep — reads THIS, never the kit's raw volumes directly, so a shrunk or
    /// grown body's contact geometry agrees everywhere.</summary>
    /// <returns>The scaled volumes, or empty for a body with no collider.</returns>
    public ReadOnlySpan<FixedBodyColliderVolume> ScaledColliderVolumes() => ((m_scaledColliderVolumes is { } scaled)
        ? scaled
        : ((m_collider is { } collider) ? collider.Volumes : [])
    );

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
            // A body whose size moves every tick (a size field) rewrites its own copy in place: no caller keeps the
            // span ScaledColliderVolumes hands out past the step that asked for it.
            var scaled = ((m_scaledColliderVolumes is { } owned) && (owned.Length == volumes.Length))
                ? owned
                : new FixedBodyColliderVolume[volumes.Length];

            for (var index = 0; (index < volumes.Length); index++) {
                scaled[index] = volumes[index].Scaled(scale: m_scale);
            }

            m_scaledColliderVolumes = scaled;
        }

        m_broadphaseRadius = FixedDynamicBodyContacts.BroadphaseRadius(volumes: ScaledColliderVolumes());
        BroadphaseRadiusComputations++;
    }
    // The one seat-time turn-rate resolve: the seated profile's claimed rate, else the kit's own. An angular rate is
    // the same at every size, so unlike ResolveMoveSpeed it does not read this body's live Scale.
    private FixedQ4816 ResolveTurnRate() => (Profile?.FixedTurnSpeed ?? m_tuning.Turn.Rate);
}
