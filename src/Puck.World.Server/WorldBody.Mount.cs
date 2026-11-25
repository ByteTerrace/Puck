using Puck.Maths;

namespace Puck.World.Server;

public sealed partial class WorldBody {
    // The population index of the body this one rides, or -1. WorldEngagement.ResolveMounts derives it from the
    // participant's control set and the driven body's kit at the top of every tick, before any body advances, so no
    // checkpoint holds it: a restored authority derives the same value before its first step.
    private int m_mountIndex = -1;

    /// <summary>Gets the population index of the body this body rides, or <see langword="null"/> when it moves on its
    /// own. A riding body's control set leaves its own body out, so <see cref="Advance"/> resolves its intent for the
    /// body it drives and integrates no pose; <see cref="FollowMount"/> gives it the pose and velocity of the body it
    /// rides after that body has moved.</summary>
    public int? MountedOn => ((m_mountIndex >= 0)
        ? m_mountIndex
        : null
    );

    /// <summary>Sets the body this body rides this tick.</summary>
    /// <param name="index">The ridden body's population index, or -1 for none.</param>
    internal void SetMount(int index) => m_mountIndex = index;
    /// <summary>Takes the seat of the body this body rides: its position at <paramref name="facet"/>'s seat in
    /// <paramref name="mount"/>'s frame, and <paramref name="mount"/>'s attitude, up axis and velocity. The move is
    /// continuous motion, so the segment a seam or a door sweeps runs from where the rider was to where it is.</summary>
    /// <param name="mount">The ridden body, already moved this tick.</param>
    /// <param name="facet">The ridden body's mount facet.</param>
    internal void FollowMount(WorldBody mount, in FixedWorldMount facet) {
        WakeUp();
        m_previousPosition = m_position;
        m_position = (mount.m_position + mount.m_orientation.Rotate(vector: (facet.Seat * mount.m_scale)));
        m_orientation = mount.m_orientation;
        m_yaw = mount.FixedYaw;
        m_frame = mount.m_frame;
        m_up = mount.m_up;
        m_planarVelocity = mount.m_planarVelocity;
        m_verticalVelocity = mount.m_verticalVelocity;
        m_ballisticTangent = mount.m_ballisticTangent;
        m_grounded = false;
    }
    /// <summary>Sets the body down beside the body it rode: at <paramref name="facet"/>'s exit in
    /// <paramref name="mount"/>'s frame, facing as <paramref name="mount"/> faces, at rest. It is a teleport, as any
    /// pose is, and the body moves on its own from the next step.</summary>
    /// <param name="mount">The body it rode.</param>
    /// <param name="facet">The ridden body's mount facet.</param>
    internal void LeaveMount(WorldBody mount, in FixedWorldMount facet) {
        m_mountIndex = -1;
        Pose(
            pitchRadians: FixedQ4816.Zero,
            position: (mount.m_position + mount.m_orientation.Rotate(vector: (facet.Exit * mount.m_scale))),
            rollRadians: FixedQ4816.Zero,
            yawRadians: mount.FixedYaw
        );
    }
}
